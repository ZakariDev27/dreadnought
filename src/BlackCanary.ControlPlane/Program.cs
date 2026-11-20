using BlackCanary.ControlPlane;
using BlackCanary.Core.Audit;
using BlackCanary.Core.Config;
using BlackCanary.Core.Fleet;
using BlackCanary.Core.Rollouts;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

var options = builder.Configuration.GetSection(ControlPlaneOptions.Section).Get<ControlPlaneOptions>()
    ?? new ControlPlaneOptions();

// Refuse to serve a fleet definition that would not pass review.
var fleet = FleetRepository.Load(options.FleetPath);
var issues = BaselineValidator.Validate(fleet);
if (issues.Count > 0)
{
    throw new InvalidOperationException(
        $"Fleet definition at '{options.FleetPath}' is invalid:{Environment.NewLine}{string.Join(Environment.NewLine, issues)}");
}

var keys = new ApiKeyDirectory(options.ApiKeys);
if (keys.Count == 0)
{
    throw new InvalidOperationException("No API keys configured (BlackCanary:ApiKeys). Refusing to start open.");
}

builder.Services.ConfigureHttpJsonOptions(o => FleetJson.Configure(o.SerializerOptions));
builder.Services.AddProblemDetails();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(fleet);
builder.Services.AddSingleton(keys);
builder.Services.AddSingleton(new ApprovalPolicy(options.StandardApprovals, options.SensitiveApprovals));
builder.Services.AddSingleton<AuditLog>();
builder.Services.AddSingleton<IRolloutStore, InMemoryRolloutStore>();
builder.Services.AddSingleton<RolloutService>();
builder.Services.AddSingleton<FleetService>();

var app = builder.Build();

// Domain failures are client-visible conditions, not 500s.
app.Use(async (http, next) =>
{
    try
    {
        await next(http);
    }
    catch (Exception ex) when (StatusFor(ex) is { } status)
    {
        http.Response.StatusCode = status;
        await http.Response.WriteAsJsonAsync(
            new ProblemDetails { Status = status, Title = ex.GetType().Name.Replace("Exception", ""), Detail = ex.Message },
            options: null,
            contentType: "application/problem+json");
    }
});

app.MapGet("/healthz", (FleetDefinition f) => Results.Ok(new { status = "ok", baselines = f.Baselines.Count }));

// ---- Fleet ----------------------------------------------------------------

app.MapPut("/v1/devices/{id}", (string id, CheckInRequest body, FleetService fleetService, TimeProvider clock) =>
    {
        var device = new Device(
            id, body.Platform, body.Tier, body.OsVersion,
            body.ObservedSettings ?? new Dictionary<string, string>(), clock.GetUtcNow());
        return Results.Ok(fleetService.CheckIn(device));
    })
    .RequireScope(Scopes.DeviceCheckIn);

app.MapGet("/v1/devices/{id}/drift", (string id, FleetService fleetService) =>
        fleetService.Drift(id) is { } report ? Results.Ok(report) : Results.NotFound())
    .RequireScope(Scopes.FleetRead);

app.MapGet("/v1/fleet/compliance", (FleetService fleetService) => fleetService.Summarise())
    .RequireScope(Scopes.FleetRead);

// ---- Rollouts -------------------------------------------------------------

var rollouts = app.MapGroup("/v1/rollouts");

rollouts.MapGet("/", (RolloutService service) => service.List()).RequireScope(Scopes.FleetRead);

rollouts.MapGet("/{id}", (string id, RolloutService service) => service.Get(id)).RequireScope(Scopes.FleetRead);

rollouts.MapPost("/", (CreateRolloutRequest body, RolloutService service, HttpContext http) =>
    {
        var created = service.Create(body.Id, body.Baseline, body.Version, http.Actor());
        return Results.Created($"/v1/rollouts/{created.Id}", created);
    })
    .RequireScope(Scopes.RolloutWrite);

rollouts.MapPost("/{id}/approvals", (string id, RolloutService service, HttpContext http) =>
        service.Approve(id, http.Actor()))
    .RequireScope(Scopes.RolloutApprove);

rollouts.MapPost("/{id}/start", (string id, RolloutService service, HttpContext http) =>
        service.Start(id, http.Actor()))
    .RequireScope(Scopes.RolloutWrite);

rollouts.MapPost("/{id}/verdicts", (string id, Verdict verdict, RolloutService service, HttpContext http) =>
        service.RecordVerdict(id, verdict, http.Actor()))
    .RequireScope(Scopes.VerdictWrite);

rollouts.MapPost("/{id}/advance", (string id, RolloutService service, HttpContext http) =>
        service.Advance(id, http.Actor()))
    .RequireScope(Scopes.RolloutWrite);

rollouts.MapPost("/{id}/rollback", (string id, RollbackRequest body, RolloutService service, HttpContext http) =>
        service.Rollback(id, http.Actor(), body.Reason))
    .RequireScope(Scopes.RolloutWrite);

// Agents ask this to learn whether a change has reached them yet.
rollouts.MapGet("/{id}/targets/{deviceId}", (string id, string deviceId, RolloutService service) =>
    {
        var rollout = service.Get(id);
        var ring = RingAssigner.RingIndexFor(rollout.Plan, deviceId, rollout.ChangeId);
        var targeted = rollout.State is RolloutState.InProgress or RolloutState.Completed
            && RingAssigner.IsTargeted(rollout.Plan, rollout.RingIndex, deviceId, rollout.ChangeId);
        return Results.Ok(new { deviceId, ring = rollout.Plan.Rings[ring].Name, targeted });
    })
    .RequireScope(Scopes.FleetRead);

// ---- Audit ----------------------------------------------------------------

app.MapGet("/v1/audit", (AuditLog audit) => audit.Snapshot()).RequireScope(Scopes.AuditRead);

app.MapGet("/v1/audit/verify", (AuditLog audit) =>
    {
        var entries = audit.Snapshot();
        var brokenAt = AuditLog.Verify(entries);
        return Results.Ok(new { entries = entries.Count, intact = brokenAt is null, brokenAt });
    })
    .RequireScope(Scopes.AuditRead);

app.Run();

static int? StatusFor(Exception ex) => ex switch
{
    RolloutNotFoundException => StatusCodes.Status404NotFound,
    InvalidTransitionException or RolloutConflictException => StatusCodes.Status409Conflict,
    ArgumentException => StatusCodes.Status400BadRequest,
    _ => null,
};

internal sealed record CheckInRequest(
    Platform Platform,
    ComplianceTier Tier,
    string OsVersion,
    Dictionary<string, string>? ObservedSettings);

internal sealed record CreateRolloutRequest(string Id, string Baseline, int Version);

internal sealed record RollbackRequest(string Reason);

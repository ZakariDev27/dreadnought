using System.Security.Cryptography;
using System.Text;

namespace Dreadnought.ControlPlane;

/// <summary>Least-privilege scopes. A caller gets only what its job needs.</summary>
public static class Scopes
{
    public const string FleetRead = "fleet:read";
    public const string DeviceCheckIn = "device:checkin";
    public const string RolloutWrite = "rollout:write";
    public const string RolloutApprove = "rollout:approve";
    public const string VerdictWrite = "verdict:write";
    public const string AuditRead = "audit:read";
}

public sealed class ApiKeyEntry
{
    public string Key { get; set; } = "";
    public string Actor { get; set; } = "";
    public List<string> Scopes { get; set; } = [];
}

public sealed class ControlPlaneOptions
{
    public const string Section = "Dreadnought";

    public string FleetPath { get; set; } = "fleet";
    public int StandardApprovals { get; set; } = 1;
    public int SensitiveApprovals { get; set; } = 2;
    public List<ApiKeyEntry> ApiKeys { get; set; } = [];
}

public sealed record Caller(string Actor, IReadOnlySet<string> Scopes);

/// <summary>Resolves an API key to a caller without leaking key material through timing.</summary>
public sealed class ApiKeyDirectory
{
    private readonly List<(byte[] Digest, Caller Caller)> _callers;

    public ApiKeyDirectory(IEnumerable<ApiKeyEntry> entries)
    {
        _callers = entries
            .Where(e => !string.IsNullOrWhiteSpace(e.Key) && !string.IsNullOrWhiteSpace(e.Actor))
            .Select(e => (Digest(e.Key), new Caller(e.Actor, e.Scopes.ToHashSet(StringComparer.Ordinal))))
            .ToList();
    }

    public int Count => _callers.Count;

    public Caller? Resolve(string? presented)
    {
        if (string.IsNullOrEmpty(presented))
        {
            return null;
        }

        var digest = Digest(presented);
        Caller? match = null;

        // Compare against every key so the response time does not reveal which one matched.
        foreach (var (candidate, caller) in _callers)
        {
            if (CryptographicOperations.FixedTimeEquals(candidate, digest))
            {
                match = caller;
            }
        }

        return match;
    }

    private static byte[] Digest(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));
}

public static class ScopeEndpointExtensions
{
    private const string CallerItem = "dreadnought.caller";
    public const string HeaderName = "X-Api-Key";

    /// <summary>Rejects the request unless the caller's key carries <paramref name="scope"/>.</summary>
    public static RouteHandlerBuilder RequireScope(this RouteHandlerBuilder builder, string scope) =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var directory = http.RequestServices.GetRequiredService<ApiKeyDirectory>();
            var caller = directory.Resolve(http.Request.Headers[HeaderName].FirstOrDefault());

            if (caller is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Missing or unknown API key.");
            }

            if (!caller.Scopes.Contains(scope))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Insufficient scope.",
                    detail: $"This operation requires '{scope}'.");
            }

            http.Items[CallerItem] = caller;
            return await next(context);
        });

    public static string Actor(this HttpContext http) =>
        ((Caller)http.Items[CallerItem]!).Actor;
}

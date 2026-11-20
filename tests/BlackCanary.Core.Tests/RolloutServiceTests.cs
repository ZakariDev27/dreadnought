using BlackCanary.Core.Audit;
using BlackCanary.Core.Config;
using BlackCanary.Core.Rollouts;

namespace BlackCanary.Core.Tests;

public class RolloutServiceTests
{
    private readonly ManualClock _clock = new(TestData.T0);
    private readonly AuditLog _audit;
    private readonly RolloutService _service;

    public RolloutServiceTests()
    {
        _audit = new AuditLog(_clock);
        var fleet = new FleetDefinition([TestData.MacCore(3)], TestData.TwoRings);
        _service = new RolloutService(new InMemoryRolloutStore(), _audit, fleet, new ApprovalPolicy(), _clock);
    }

    [Fact]
    public void Sensitivity_is_derived_from_the_baseline_not_the_caller()
    {
        var rollout = _service.Create("r-1", "macos-core", 3, "ada");

        Assert.True(rollout.Sensitive);
        Assert.Equal(RolloutState.AwaitingApproval, rollout.State);
    }

    [Fact]
    public void Unknown_baselines_and_duplicate_ids_are_rejected()
    {
        _service.Create("r-1", "macos-core", 3, "ada");

        Assert.Throws<ArgumentException>(() => _service.Create("r-2", "macos-core", 99, "ada"));
        Assert.Throws<RolloutConflictException>(() => _service.Create("r-1", "macos-core", 3, "ada"));
        Assert.Throws<RolloutNotFoundException>(() => _service.Get("nope"));
    }

    [Fact]
    public void Full_lifecycle_is_audited_and_the_chain_verifies()
    {
        _service.Create("r-1", "macos-core", 3, "ada");
        _service.Approve("r-1", "grace");
        _service.Approve("r-1", "linus");
        _service.Start("r-1", "ada");

        _clock.Advance(TimeSpan.FromHours(1));
        _service.RecordVerdict("r-1", TestData.Verdict("canary", VerdictStatus.Healthy, _clock.GetUtcNow()), "analytics");
        _service.Advance("r-1", "ada");
        _service.RecordVerdict("r-1", TestData.Verdict("fleet", VerdictStatus.Healthy, _clock.GetUtcNow()), "analytics");
        var done = _service.Advance("r-1", "ada");

        Assert.Equal(RolloutState.Completed, done.State);

        var entries = _audit.Snapshot();
        Assert.Equal(
            new[]
            {
                "rollout.created", "rollout.approved", "rollout.approved", "rollout.started",
                "rollout.verdict", "rollout.advanced", "rollout.verdict", "rollout.advanced",
            },
            entries.Select(e => e.Action).ToArray());
        Assert.Null(AuditLog.Verify(entries));
    }

    [Fact]
    public void Automatic_rollback_is_attributed_to_the_system()
    {
        _service.Create("r-1", "macos-core", 3, "ada");
        _service.Approve("r-1", "grace");
        _service.Approve("r-1", "linus");
        _service.Start("r-1", "ada");

        var result = _service.RecordVerdict(
            "r-1", TestData.Verdict("canary", VerdictStatus.Unhealthy, _clock.GetUtcNow()), "analytics");

        Assert.Equal(RolloutState.RolledBack, result.State);
        var last = _audit.Snapshot()[^1];
        Assert.Equal("rollout.rolled_back", last.Action);
        Assert.Equal("black-canary", last.Actor);
    }

    [Fact]
    public void Rejected_transitions_leave_no_audit_entry()
    {
        _service.Create("r-1", "macos-core", 3, "ada");
        var before = _audit.Snapshot().Count;

        Assert.Throws<InvalidTransitionException>(() => _service.Approve("r-1", "ada"));

        Assert.Equal(before, _audit.Snapshot().Count);
    }
}

using BlackCanary.Core.Rollouts;

namespace BlackCanary.Core.Tests;

public class RolloutTests
{
    private static readonly ApprovalPolicy Policy = new(StandardApprovals: 1, SensitiveApprovals: 2);
    private static readonly DateTimeOffset T0 = TestData.T0;

    private static Rollout Draft(bool sensitive = false) =>
        Rollout.Create("r-1", "macos-core@3", "ada", sensitive, TestData.TwoRings);

    private static Rollout Started(bool sensitive = false)
    {
        var rollout = Draft(sensitive).Submit(Policy).Approve("grace", Policy);
        if (sensitive)
        {
            rollout = rollout.Approve("linus", Policy);
        }

        return rollout.Start(T0);
    }

    [Fact]
    public void Happy_path_walks_every_ring_to_completion()
    {
        var rollout = Started();
        Assert.Equal("canary", rollout.CurrentRing!.Name);

        rollout = rollout
            .RecordVerdict(TestData.Verdict("canary", VerdictStatus.Healthy, T0.AddMinutes(50)))
            .Advance(T0.AddHours(1));
        Assert.Equal("fleet", rollout.CurrentRing!.Name);
        Assert.Null(rollout.LastVerdict);

        rollout = rollout
            .RecordVerdict(TestData.Verdict("fleet", VerdictStatus.Healthy, T0.AddHours(2)))
            .Advance(T0.AddHours(2));

        Assert.Equal(RolloutState.Completed, rollout.State);
    }

    [Fact]
    public void Authors_cannot_approve_their_own_rollout()
    {
        var submitted = Draft().Submit(Policy);

        var ex = Assert.Throws<InvalidTransitionException>(() => submitted.Approve("ADA", Policy));
        Assert.Contains("own rollout", ex.Message);
    }

    [Fact]
    public void Sensitive_changes_need_two_distinct_approvers()
    {
        var once = Draft(sensitive: true).Submit(Policy).Approve("grace", Policy);

        Assert.Equal(RolloutState.AwaitingApproval, once.State);
        Assert.Throws<InvalidTransitionException>(() => once.Approve("grace", Policy));
        Assert.Throws<InvalidTransitionException>(() => once.Start(T0));
        Assert.Equal(RolloutState.Approved, once.Approve("linus", Policy).State);
    }

    [Fact]
    public void A_policy_requiring_no_approvals_skips_the_queue()
    {
        var rollout = Draft().Submit(new ApprovalPolicy(StandardApprovals: 0));

        Assert.Equal(RolloutState.Approved, rollout.State);
    }

    [Fact]
    public void Cannot_advance_without_a_healthy_verdict()
    {
        var rollout = Started();

        Assert.Throws<InvalidTransitionException>(() => rollout.Advance(T0.AddHours(5)));

        var inconclusive = rollout.RecordVerdict(TestData.Verdict("canary", VerdictStatus.Inconclusive, T0.AddHours(2)));
        Assert.Throws<InvalidTransitionException>(() => inconclusive.Advance(T0.AddHours(5)));
    }

    [Fact]
    public void Cannot_advance_before_the_ring_has_soaked()
    {
        var rollout = Started().RecordVerdict(TestData.Verdict("canary", VerdictStatus.Healthy, T0.AddMinutes(10)));

        var ex = Assert.Throws<InvalidTransitionException>(() => rollout.Advance(T0.AddMinutes(59)));
        Assert.Contains("soak", ex.Message);
    }

    [Fact]
    public void An_unhealthy_verdict_rolls_back_automatically()
    {
        var rollout = Started().RecordVerdict(TestData.Verdict("canary", VerdictStatus.Unhealthy, T0.AddMinutes(20)));

        Assert.Equal(RolloutState.RolledBack, rollout.State);
        Assert.Contains("crash rate regressed", rollout.RollbackReason);
        Assert.Throws<InvalidTransitionException>(() => rollout.Advance(T0.AddHours(3)));
    }

    [Fact]
    public void Verdicts_for_the_wrong_ring_or_rollout_are_rejected()
    {
        var rollout = Started();

        Assert.Throws<InvalidTransitionException>(() =>
            rollout.RecordVerdict(TestData.Verdict("fleet", VerdictStatus.Healthy, T0.AddMinutes(5))));
        Assert.Throws<InvalidTransitionException>(() =>
            rollout.RecordVerdict(TestData.Verdict("canary", VerdictStatus.Healthy, T0.AddMinutes(5), rolloutId: "other")));
    }

    [Fact]
    public void A_stale_verdict_cannot_vouch_for_a_new_ring()
    {
        var rollout = Started();

        Assert.Throws<InvalidTransitionException>(() =>
            rollout.RecordVerdict(TestData.Verdict("canary", VerdictStatus.Healthy, T0.AddMinutes(-1))));
    }

    [Fact]
    public void Terminal_states_accept_no_further_transitions()
    {
        var rolledBack = Started().Rollback("operator abort");

        Assert.Throws<InvalidTransitionException>(() => rolledBack.Rollback("again"));
        Assert.Throws<InvalidTransitionException>(() => rolledBack.Start(T0));
    }

    [Fact]
    public void An_invalid_plan_cannot_produce_a_rollout()
    {
        var plan = new RolloutPlan([new Ring("only", 40, TimeSpan.Zero)]);

        Assert.Throws<ArgumentException>(() => Rollout.Create("r-1", "c@1", "ada", false, plan));
    }
}

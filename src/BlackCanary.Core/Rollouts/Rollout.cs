namespace BlackCanary.Core.Rollouts;

public enum RolloutState
{
    Draft,
    AwaitingApproval,
    Approved,
    InProgress,
    Completed,
    RolledBack,
}

/// <summary>How many humans must sign off before a change may start.</summary>
public sealed record ApprovalPolicy(int StandardApprovals = 1, int SensitiveApprovals = 2)
{
    public int RequiredFor(Rollout rollout) =>
        rollout.Sensitive ? SensitiveApprovals : StandardApprovals;
}

public sealed class InvalidTransitionException(string message) : Exception(message);

/// <summary>
/// A change moving through the fleet ring by ring.
///
/// Immutable: every transition returns a new value or throws
/// <see cref="InvalidTransitionException"/>. Time is passed in rather than read, which
/// keeps the state machine a pure function and makes every rule testable without a clock.
/// </summary>
public sealed record Rollout
{
    public required string Id { get; init; }
    public required string ChangeId { get; init; }
    public required string Author { get; init; }
    public required bool Sensitive { get; init; }
    public required RolloutPlan Plan { get; init; }

    public RolloutState State { get; init; } = RolloutState.Draft;
    public IReadOnlyList<string> Approvers { get; init; } = [];

    /// <summary>Index into <see cref="RolloutPlan.Rings"/>; -1 until the rollout starts.</summary>
    public int RingIndex { get; init; } = -1;
    public DateTimeOffset? RingEnteredAt { get; init; }
    public Verdict? LastVerdict { get; init; }
    public string? RollbackReason { get; init; }

    public Ring? CurrentRing => RingIndex >= 0 ? Plan.Rings[RingIndex] : null;

    public static Rollout Create(string id, string changeId, string author, bool sensitive, RolloutPlan plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(changeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(author);

        var problems = plan.Validate();
        if (problems.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", problems), nameof(plan));
        }

        return new Rollout { Id = id, ChangeId = changeId, Author = author, Sensitive = sensitive, Plan = plan };
    }

    public Rollout Submit(ApprovalPolicy policy)
    {
        Require(RolloutState.Draft, "submit");
        return this with
        {
            State = policy.RequiredFor(this) > 0 ? RolloutState.AwaitingApproval : RolloutState.Approved,
        };
    }

    public Rollout Approve(string approver, ApprovalPolicy policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approver);
        Require(RolloutState.AwaitingApproval, "approve");

        // Separation of duties: nobody ships their own change on their own say-so.
        if (string.Equals(approver, Author, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidTransitionException("Authors cannot approve their own rollout.");
        }

        if (Approvers.Contains(approver, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidTransitionException($"'{approver}' has already approved this rollout.");
        }

        IReadOnlyList<string> approvers = [.. Approvers, approver];
        return this with
        {
            Approvers = approvers,
            State = approvers.Count >= policy.RequiredFor(this) ? RolloutState.Approved : State,
        };
    }

    public Rollout Start(DateTimeOffset now)
    {
        Require(RolloutState.Approved, "start");
        return this with { State = RolloutState.InProgress, RingIndex = 0, RingEnteredAt = now };
    }

    /// <summary>
    /// Records the health gate's answer for the current ring. An unhealthy verdict
    /// rolls the change back immediately; no human needs to be awake for that.
    /// </summary>
    public Rollout RecordVerdict(Verdict verdict)
    {
        Require(RolloutState.InProgress, "record a verdict on");

        if (verdict.RolloutId != Id)
        {
            throw new InvalidTransitionException($"Verdict is for rollout '{verdict.RolloutId}', not '{Id}'.");
        }

        if (verdict.Ring != CurrentRing!.Name)
        {
            throw new InvalidTransitionException(
                $"Verdict is for ring '{verdict.Ring}' but the rollout is in '{CurrentRing.Name}'.");
        }

        if (verdict.EvaluatedAt < RingEnteredAt)
        {
            throw new InvalidTransitionException("Verdict predates the current ring and cannot describe it.");
        }

        return verdict.Status == VerdictStatus.Unhealthy
            ? this with
            {
                State = RolloutState.RolledBack,
                LastVerdict = verdict,
                RollbackReason = $"Health gate failed in '{verdict.Ring}': {string.Join("; ", verdict.Reasons)}",
            }
            : this with { LastVerdict = verdict };
    }

    /// <summary>
    /// Widens to the next ring, or completes after the last one. Requires a healthy
    /// verdict for the current ring and the ring's minimum soak time to have passed.
    /// </summary>
    public Rollout Advance(DateTimeOffset now)
    {
        Require(RolloutState.InProgress, "advance");
        var ring = CurrentRing!;

        if (LastVerdict is not { Status: VerdictStatus.Healthy })
        {
            throw new InvalidTransitionException($"Ring '{ring.Name}' has no healthy verdict yet.");
        }

        var soaked = now - RingEnteredAt!.Value;
        if (soaked < ring.MinSoak)
        {
            throw new InvalidTransitionException(
                $"Ring '{ring.Name}' must soak for {ring.MinSoak}; only {soaked} has elapsed.");
        }

        return RingIndex == Plan.Rings.Count - 1
            ? this with { State = RolloutState.Completed }
            : this with { RingIndex = RingIndex + 1, RingEnteredAt = now, LastVerdict = null };
    }

    public Rollout Rollback(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Require(RolloutState.InProgress, "roll back");
        return this with { State = RolloutState.RolledBack, RollbackReason = reason };
    }

    private void Require(RolloutState expected, string verb)
    {
        if (State != expected)
        {
            throw new InvalidTransitionException($"Cannot {verb} a rollout that is {State}.");
        }
    }
}

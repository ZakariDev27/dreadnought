using System.Collections.Concurrent;
using Dreadnought.Core.Audit;
using Dreadnought.Core.Config;

namespace Dreadnought.Core.Rollouts;

public interface IRolloutStore
{
    Rollout? Find(string id);
    IReadOnlyList<Rollout> List();
    bool TryAdd(Rollout rollout);

    /// <summary>Compare-and-swap: replaces only if the stored value is still <paramref name="expected"/>.</summary>
    bool TryReplace(Rollout expected, Rollout updated);
}

public sealed class InMemoryRolloutStore : IRolloutStore
{
    private readonly ConcurrentDictionary<string, Rollout> _rollouts = new(StringComparer.Ordinal);

    public Rollout? Find(string id) => _rollouts.GetValueOrDefault(id);
    public IReadOnlyList<Rollout> List() => [.. _rollouts.Values.OrderBy(r => r.Id, StringComparer.Ordinal)];
    public bool TryAdd(Rollout rollout) => _rollouts.TryAdd(rollout.Id, rollout);
    public bool TryReplace(Rollout expected, Rollout updated) => _rollouts.TryUpdate(updated.Id, updated, expected);
}

public sealed class RolloutNotFoundException(string id) : Exception($"Rollout '{id}' does not exist.");

public sealed class RolloutConflictException(string message) : Exception(message);

/// <summary>
/// Application service around the <see cref="Rollout"/> state machine: loads, applies a
/// transition, persists with optimistic concurrency, and writes the audit trail.
/// </summary>
public sealed class RolloutService(
    IRolloutStore store,
    AuditLog audit,
    FleetDefinition fleet,
    ApprovalPolicy policy,
    TimeProvider clock)
{
    public Rollout Create(string id, string baselineName, int version, string author)
    {
        var baseline = fleet.Find(baselineName, version)
            ?? throw new ArgumentException($"Baseline '{baselineName}@{version}' is not in the fleet definition.");

        var rollout = Rollout
            .Create(id, baseline.ChangeId, author, baseline.IsSensitive, fleet.Plan)
            .Submit(policy);

        if (!store.TryAdd(rollout))
        {
            throw new RolloutConflictException($"Rollout '{id}' already exists.");
        }

        audit.Append(author, "rollout.created", id,
            $"{baseline.ChangeId}; sensitive={baseline.IsSensitive}; approvals required={policy.RequiredFor(rollout)}");
        return rollout;
    }

    public Rollout Get(string id) => store.Find(id) ?? throw new RolloutNotFoundException(id);

    public IReadOnlyList<Rollout> List() => store.List();

    public Rollout Approve(string id, string approver) =>
        Apply(id, approver, "rollout.approved", r => r.Approve(approver, policy),
            r => $"{r.Approvers.Count}/{policy.RequiredFor(r)} approvals");

    public Rollout Start(string id, string actor) =>
        Apply(id, actor, "rollout.started", r => r.Start(clock.GetUtcNow()), r => $"ring={r.CurrentRing!.Name}");

    public Rollout RecordVerdict(string id, Verdict verdict, string actor)
    {
        var updated = Apply(id, actor, "rollout.verdict", r => r.RecordVerdict(verdict),
            _ => $"ring={verdict.Ring}; status={verdict.Status}; n={verdict.SampleSize}");

        if (updated.State == RolloutState.RolledBack)
        {
            audit.Append("dreadnought", "rollout.rolled_back", id, updated.RollbackReason!);
        }

        return updated;
    }

    public Rollout Advance(string id, string actor) =>
        Apply(id, actor, "rollout.advanced", r => r.Advance(clock.GetUtcNow()),
            r => r.State == RolloutState.Completed ? "completed" : $"ring={r.CurrentRing!.Name}");

    public Rollout Rollback(string id, string actor, string reason) =>
        Apply(id, actor, "rollout.rolled_back", r => r.Rollback(reason), _ => reason);

    private Rollout Apply(
        string id, string actor, string action, Func<Rollout, Rollout> transition, Func<Rollout, string> describe)
    {
        var current = Get(id);
        var updated = transition(current);

        if (!store.TryReplace(current, updated))
        {
            throw new RolloutConflictException($"Rollout '{id}' was modified concurrently; retry.");
        }

        audit.Append(actor, action, id, describe(updated));
        return updated;
    }
}

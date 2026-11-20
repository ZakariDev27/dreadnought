namespace BlackCanary.Core.Rollouts;

public enum VerdictStatus
{
    Healthy,
    Unhealthy,
    Inconclusive,
}

/// <summary>
/// The health gate's answer for one ring of one rollout. Produced by the analytics
/// service from fleet telemetry; the shape is pinned by <c>contracts/verdict.example.json</c>.
/// </summary>
public sealed record Verdict(
    string RolloutId,
    string Ring,
    VerdictStatus Status,
    IReadOnlyList<string> Reasons,
    int SampleSize,
    DateTimeOffset EvaluatedAt);

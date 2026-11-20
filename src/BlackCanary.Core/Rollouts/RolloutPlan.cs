namespace BlackCanary.Core.Rollouts;

/// <summary>
/// One stage of a rollout. <see cref="CumulativePercent"/> is the share of the fleet
/// that has the change once this ring is live, so rings only ever widen.
/// </summary>
public sealed record Ring(string Name, double CumulativePercent, TimeSpan MinSoak);

public sealed record RolloutPlan(IReadOnlyList<Ring> Rings)
{
    public static RolloutPlan Default { get; } = new(
    [
        new Ring("canary", 1, TimeSpan.FromHours(1)),
        new Ring("early", 10, TimeSpan.FromHours(4)),
        new Ring("broad", 50, TimeSpan.FromHours(12)),
        new Ring("fleet", 100, TimeSpan.Zero),
    ]);

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (Rings is null || Rings.Count == 0)
        {
            problems.Add("A rollout plan needs at least one ring.");
            return problems;
        }

        if (Rings.Select(r => r.Name).Distinct(StringComparer.Ordinal).Count() != Rings.Count)
        {
            problems.Add("Ring names must be unique.");
        }

        var previous = 0.0;
        foreach (var ring in Rings)
        {
            if (string.IsNullOrWhiteSpace(ring.Name))
            {
                problems.Add("Ring with an empty name.");
            }

            if (ring.CumulativePercent <= previous || ring.CumulativePercent > 100)
            {
                problems.Add($"Ring '{ring.Name}' must widen the previous ring and stay within 100%.");
            }

            if (ring.MinSoak < TimeSpan.Zero)
            {
                problems.Add($"Ring '{ring.Name}' has a negative soak time.");
            }

            previous = ring.CumulativePercent;
        }

        if (Math.Abs(Rings[^1].CumulativePercent - 100) > double.Epsilon)
        {
            problems.Add("The final ring must cover 100% of the fleet.");
        }

        return problems;
    }
}

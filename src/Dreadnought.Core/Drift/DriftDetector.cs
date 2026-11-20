using Dreadnought.Core.Config;
using Dreadnought.Core.Fleet;

namespace Dreadnought.Core.Drift;

public enum DriftKind
{
    /// <summary>The device did not report the setting at all.</summary>
    Missing,

    /// <summary>The device reported a different value.</summary>
    Mismatch,
}

public sealed record DriftFinding(
    string Baseline,
    string Key,
    DriftKind Kind,
    string Expected,
    string? Actual,
    Severity Severity);

public sealed record DriftReport(
    string DeviceId,
    IReadOnlyList<DriftFinding> Findings,
    int EvaluatedSettings,
    double ComplianceScore)
{
    public bool IsCompliant => Findings.Count == 0;
}

/// <summary>
/// Compares what a device reports against the baselines that apply to it.
/// Pure and deterministic: same inputs, same report.
/// </summary>
public static class DriftDetector
{
    public static DriftReport Evaluate(Device device, IEnumerable<Baseline> baselines)
    {
        var findings = new List<DriftFinding>();
        var evaluated = 0;
        var totalWeight = 0;
        var driftedWeight = 0;

        foreach (var baseline in LatestApplicable(device, baselines))
        {
            foreach (var setting in baseline.Settings)
            {
                evaluated++;
                totalWeight += (int)setting.Severity;

                DriftKind? kind = device.ObservedSettings.TryGetValue(setting.Key, out var actual)
                    ? Matches(setting.Value, actual) ? null : DriftKind.Mismatch
                    : DriftKind.Missing;

                if (kind is null)
                {
                    continue;
                }

                driftedWeight += (int)setting.Severity;
                findings.Add(new(baseline.ChangeId, setting.Key, kind.Value, setting.Value, actual, setting.Severity));
            }
        }

        var score = totalWeight == 0 ? 1.0 : 1.0 - ((double)driftedWeight / totalWeight);

        return new DriftReport(
            device.Id,
            findings.OrderByDescending(f => f.Severity).ThenBy(f => f.Key, StringComparer.Ordinal).ToList(),
            evaluated,
            Math.Round(score, 4));
    }

    private static IEnumerable<Baseline> LatestApplicable(Device device, IEnumerable<Baseline> baselines) =>
        baselines
            .Where(b => b.AppliesTo(device))
            .GroupBy(b => b.Name)
            .Select(g => g.MaxBy(b => b.Version)!)
            .OrderBy(b => b.Name, StringComparer.Ordinal);

    // Agents on different platforms report booleans with different casing.
    private static bool Matches(string expected, string actual) =>
        string.Equals(expected.Trim(), actual.Trim(), StringComparison.OrdinalIgnoreCase);
}

using Dreadnought.Core.Fleet;

namespace Dreadnought.Core.Config;

/// <summary>
/// Severity doubles as the weight a setting carries in the compliance score,
/// so one missing critical control outweighs several cosmetic ones.
/// </summary>
public enum Severity
{
    Low = 1,
    Medium = 3,
    High = 7,
    Critical = 15,
}

public sealed record Setting(
    string Key,
    string Value,
    Severity Severity = Severity.Medium,
    bool Sensitive = false);

/// <summary>
/// A versioned, declarative statement of desired device state. Baselines live in
/// git; a new version is a change that must be rolled out, never edited in place.
/// </summary>
public sealed record Baseline(
    string Name,
    int Version,
    Platform Platform,
    IReadOnlyList<ComplianceTier> Tiers,
    IReadOnlyList<Setting> Settings)
{
    public string ChangeId => $"{Name}@{Version}";

    /// <summary>Sensitive baselines need extra human approval before rollout.</summary>
    public bool IsSensitive => Settings.Any(s => s.Sensitive);

    public bool AppliesTo(Device device) =>
        device.Platform == Platform && Tiers.Contains(device.Tier);
}

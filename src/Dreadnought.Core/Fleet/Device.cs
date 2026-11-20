namespace Dreadnought.Core.Fleet;

public enum Platform
{
    MacOS,
    Windows,
    Linux,
}

/// <summary>
/// Compliance environment a device lives in. Baselines target tiers, so the same
/// fleet can carry different requirements without forking the tooling.
/// </summary>
public enum ComplianceTier
{
    Standard,
    Restricted,
    Research,
}

/// <summary>A device as last reported by its agent.</summary>
public sealed record Device(
    string Id,
    Platform Platform,
    ComplianceTier Tier,
    string OsVersion,
    IReadOnlyDictionary<string, string> ObservedSettings,
    DateTimeOffset LastSeen);

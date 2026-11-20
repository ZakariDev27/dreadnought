using Dreadnought.Core.Config;
using Dreadnought.Core.Fleet;
using Dreadnought.Core.Rollouts;

namespace Dreadnought.Core.Tests;

internal static class TestData
{
    public static readonly DateTimeOffset T0 = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);

    public static readonly RolloutPlan TwoRings = new(
    [
        new Ring("canary", 5, TimeSpan.FromHours(1)),
        new Ring("fleet", 100, TimeSpan.Zero),
    ]);

    public static Baseline MacCore(int version = 1, params Setting[] settings) => new(
        "macos-core",
        version,
        Platform.MacOS,
        [ComplianceTier.Standard, ComplianceTier.Restricted],
        settings.Length > 0
            ? settings
            :
            [
                new Setting("disk.encryption", "enabled", Severity.Critical, Sensitive: true),
                new Setting("firewall.enabled", "true", Severity.High),
                new Setting("motd", "hello", Severity.Low),
            ]);

    public static Device Mac(string id = "mac-1", ComplianceTier tier = ComplianceTier.Standard, params (string Key, string Value)[] observed) =>
        new(id, Platform.MacOS, tier, "15.1", observed.ToDictionary(o => o.Key, o => o.Value), T0);

    public static Verdict Verdict(string ring, VerdictStatus status, DateTimeOffset at, string rolloutId = "r-1") =>
        new(rolloutId, ring, status, status == VerdictStatus.Healthy ? [] : ["crash rate regressed"], 40, at);
}

/// <summary>A clock tests can move by hand.</summary>
internal sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

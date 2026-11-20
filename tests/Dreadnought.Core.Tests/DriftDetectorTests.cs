using Dreadnought.Core.Config;
using Dreadnought.Core.Drift;
using Dreadnought.Core.Fleet;

namespace Dreadnought.Core.Tests;

public class DriftDetectorTests
{
    [Fact]
    public void Compliant_device_scores_one_with_no_findings()
    {
        var device = TestData.Mac(observed: [("disk.encryption", "enabled"), ("firewall.enabled", "TRUE"), ("motd", " hello ")]);

        var report = DriftDetector.Evaluate(device, [TestData.MacCore()]);

        Assert.True(report.IsCompliant);
        Assert.Equal(1.0, report.ComplianceScore);
        Assert.Equal(3, report.EvaluatedSettings);
    }

    [Fact]
    public void Missing_and_mismatched_settings_are_distinguished()
    {
        var device = TestData.Mac(observed: [("disk.encryption", "enabled"), ("firewall.enabled", "false")]);

        var report = DriftDetector.Evaluate(device, [TestData.MacCore()]);

        Assert.Equal(2, report.Findings.Count);
        Assert.Contains(report.Findings, f => f is { Key: "firewall.enabled", Kind: DriftKind.Mismatch, Actual: "false" });
        Assert.Contains(report.Findings, f => f is { Key: "motd", Kind: DriftKind.Missing, Actual: null });
    }

    [Fact]
    public void Score_is_weighted_by_severity()
    {
        // Weights: critical 15, high 7, low 1 => total 23.
        var missingLow = TestData.Mac(observed: [("disk.encryption", "enabled"), ("firewall.enabled", "true")]);
        var missingCritical = TestData.Mac(observed: [("firewall.enabled", "true"), ("motd", "hello")]);

        var low = DriftDetector.Evaluate(missingLow, [TestData.MacCore()]);
        var critical = DriftDetector.Evaluate(missingCritical, [TestData.MacCore()]);

        Assert.Equal(Math.Round(1 - (1.0 / 23), 4), low.ComplianceScore);
        Assert.Equal(Math.Round(1 - (15.0 / 23), 4), critical.ComplianceScore);
    }

    [Fact]
    public void Findings_are_ordered_most_severe_first()
    {
        var report = DriftDetector.Evaluate(TestData.Mac(), [TestData.MacCore()]);

        Assert.Equal(new[] { "disk.encryption", "firewall.enabled", "motd" }, report.Findings.Select(f => f.Key).ToArray());
    }

    [Fact]
    public void Baselines_for_other_platforms_or_tiers_are_ignored()
    {
        var research = TestData.Mac(tier: ComplianceTier.Research);
        var windowsOnly = TestData.MacCore() with { Name = "windows-core", Platform = Platform.Windows };

        Assert.Equal(0, DriftDetector.Evaluate(research, [TestData.MacCore()]).EvaluatedSettings);
        Assert.True(DriftDetector.Evaluate(TestData.Mac(), [windowsOnly]).IsCompliant);
    }

    [Fact]
    public void Only_the_latest_version_of_a_baseline_is_enforced()
    {
        var v1 = TestData.MacCore(1, new Setting("screenlock", "600"));
        var v2 = TestData.MacCore(2, new Setting("screenlock", "300"));
        var device = TestData.Mac(observed: [("screenlock", "300")]);

        Assert.True(DriftDetector.Evaluate(device, [v1, v2]).IsCompliant);
    }
}

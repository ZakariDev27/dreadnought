using Dreadnought.Core.Config;
using Dreadnought.Core.Fleet;
using Dreadnought.Core.Rollouts;

namespace Dreadnought.Core.Tests;

public class BaselineValidatorTests
{
    private static IReadOnlyList<string> Codes(params Baseline[] baselines) =>
        BaselineValidator.Validate(new FleetDefinition(baselines, RolloutPlan.Default)).Select(i => i.Code).ToList();

    [Fact]
    public void Well_formed_fleet_has_no_issues()
    {
        Assert.Empty(Codes(TestData.MacCore()));
    }

    [Theory]
    [InlineData("MacOS_Core")]
    [InlineData("-leading-dash")]
    [InlineData("")]
    public void Names_must_be_kebab_case(string name)
    {
        Assert.Contains("DN001", Codes(TestData.MacCore() with { Name = name }));
    }

    [Fact]
    public void Version_must_be_positive()
    {
        Assert.Contains("DN002", Codes(TestData.MacCore(0)));
    }

    [Fact]
    public void Duplicate_keys_within_a_baseline_are_rejected()
    {
        var baseline = TestData.MacCore(1, new Setting("a", "1"), new Setting("a", "2"));

        Assert.Contains("DN004", Codes(baseline));
    }

    [Fact]
    public void Sensitive_settings_must_be_high_severity()
    {
        var baseline = TestData.MacCore(1, new Setting("usb", "blocked", Severity.Low, Sensitive: true));

        Assert.Contains("DN006", Codes(baseline));
    }

    [Fact]
    public void Overlapping_baselines_must_agree_on_shared_keys()
    {
        var core = TestData.MacCore(1, new Setting("screenlock", "300"));
        var overlay = new Baseline("macos-restricted", 1, Platform.MacOS, [ComplianceTier.Restricted], [new Setting("screenlock", "60")]);

        var issues = BaselineValidator.Validate(new FleetDefinition([core, overlay], RolloutPlan.Default));

        var conflict = Assert.Single(issues);
        Assert.Equal("DN007", conflict.Code);
        Assert.Contains("Restricted", conflict.Message);
    }

    [Fact]
    public void Baselines_that_never_share_a_device_may_disagree()
    {
        var core = TestData.MacCore(1, new Setting("screenlock", "300"));
        var research = new Baseline("macos-research", 1, Platform.MacOS, [ComplianceTier.Research], [new Setting("screenlock", "900")]);

        Assert.Empty(Codes(core, research));
    }

    [Fact]
    public void A_superseded_version_does_not_conflict_with_its_successor()
    {
        var v1 = TestData.MacCore(1, new Setting("screenlock", "600"));
        var v2 = TestData.MacCore(2, new Setting("screenlock", "300"));

        Assert.Empty(Codes(v1, v2));
    }

    [Fact]
    public void Ring_plan_problems_surface_as_issues()
    {
        var shrinking = new RolloutPlan([new Ring("a", 50, TimeSpan.Zero), new Ring("b", 20, TimeSpan.Zero)]);

        var issues = BaselineValidator.Validate(new FleetDefinition([TestData.MacCore()], shrinking));

        Assert.Contains(issues, i => i.Code == "DN020");
    }
}

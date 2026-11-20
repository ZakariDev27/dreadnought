using System.Text.RegularExpressions;
using BlackCanary.Core.Rollouts;

namespace BlackCanary.Core.Config;

public sealed record ValidationIssue(string Code, string Subject, string Message)
{
    public override string ToString() => $"{Code} {Subject}: {Message}";
}

/// <summary>
/// Static checks run in CI on every pull request against the fleet directory.
/// Each rule has a stable code so a failure can be searched and suppressed deliberately.
/// </summary>
public static partial class BaselineValidator
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex NamePattern();

    public static IReadOnlyList<ValidationIssue> Validate(FleetDefinition fleet)
    {
        var issues = new List<ValidationIssue>();

        foreach (var baseline in fleet.Baselines)
        {
            ValidateOne(baseline, issues);
        }

        foreach (var group in fleet.Baselines.GroupBy(b => (b.Name, b.Version)).Where(g => g.Count() > 1))
        {
            issues.Add(new("DN008", $"{group.Key.Name}@{group.Key.Version}", "Declared more than once."));
        }

        FindConflicts(fleet.Baselines, issues);

        foreach (var problem in fleet.Plan.Validate())
        {
            issues.Add(new("DN020", "rings", problem));
        }

        return issues;
    }

    private static void ValidateOne(Baseline baseline, List<ValidationIssue> issues)
    {
        var subject = string.IsNullOrWhiteSpace(baseline.Name) ? "<unnamed>" : baseline.ChangeId;

        if (baseline.Name is null || !NamePattern().IsMatch(baseline.Name))
        {
            issues.Add(new("DN001", subject, "Name must be lowercase kebab-case."));
        }

        if (baseline.Version < 1)
        {
            issues.Add(new("DN002", subject, "Version must be 1 or greater."));
        }

        if (baseline.Tiers is null || baseline.Tiers.Count == 0)
        {
            issues.Add(new("DN003", subject, "At least one compliance tier is required."));
        }

        if (baseline.Settings is null || baseline.Settings.Count == 0)
        {
            issues.Add(new("DN003", subject, "At least one setting is required."));
            return;
        }

        foreach (var setting in baseline.Settings)
        {
            if (string.IsNullOrWhiteSpace(setting.Key))
            {
                issues.Add(new("DN005", subject, "Setting with an empty key."));
            }
            else if (setting.Value is null)
            {
                issues.Add(new("DN005", subject, $"Setting '{setting.Key}' has no value."));
            }

            // A change that needs two-person review but is scored as cosmetic is a
            // contradiction; force the author to decide which it is.
            if (setting.Sensitive && setting.Severity < Severity.High)
            {
                issues.Add(new("DN006", subject,
                    $"Sensitive setting '{setting.Key}' must be severity high or critical."));
            }
        }

        foreach (var dup in baseline.Settings
                     .Where(s => !string.IsNullOrWhiteSpace(s.Key))
                     .GroupBy(s => s.Key, StringComparer.Ordinal)
                     .Where(g => g.Count() > 1))
        {
            issues.Add(new("DN004", subject, $"Setting '{dup.Key}' is declared {dup.Count()} times."));
        }
    }

    /// <summary>
    /// Two baselines that can land on the same device must not disagree about a key,
    /// otherwise the fleet flaps between them and drift never converges. Only the
    /// latest version of each baseline is considered live.
    /// </summary>
    private static void FindConflicts(IReadOnlyList<Baseline> baselines, List<ValidationIssue> issues)
    {
        var live = baselines
            .Where(b => b.Name is not null && b.Tiers is not null && b.Settings is not null)
            .GroupBy(b => b.Name)
            .Select(g => g.MaxBy(b => b.Version)!)
            .ToList();

        var claims =
            from baseline in live
            from tier in baseline.Tiers.Distinct()
            from setting in baseline.Settings
            where !string.IsNullOrWhiteSpace(setting.Key)
            group (baseline, setting) by (baseline.Platform, tier, setting.Key);

        foreach (var claim in claims)
        {
            var values = claim.Select(c => c.setting.Value).Distinct(StringComparer.Ordinal).ToList();
            if (values.Count > 1)
            {
                var owners = string.Join(", ", claim.Select(c => c.baseline.ChangeId).Distinct());
                issues.Add(new("DN007", claim.Key.Key,
                    $"Conflicting values on {claim.Key.Platform}/{claim.Key.tier} from {owners}."));
            }
        }
    }
}

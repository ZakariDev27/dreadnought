using System.Text.Json;
using Dreadnought.Core.Rollouts;

namespace Dreadnought.Core.Config;

/// <summary>Everything the control plane reads from the config-as-code directory.</summary>
public sealed record FleetDefinition(IReadOnlyList<Baseline> Baselines, RolloutPlan Plan)
{
    public Baseline? Find(string name, int version) =>
        Baselines.FirstOrDefault(b => b.Name == name && b.Version == version);
}

public sealed class FleetLoadException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
/// Loads a fleet directory laid out as <c>baselines/*.json</c> plus <c>rings.json</c>.
/// Loading only parses; <see cref="BaselineValidator"/> decides whether it is acceptable.
/// </summary>
public static class FleetRepository
{
    public static FleetDefinition Load(string root)
    {
        var baselineDir = Path.Combine(root, "baselines");
        if (!Directory.Exists(baselineDir))
        {
            throw new FleetLoadException($"No baselines directory under '{root}'.");
        }

        var baselines = Directory
            .EnumerateFiles(baselineDir, "*.json", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(Read<Baseline>)
            .ToList();

        var ringsPath = Path.Combine(root, "rings.json");
        var plan = File.Exists(ringsPath) ? Read<RolloutPlan>(ringsPath) : RolloutPlan.Default;

        return new FleetDefinition(baselines, plan);
    }

    private static T Read<T>(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream, FleetJson.Options)
                ?? throw new FleetLoadException($"{path}: document is null.");
        }
        catch (JsonException ex)
        {
            throw new FleetLoadException($"{path}: {ex.Message}", ex);
        }
    }
}

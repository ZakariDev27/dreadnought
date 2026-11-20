using System.Collections.Concurrent;
using BlackCanary.Core.Config;
using BlackCanary.Core.Drift;

namespace BlackCanary.Core.Fleet;

public sealed record ComplianceSummary(
    int Devices,
    int Compliant,
    double MeanScore,
    IReadOnlyList<DriftHotspot> Hotspots);

/// <summary>A setting that is drifting across many devices — usually one root cause.</summary>
public sealed record DriftHotspot(string Key, Severity Severity, int Devices);

/// <summary>Device inventory plus the drift view derived from it.</summary>
public sealed class FleetService(FleetDefinition fleet)
{
    private readonly ConcurrentDictionary<string, Device> _devices = new(StringComparer.Ordinal);

    public DriftReport CheckIn(Device device)
    {
        _devices[device.Id] = device;
        return DriftDetector.Evaluate(device, fleet.Baselines);
    }

    public DriftReport? Drift(string deviceId) =>
        _devices.TryGetValue(deviceId, out var device) ? DriftDetector.Evaluate(device, fleet.Baselines) : null;

    public ComplianceSummary Summarise(int hotspots = 5)
    {
        var reports = _devices.Values.Select(d => DriftDetector.Evaluate(d, fleet.Baselines)).ToList();

        var top = reports
            .SelectMany(r => r.Findings)
            .GroupBy(f => (f.Key, f.Severity))
            .Select(g => new DriftHotspot(g.Key.Key, g.Key.Severity, g.Count()))
            .OrderByDescending(h => h.Devices * (int)h.Severity)
            .ThenBy(h => h.Key, StringComparer.Ordinal)
            .Take(hotspots)
            .ToList();

        return new ComplianceSummary(
            reports.Count,
            reports.Count(r => r.IsCompliant),
            reports.Count == 0 ? 1.0 : Math.Round(reports.Average(r => r.ComplianceScore), 4),
            top);
    }
}

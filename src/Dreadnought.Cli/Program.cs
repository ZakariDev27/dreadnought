using System.Text.Json;
using Dreadnought.Core.Config;
using Dreadnought.Core.Drift;
using Dreadnought.Core.Fleet;
using Dreadnought.Core.Rollouts;

// Exit codes: 0 ok, 1 findings (validation issues / drift), 2 usage or load error.
try
{
    return args switch
    {
        ["validate", var dir] => Validate(dir),
        ["rings", var dir, var changeId, var count] when int.TryParse(count, out var n) && n > 0 => Rings(dir, changeId, n),
        ["drift", var dir, var devicesFile] => Drift(dir, devicesFile),
        _ => Usage(),
    };
}
catch (Exception ex) when (ex is FleetLoadException or IOException or JsonException)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 2;
}

static int Usage()
{
    Console.Error.WriteLine(
        """
        dreadnought — fleet configuration as code

          dreadnought validate <fleet-dir>                   Lint baselines and rings (CI gate)
          dreadnought rings <fleet-dir> <change-id> <count>  Preview ring sizes for a simulated fleet
          dreadnought drift <fleet-dir> <devices.json>       Report drift for a device snapshot
        """);
    return 2;
}

static int Validate(string dir)
{
    var fleet = FleetRepository.Load(dir);
    var issues = BaselineValidator.Validate(fleet);

    foreach (var issue in issues)
    {
        Console.WriteLine(issue);
    }

    Console.WriteLine(issues.Count == 0
        ? $"ok: {fleet.Baselines.Count} baselines, {fleet.Plan.Rings.Count} rings"
        : $"failed: {issues.Count} issue(s)");
    return issues.Count == 0 ? 0 : 1;
}

static int Rings(string dir, string changeId, int count)
{
    var plan = FleetRepository.Load(dir).Plan;
    var sizes = new int[plan.Rings.Count];
    for (var i = 0; i < count; i++)
    {
        sizes[RingAssigner.RingIndexFor(plan, $"device-{i:D6}", changeId)]++;
    }

    var cumulative = 0;
    for (var i = 0; i < sizes.Length; i++)
    {
        cumulative += sizes[i];
        Console.WriteLine(
            $"{plan.Rings[i].Name,-10} +{sizes[i],7} devices  cumulative {cumulative,7} ({100.0 * cumulative / count:F1}%)  soak {plan.Rings[i].MinSoak}");
    }

    return 0;
}

static int Drift(string dir, string devicesFile)
{
    var fleet = FleetRepository.Load(dir);
    using var stream = File.OpenRead(devicesFile);
    var devices = JsonSerializer.Deserialize<List<Device>>(stream, FleetJson.Options) ?? [];

    var drifted = 0;
    foreach (var device in devices)
    {
        var report = DriftDetector.Evaluate(device, fleet.Baselines);
        if (report.IsCompliant)
        {
            continue;
        }

        drifted++;
        Console.WriteLine($"{device.Id}  score {report.ComplianceScore:P0}");
        foreach (var f in report.Findings)
        {
            Console.WriteLine($"  [{f.Severity,-8}] {f.Key}: expected '{f.Expected}', {(f.Actual is null ? "not reported" : $"found '{f.Actual}'")}");
        }
    }

    Console.WriteLine($"{devices.Count - drifted}/{devices.Count} devices compliant");
    return drifted == 0 ? 0 : 1;
}

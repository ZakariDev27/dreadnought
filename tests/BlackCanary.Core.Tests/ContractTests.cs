using System.Text.Json;
using BlackCanary.Core.Config;
using BlackCanary.Core.Rollouts;

namespace BlackCanary.Core.Tests;

/// <summary>Pins the wire format shared with the Python analytics package.</summary>
public class ContractTests
{
    private static readonly string Example =
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "verdict.example.json"));

    [Fact]
    public void Verdict_example_deserialises_strictly()
    {
        var verdict = JsonSerializer.Deserialize<Verdict>(Example, FleetJson.Options)!;

        Assert.Equal("canary", verdict.Ring);
        Assert.Equal(VerdictStatus.Unhealthy, verdict.Status);
        Assert.Equal(50, verdict.SampleSize);
        Assert.Single(verdict.Reasons);
        Assert.Equal(TimeSpan.Zero, verdict.EvaluatedAt.Offset);
    }

    [Fact]
    public void Verdict_round_trips_with_the_same_field_names()
    {
        var verdict = JsonSerializer.Deserialize<Verdict>(Example, FleetJson.Options)!;
        using var original = JsonDocument.Parse(Example);
        using var written = JsonDocument.Parse(JsonSerializer.Serialize(verdict, FleetJson.Options));

        Assert.Equal(
            original.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray(),
            written.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal("unhealthy", written.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public void Unknown_fields_are_rejected()
    {
        var withExtra = Example.Replace("\"ring\"", "\"rnig\": \"typo\", \"ring\"");

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Verdict>(withExtra, FleetJson.Options));
    }
}

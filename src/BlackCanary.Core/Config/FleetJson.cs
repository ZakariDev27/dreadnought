using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlackCanary.Core.Config;

/// <summary>Single source of truth for how fleet documents are (de)serialised.</summary>
public static class FleetJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static void Configure(JsonSerializerOptions options)
    {
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.PropertyNameCaseInsensitive = true;
        options.ReadCommentHandling = JsonCommentHandling.Skip;
        options.AllowTrailingCommas = true;
        // A typo in a config key must fail review, not be silently ignored.
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    }

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions();
        Configure(options);
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

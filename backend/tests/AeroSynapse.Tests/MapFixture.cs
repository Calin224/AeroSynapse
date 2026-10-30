using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AeroSynapse.Domain.Airports;

namespace AeroSynapse.Tests;

internal static class MapFixture
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static AirportMap Create()
    {
        var ids = new Dictionary<string, string>();
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "airport-map.json"));
        json = Regex.Replace(json, @"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", match =>
        {
            if (!ids.TryGetValue(match.Value, out var id))
                ids[match.Value] = id = Guid.NewGuid().ToString();
            return id;
        });
        return JsonSerializer.Deserialize<AirportMap>(json, Json)!;
    }
}

using System.Text.Json;

namespace AeroSynapse.Application.Contracts;

internal static class NavigationContractValidation
{
    public static void Validate(JsonElement value, string path, List<ContractFieldError> errors)
    {
        void Add(string field, string code = "invalid_value") => errors.Add(VersionedContract.Error(path + field, code));
        static string Text(JsonElement item, string name) => item.GetProperty(name).GetString()!;
        static JsonElement[] Items(JsonElement item, string name) => item.GetProperty(name).EnumerateArray().ToArray();
        void Unique(JsonElement[] items, string? key, string field)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < items.Length; index++)
                if (!seen.Add(key is null ? items[index].GetString()! : Text(items[index], key)))
                    Add($"{field}/{index}" + (key is null ? "" : "/" + key), "duplicate_id");
        }
        var graph = value.GetProperty("graph"); var nodes = Items(graph,"nodes"); var edges = Items(graph,"edges");
        Unique(nodes,"id","/graph/nodes"); Unique(edges,"id","/graph/edges");
        var nodeIds = nodes.Select(node => Text(node,"id")).ToHashSet(); var edgeIds = edges.Select(edge => Text(edge,"id")).ToHashSet();
        var kinds = nodes.GroupBy(node => Text(node,"id")).ToDictionary(group => group.Key,group => Text(group.First(),"kind"));
        for (var index = 0; index < edges.Length; index++)
        {
            foreach (var name in new[] { "fromZoneId", "toZoneId" })
                if (!nodeIds.Contains(Text(edges[index],name))) Add($"/graph/edges/{index}/{name}");
            if (Text(edges[index],"fromZoneId") == Text(edges[index],"toZoneId")) Add($"/graph/edges/{index}/toZoneId");
            Unique(Items(edges[index],"requiredTags"),null,$"/graph/edges/{index}/requiredTags");
        }
        for (var index = 0; index < nodes.Length; index++) Unique(Items(nodes[index],"requiredTags"),null,$"/graph/nodes/{index}/requiredTags");
        foreach (var name in new[] { "checkpointBindings", "gateBindings" })
        {
            var bindings = Items(value,name); Unique(bindings,"resourceId","/"+name);
            for (var index = 0; index < bindings.Length; index++)
                if (!nodeIds.Contains(Text(bindings[index],"zoneId")) || name == "gateBindings" && kinds[Text(bindings[index],"zoneId")] != "Gate") Add($"/{name}/{index}/zoneId");
        }
        foreach (var (name,known) in new[] { ("closedZoneIds",nodeIds), ("closedEdgeIds",edgeIds) })
        {
            var ids = Items(value,name); Unique(ids,null,"/"+name);
            for (var index = 0; index < ids.Length; index++) if (!known.Contains(ids[index].GetString()!)) Add($"/{name}/{index}");
        }
        var profiles = Items(value,"accessProfiles"); Unique(profiles,"groupKind","/accessProfiles");
        for (var index = 0; index < profiles.Length; index++)
        {
            Unique(Items(profiles[index],"accessTags"),null,$"/accessProfiles/{index}/accessTags");
            var ids = Items(profiles[index],"forbiddenZoneIds"); Unique(ids,null,$"/accessProfiles/{index}/forbiddenZoneIds");
            for (var zone = 0; zone < ids.Length; zone++) if (!nodeIds.Contains(ids[zone].GetString()!)) Add($"/accessProfiles/{index}/forbiddenZoneIds/{zone}");
        }
        if (value.TryGetProperty("flightGates",out var destinations)) Unique(destinations.EnumerateArray().ToArray(),"flightId","/flightGates");
    }

    public static void Envelope(JsonElement value, string path, List<ContractFieldError> errors)
    {
        var generation = value.GetProperty("passengerGeneration"); var navigation = value.GetProperty("navigation");
        PassengerContractValidation.Validate(generation,path+"/passengerGeneration",errors);
        Validate(navigation,path+"/navigation",errors);
        var flights = generation.GetProperty("flights").EnumerateArray().Select(flight => flight.GetProperty("flightId").GetString()).ToHashSet();
        if (!flights.SetEquals(navigation.GetProperty("flightGates").EnumerateArray().Select(flight => flight.GetProperty("flightId").GetString())))
            errors.Add(VersionedContract.Error(path+"/navigation/flightGates","comparison_mismatch"));
    }

    public static void Result(JsonElement value, string path, List<ContractFieldError> errors)
    {
        static int Number(JsonElement item,string name) => (int)item.GetProperty(name).GetDouble();
        var count = Number(value,"modeledCount"); var offset = Number(value,"offset"); var returned = Number(value,"returnedCount");
        if (Number(value,"routedCount") + Number(value,"errorCount") != count || returned != value.GetProperty("journeys").GetArrayLength()
            || returned > Math.Max(0,count-offset) || value.GetProperty("hasMore").GetBoolean() != (offset+returned < count)
            || value.GetProperty("routes").EnumerateArray().Sum(route => Number(route,"passengerCount")) != Number(value,"routedCount")
            || value.GetProperty("flights").EnumerateArray().Sum(flight => Number(flight,"modeledCount")) != count)
            errors.Add(VersionedContract.Error(path,"invalid_result"));
    }
}

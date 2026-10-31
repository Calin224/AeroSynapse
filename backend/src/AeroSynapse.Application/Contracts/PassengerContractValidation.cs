using System.Text.Json;

namespace AeroSynapse.Application.Contracts;

internal static class PassengerContractValidation
{
    public static void Validate(JsonElement value, string path, List<ContractFieldError> errors)
    {
        void Add(string field, string code = "invalid_value") => errors.Add(VersionedContract.Error(path + field, code));
        JsonElement[] Items(string name) => value.GetProperty(name).EnumerateArray().ToArray();
        static double Number(JsonElement item, string name) => item.GetProperty(name).GetDouble();
        static string Text(JsonElement item, string name) => item.GetProperty(name).GetString()!;
        void Unique(JsonElement[] items, string key, string field)
        {
            var seen = new HashSet<string>();
            for (var index = 0; index < items.Length; index++)
                if (!seen.Add(Text(items[index], key))) Add($"{field}/{index}/{key}", "duplicate_id");
        }
        var checkpoints = Items("checkpoints"); var flights = Items("flights"); var batches = Items("batches");
        Unique(checkpoints, "checkpointId", "/checkpoints"); Unique(flights, "flightId", "/flights"); Unique(batches, "flightId", "/batches");
        var checkpointMap = checkpoints.GroupBy(item => Text(item, "checkpointId")).ToDictionary(group => group.Key, group => group.First());
        var flightMap = flights.GroupBy(item => Text(item, "flightId")).ToDictionary(group => group.Key, group => group.First());
        if (!flightMap.Keys.ToHashSet().SetEquals(batches.Select(item => Text(item, "flightId")))) Add("/batches");
        if (batches.Sum(item => Number(item, "modeledCount")) > 100000) Add("/batches", "out_of_range");
        for (var index = 0; index < flights.Length; index++)
        {
            if (!checkpointMap.ContainsKey(Text(flights[index], "checkpointId"))) Add($"/flights/{index}/checkpointId");
            if (Number(flights[index], "boardingCloseMinute") > Number(value, "durationMinutes")) Add($"/flights/{index}/boardingCloseMinute", "out_of_range");
            // Spread alternatives name each checkpoint once, all known, and include the flight's own route.
            if (!flights[index].TryGetProperty("checkpointRoutes", out var alternatives)) continue;
            var lanes = alternatives.EnumerateArray().ToArray();
            Unique(lanes, "checkpointId", $"/flights/{index}/checkpointRoutes");
            for (var lane = 0; lane < lanes.Length; lane++)
                if (!checkpointMap.ContainsKey(Text(lanes[lane], "checkpointId"))) Add($"/flights/{index}/checkpointRoutes/{lane}/checkpointId");
            if (!lanes.Any(lane => Text(lane, "checkpointId") == Text(flights[index], "checkpointId") && Text(lane, "routeId") == Text(flights[index], "routeId")))
                Add($"/flights/{index}/checkpointRoutes");
        }
        // Passenger types scale every stage; operational bounds use the largest configured multiplier.
        var hasFlow = value.TryGetProperty("flow", out var flow);
        var segments = hasFlow ? flow.GetProperty("segments").EnumerateArray().ToArray() : [];
        var serviceScale = segments.Length == 0 ? 1 : segments.Max(segment => Number(segment, "serviceMultiplier"));
        var travelLow = segments.Length == 0 ? 1 : segments.Min(segment => Number(segment, "travelMultiplier"));
        var travelHigh = segments.Length == 0 ? 1 : segments.Max(segment => Number(segment, "travelMultiplier"));
        for (var index = 0; index < batches.Length; index++)
        {
            var batch = batches[index]; var field = $"/batches/{index}";
            var hasFlight = flightMap.TryGetValue(Text(batch, "flightId"), out var flight);
            var hasCheckpoint = checkpointMap.TryGetValue(Text(batch, "checkpointId"), out var checkpoint);
            if (!hasCheckpoint) Add(field + "/checkpointId");
            if (!hasFlight) Add(field + "/flightId");
            else
            {
                var count = Number(batch, "modeledCount"); var capacity = Number(flight, "passengerCapacity");
                if (Text(batch, "checkpointId") != Text(flight, "checkpointId")) Add(field + "/checkpointId");
                if ((Text(batch, "countMode") == "full" && count != capacity) || (Text(batch, "countMode") == "subset" && count >= capacity)
                    || (flight.GetProperty("isCancelled").GetBoolean() && count != 0)) Add(field + "/modeledCount");
                if (count > 0 && Number(batch.GetProperty("arrival"), "maximum") > Number(flight, "boardingCloseMinute")) Add(field + "/arrival/maximum", "out_of_range");
                var spread = batch.TryGetProperty("checkpointMode", out var mode) && mode.GetString() == "spread";
                if (spread != flight.TryGetProperty("checkpointRoutes", out _)) Add(field + "/checkpointMode");
            }
            // Bounds hold on every checkpoint and route a spread batch may use.
            var lanes = hasFlight && flight.TryGetProperty("checkpointRoutes", out var routes) ? routes.EnumerateArray().ToArray() : [];
            var rates = lanes.Where(lane => checkpointMap.ContainsKey(Text(lane, "checkpointId")))
                .Select(lane => Number(checkpointMap[Text(lane, "checkpointId")], "serviceRatePerMinute"))
                .Concat(hasCheckpoint ? [Number(checkpoint, "serviceRatePerMinute")] : []).ToArray();
            var travel = lanes.Select(lane => Number(lane, "routeTravelMinutes"))
                .Concat(hasFlight ? [Number(flight, "routeTravelMinutes")] : []).ToArray();
            Distribution(batch.GetProperty("arrival"), field + "/arrival");
            Distribution(batch.GetProperty("service"), field + "/service", .01, 20);
            Distribution(batch.GetProperty("travel"), field + "/travel", .01, 20);
            var groups = batch.GetProperty("groups").EnumerateArray().ToArray();
            Unique(groups, "kind", field + "/groups");
            if (Math.Abs(groups.Sum(group => Number(group, "weight")) - 1) > 1e-9) Add(field + "/groups");
            if (!groups.Any(group => Number(group, "minSize") == 1)) Add(field + "/groups");
            for (var groupIndex = 0; groupIndex < groups.Length; groupIndex++)
                if (Number(groups[groupIndex], "minSize") > Number(groups[groupIndex], "maxSize") ||
                    (Text(groups[groupIndex], "kind") == "solo" && (Number(groups[groupIndex], "minSize") != 1 || Number(groups[groupIndex], "maxSize") != 1)))
                    Add($"{field}/groups/{groupIndex}/maxSize");
            if (rates.Length > 0 && Number(batch.GetProperty("service"), "maximum") * groups.Max(group => Number(group, "serviceMultiplier")) * serviceScale / rates.Min() > 240)
                Add(field + "/service/maximum", "out_of_range");
            if (hasFlight && Number(batch.GetProperty("travel"), "maximum") * groups.Max(group => Number(group, "travelMultiplier")) * travelHigh * travel.Max() > 240)
                Add(field + "/travel/maximum", "out_of_range");
            if (hasFlight && Number(batch.GetProperty("travel"), "minimum") * travel.Min() * groups.Min(group => Number(group, "travelMultiplier")) * travelLow <= 0)
                Add(field + "/travel/minimum", "out_of_range");
            var surges = batch.GetProperty("surges").EnumerateArray().ToArray();
            if (surges.Sum(surge => Number(surge, "share")) > 1 + 1e-9) Add(field + "/surges");
            for (var surgeIndex = 0; surgeIndex < surges.Length; surgeIndex++)
                if (Number(surges[surgeIndex], "startsMinute") < Number(batch.GetProperty("arrival"), "minimum")
                    || Number(surges[surgeIndex], "startsMinute") >= Number(surges[surgeIndex], "endsMinute")
                    || Number(surges[surgeIndex], "endsMinute") > Number(batch.GetProperty("arrival"), "maximum")) Add($"{field}/surges/{surgeIndex}");
        }
        if (!hasFlow) return;

        var stages = flow.GetProperty("stages").EnumerateArray().ToArray();
        Unique(segments, "kind", "/flow/segments"); Unique(stages, "kind", "/flow/stages");
        var defined = stages.Select(stage => Text(stage, "kind")).ToHashSet();
        if (Math.Abs(segments.Sum(segment => Number(segment, "share")) - 1) > 1e-9) Add("/flow/segments");
        for (var index = 0; index < segments.Length; index++)
        {
            var field = $"/flow/segments/{index}";
            if (segments[index].GetProperty("holdBaggage").GetBoolean() && !defined.Contains("bagDrop")) Add(field + "/holdBaggage");
            var control = Text(segments[index], "control");
            if (control != "security" && !defined.Contains(control)) Add(field + "/control");
            if (segments[index].TryGetProperty("transferMinutes", out var transfer)) Distribution(transfer, field + "/transferMinutes", 0, 240);
        }
        var groupScale = batches.SelectMany(batch => batch.GetProperty("groups").EnumerateArray()).Max(group => Number(group, "serviceMultiplier"));
        for (var index = 0; index < stages.Length; index++)
        {
            var field = $"/flow/stages/{index}/service"; var service = stages[index].GetProperty("service");
            Distribution(service, field, .01, 240);
            if (Number(service, "maximum") * groupScale * serviceScale > 240) Add(field + "/maximum", "out_of_range");
        }
        var closures = flow.GetProperty("closures").EnumerateArray().ToArray();
        for (var index = 0; index < closures.Length; index++)
        {
            var stage = Text(closures[index], "stage");
            if (stage != "security" && !defined.Contains(stage)) Add($"/flow/closures/{index}/stage");
            if (Number(closures[index], "startsMinute") >= Number(closures[index], "endsMinute")
                || Number(closures[index], "endsMinute") > Number(value, "durationMinutes")) Add($"/flow/closures/{index}");
        }

        void Distribution(JsonElement distribution, string field, double? lowest = null, double? highest = null)
        {
            var kind = Text(distribution, "kind"); var lo = Number(distribution, "minimum"); var hi = Number(distribution, "maximum");
            if ((kind == "constant" && lo != hi) || (kind != "constant" && lo >= hi)) Add(field + "/maximum");
            if (lowest is not null && (lo < lowest || hi > highest)) Add(field, "out_of_range");
            if (kind == "normal")
            {
                if (Number(distribution, "mean") < lo || Number(distribution, "mean") > hi) Add(field + "/mean");
                if (Number(distribution, "stdDev") <= 0 || Number(distribution, "stdDev") > hi - lo) Add(field + "/stdDev");
            }
            if (kind == "exponential" && Number(distribution, "mean") <= 0) Add(field + "/mean");
            if (kind == "triangular" && (Number(distribution, "mode") < lo || Number(distribution, "mode") > hi)) Add(field + "/mode");
        }
    }
}

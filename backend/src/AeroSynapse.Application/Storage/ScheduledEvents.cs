using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Domain.DigitalTwin;

namespace AeroSynapse.Application.Storage;

/// <summary>
/// Scenario events with an operational type are scheduled for the engine; every other type stays a
/// descriptive note, as before. Targets use the typed checkpointId/flightId fields; parameters live in payloadJson.
/// </summary>
public static class ScheduledEvents
{
    private static readonly Dictionary<string, string[]> Parameters = new()
    {
        ["passenger_surge"] = ["passengerCount", "windowMinutes"],
        ["checkpoint_unavailable"] = [],
        ["staff_reduction"] = ["staffCount"],
        ["flight_change"] = ["delayMinutes"],
        ["corridor_closure"] = ["zoneId"],
        ["resume"] = ["zoneId"],
    };

    public static bool Operational(string? type) => type is not null && Parameters.ContainsKey(type);

    /// <summary>The engine's events for a scenario, or null when it schedules none (keeps the v1 wire format).</summary>
    public static IReadOnlyList<ScheduledEvent>? Wire(ScenarioInputs inputs)
    {
        var errors = new Dictionary<string, List<string>>();
        var events = Read(inputs, errors).Select(pair => pair.Event).ToArray();
        if (errors.Count != 0) throw Invalid(errors);
        return events.Length == 0 ? null : events;
    }

    /// <summary>Validates operational events against the scenario timeline; explicit field errors on failure.</summary>
    public static void Validate(ScenarioInputs inputs, SceneDetails layout)
    {
        var errors = new Dictionary<string, List<string>>();
        var events = Read(inputs, errors);
        if (errors.Count == 0 && events.Count != 0)
        {
            var context = new EventContext(inputs.DurationMinutes,
                layout.Checkpoints.ToDictionary(checkpoint => checkpoint.Id, checkpoint => StaffShiftScenarios.StaffCount(inputs, checkpoint.Id)),
                inputs.Flights.ToDictionary(flight => flight.Id, flight => Math.Max(0,
                    (FlightScheduling.Resolve(flight, inputs.StartsAtUtc).BoardingClosesAtUtc!.Value.AddMinutes(flight.DelayMinutes) - inputs.StartsAtUtc).TotalMinutes)),
                (inputs.Navigation?.Graph.Nodes.Select(node => node.Id) ?? []).ToHashSet(),
                (inputs.Navigation?.ClosedZoneIds ?? []).ToHashSet(),
                inputs.StaffShifts is null ? null : layout.Checkpoints.Select(checkpoint => checkpoint.Id).ToHashSet(),
                RollingGates: inputs.Journey is not null);
            var demand = (int)Math.Min(int.MaxValue, inputs.Populations.Sum(item => (long)item.Count));
            foreach (var (index, field, code) in EventContractValidation.Check(events.Select(pair => pair.Event).ToArray(), context, demand))
            {
                var item = index is { } position ? events[position] : default;
                var path = index is null ? "inputs.events" : Field(item.Position, field);
                Add(errors, path, code switch
                {
                    "required" => $"{field} is required for {item.Event.Type} events.",
                    "invalid_value" when field.Length == 0 => "A resume event targets exactly one checkpoint or one corridor zone.",
                    "invalid_value" when field is "checkpointId" or "flightId" or "zoneId" && Targets(item.Event.Type).Contains(field)
                        => $"{field} must reference this scenario's layout, flights or navigation graph.",
                    "invalid_value" => $"{field} is not used by {item.Event.Type} events.",
                    "out_of_range" when index is null => "Surges would exceed 100000 modeled passengers.",
                    "out_of_range" => $"{(field == "minute" ? "The event minute" : field)} is outside the scenario horizon or the allowed range.",
                    _ => "The event contradicts the scheduled timeline."
                });
            }
        }
        if (errors.Count != 0) throw Invalid(errors);
    }

    private static string[] Targets(string type) => type switch
    {
        "passenger_surge" => ["checkpointId", "flightId"], "flight_change" => ["flightId"],
        "corridor_closure" => ["zoneId"], "resume" => ["checkpointId", "zoneId"], _ => ["checkpointId"]
    };

    private static List<(ScheduledEvent Event, int Position)> Read(ScenarioInputs inputs, Dictionary<string, List<string>> errors)
    {
        var events = new List<(ScheduledEvent, int)>();
        foreach (var (item, position) in inputs.Events.Select((item, position) => (item, position)))
        {
            if (!Operational(item.Type)) continue;
            var field = $"inputs.events[{position}]";
            var offset = (item.OccurredAtUtc - inputs.StartsAtUtc).TotalMinutes;
            if (offset < 0 || offset != Math.Floor(offset) || offset > 10079)
                Add(errors, field + ".occurredAtUtc", "Operational events start on a whole virtual minute inside the scenario.");
            if (item.GateId is not null) Add(errors, field + ".gateId", $"gateId is not used by {item.Type} events.");
            var values = new Dictionary<string, JsonElement>();
            try
            {
                using var document = JsonDocument.Parse(item.PayloadJson);
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (!Parameters[item.Type].Contains(property.Name))
                        Add(errors, field + ".payloadJson", $"{property.Name} is not a parameter of {item.Type} events.");
                    else if (!values.TryAdd(property.Name, property.Value.Clone()))
                        Add(errors, field + ".payloadJson", $"{property.Name} must be specified only once.");
                }
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            {
                Add(errors, field + ".payloadJson", "payloadJson must be a JSON object.");
                continue;
            }
            int? Number(string name)
            {
                if (!values.TryGetValue(name, out var value)) return null;
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
                Add(errors, field + ".payloadJson", $"{name} must be an integer.");
                return null;
            }
            Guid? zone = null;
            if (values.TryGetValue("zoneId", out var zoneValue))
            {
                if (zoneValue.ValueKind == JsonValueKind.String && Guid.TryParse(zoneValue.GetString(), out var parsed)) zone = parsed;
                else Add(errors, field + ".payloadJson", "zoneId must be a navigation zone UUID.");
            }
            var scheduled = new ScheduledEvent(item.Id, item.Type, (int)Math.Max(0, Math.Min(offset, 10079)), item.CheckpointId, item.FlightId, zone,
                Number("passengerCount"), Number("windowMinutes"), Number("staffCount"), Number("delayMinutes"));
            // Reuse the wire schema at the scenario boundary so saved parameters obey the engine's ranges.
            foreach (var error in VersionedContract.Validate("ScheduledEvent", JsonSerializer.SerializeToElement(scheduled, ContractJson.Options)))
                Add(errors, Field(position, error.Field.TrimStart('/')), error.Message);
            events.Add((scheduled, position));
        }
        if (events.Count > 1000) Add(errors, "inputs.events", "At most 1000 operational events can be scheduled.");
        return events;
    }

    private static string Field(int position, string field) => $"inputs.events[{position}]" + (field switch
    {
        "" => "", "eventId" => ".id", "minute" => ".occurredAtUtc", "type" => ".type",
        "checkpointId" => ".checkpointId", "flightId" => ".flightId", _ => ".payloadJson"
    });

    private static void Add(Dictionary<string, List<string>> errors, string field, string message)
    {
        if (!errors.TryGetValue(field, out var messages)) errors[field] = messages = [];
        messages.Add(message);
    }

    private static ScenarioValidationException Invalid(Dictionary<string, List<string>> errors) =>
        new(errors.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray()));
}

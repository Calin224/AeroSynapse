using System.Text.Json;
using System.Text.Json.Serialization;

namespace AeroSynapse.Application.Contracts;

// Operational event acting at the start of a virtual minute; unused fields are omitted on the wire.
public sealed record ScheduledEvent(Guid EventId, string Type, int Minute,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? CheckpointId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? FlightId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? ZoneId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? PassengerCount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? WindowMinutes = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? StaffCount = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? DelayMinutes = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? TargetCheckpointId = null);
public sealed record AppliedEvent(Guid EventId, string Type, int Minute, int Sequence);

// The state an event timeline is checked against: nominal staff, boarding closes and navigation zones.
// A checkpoint with a staff schedule takes no staff_reduction, and resume only ends its outage.
public sealed record EventContext(int Horizon, IReadOnlyDictionary<Guid, int> Staff, IReadOnlyDictionary<Guid, double> Close,
    IReadOnlySet<Guid> Zones, IReadOnlySet<Guid> StaticClosed, IReadOnlySet<Guid>? Scheduled = null, bool RollingGates = false);

/// <summary>Mirrors intelligence/app/simulation/events.py: one order, one set of field rules, one conflict replay.</summary>
public static class EventContractValidation
{
    // Infrastructure first, then capacity, then demand: a surge at T already sees T's closures.
    public static readonly IReadOnlyList<string> Types =
        ["flight_change", "corridor_closure", "checkpoint_unavailable", "staff_reduction", "resume", "passenger_surge", "queue_redistribution"];
    private static readonly string[] OptionalFields =
        ["checkpointId", "flightId", "zoneId", "passengerCount", "windowMinutes", "staffCount", "delayMinutes", "targetCheckpointId"];
    private static readonly Dictionary<string, (string[] Required, string[] Optional)> Fields = new()
    {
        ["queue_redistribution"] = (["checkpointId", "targetCheckpointId", "passengerCount", "windowMinutes"], []),
        ["passenger_surge"] = (["checkpointId", "passengerCount", "windowMinutes"], ["flightId"]),
        ["checkpoint_unavailable"] = (["checkpointId"], []),
        ["staff_reduction"] = (["checkpointId", "staffCount"], []),
        ["flight_change"] = (["flightId", "delayMinutes"], []),
        ["corridor_closure"] = (["zoneId"], []),
        ["resume"] = ([], ["checkpointId", "zoneId"]),
    };
    public const int MaxPassengers = 100000;

    public static IReadOnlyList<(ScheduledEvent Event, int Index)> Ordered(IReadOnlyList<ScheduledEvent> events) =>
        events.Select((item, index) => (item, index)).OrderBy(pair => pair.item.Minute)
            .ThenBy(pair => Priority(pair.item.Type)).ThenBy(pair => pair.item.EventId.ToString(), StringComparer.Ordinal).ToArray();
    private static int Priority(string type) => Types.ToList().IndexOf(type);

    /// <summary>Field errors as (event index, field, code); index null addresses the whole list.</summary>
    public static IReadOnlyList<(int? Index, string Field, string Code)> Check(IReadOnlyList<ScheduledEvent> events, EventContext context, int passengerCount)
    {
        var errors = new List<(int?, string, string)>();
        var surged = 0L;
        var valid = 0;
        var identifiers = new HashSet<Guid>();
        foreach (var (item, index) in events.Select((item, index) => (item, index)))
        {
            var before = errors.Count;
            if (!identifiers.Add(item.EventId)) errors.Add((index, "eventId", "duplicate_id"));
            var (required, optional) = Fields[item.Type];
            var present = Present(item);
            foreach (var name in required.Order(StringComparer.Ordinal))
                if (!present.Contains(name)) errors.Add((index, name, "required"));
            foreach (var name in OptionalFields)
                if (present.Contains(name) && !required.Contains(name) && !optional.Contains(name)) errors.Add((index, name, "invalid_value"));
            if (item.Type == "resume" && item.CheckpointId.HasValue == item.ZoneId.HasValue) errors.Add((index, "", "invalid_value"));
            if (item.Minute >= context.Horizon) errors.Add((index, "minute", "out_of_range"));
            if (item.CheckpointId is { } checkpoint && !context.Staff.ContainsKey(checkpoint)) errors.Add((index, "checkpointId", "invalid_value"));
            if (item.FlightId is { } flight && !context.Close.ContainsKey(flight)) errors.Add((index, "flightId", "invalid_value"));
            if (item.ZoneId is { } zone && !context.Zones.Contains(zone)) errors.Add((index, "zoneId", "invalid_value"));
            if (item.WindowMinutes is { } window && item.Minute + window > context.Horizon) errors.Add((index, "windowMinutes", "out_of_range"));
            if (item.Type == "flight_change" && item.DelayMinutes == 0) errors.Add((index, "delayMinutes", "out_of_range"));
            if (item.TargetCheckpointId is { } target && (!context.Staff.ContainsKey(target) || target == item.CheckpointId)) errors.Add((index, "targetCheckpointId", "invalid_value"));
            if (item.Type == "passenger_surge") surged += item.PassengerCount ?? 0;
            if (errors.Count == before) valid++;
        }
        if (passengerCount + surged > MaxPassengers) errors.Add((null, "", "out_of_range"));
        if (valid == events.Count) errors.AddRange(Conflicts(events, context).Select(pair => ((int?)pair.Index, pair.Field, "event_conflict")));
        return errors;
    }

    /// <summary>Replays the timeline; yields each event contradicting the state it meets.</summary>
    public static IEnumerable<(int Index, string Field)> Conflicts(IReadOnlyList<ScheduledEvent> events, EventContext context)
    {
        var staff = context.Staff.ToDictionary();
        var close = context.Close.ToDictionary();
        var unavailable = new HashSet<Guid>();
        var closed = context.StaticClosed.ToHashSet();
        var dynamic = new HashSet<Guid>();
        var scheduled = context.Scheduled ?? new HashSet<Guid>();
        foreach (var (item, index) in Ordered(events))
        {
            switch (item.Type)
            {
                case "checkpoint_unavailable":
                    if (!unavailable.Add(item.CheckpointId!.Value)) yield return (index, "checkpointId");
                    break;
                case "staff_reduction" when scheduled.Contains(item.CheckpointId!.Value):
                    yield return (index, "checkpointId");
                    break;
                case "staff_reduction":
                    if (item.StaffCount >= staff[item.CheckpointId!.Value]) yield return (index, "staffCount");
                    staff[item.CheckpointId.Value] = item.StaffCount!.Value;
                    break;
                case "corridor_closure":
                    if (!closed.Add(item.ZoneId!.Value)) yield return (index, "zoneId");
                    dynamic.Add(item.ZoneId.Value);
                    break;
                case "resume" when item.ZoneId is { } zone:
                    if (!dynamic.Remove(zone)) yield return (index, "zoneId");
                    closed.Remove(zone);
                    break;
                case "resume":
                    var checkpoint = item.CheckpointId!.Value;
                    if (!unavailable.Remove(checkpoint) && (staff[checkpoint] == context.Staff[checkpoint] || scheduled.Contains(checkpoint))) yield return (index, "checkpointId");
                    staff[checkpoint] = context.Staff[checkpoint];
                    break;
                case "flight_change" when !context.RollingGates:
                    var flight = item.FlightId!.Value;
                    var changed = close[flight] + item.DelayMinutes!.Value;
                    // Boarding already closed, or the new deadline would rewrite the past.
                    if (close[flight] <= item.Minute || changed < item.Minute || changed > 10080) yield return (index, "delayMinutes");
                    close[flight] = changed;
                    break;
            }
        }
    }

    /// <summary>Final boarding close per flight after every flight_change.</summary>
    public static Dictionary<Guid, double> EffectiveClose(IReadOnlyList<ScheduledEvent>? events, IReadOnlyDictionary<Guid, double> close)
    {
        var result = close.ToDictionary();
        foreach (var (item, _) in Ordered(events ?? []))
            if (item.Type == "flight_change") result[item.FlightId!.Value] += item.DelayMinutes!.Value;
        return result;
    }

    private static HashSet<string> Present(ScheduledEvent item)
    {
        var present = new HashSet<string>();
        if (item.CheckpointId.HasValue) present.Add("checkpointId");
        if (item.FlightId.HasValue) present.Add("flightId");
        if (item.ZoneId.HasValue) present.Add("zoneId");
        if (item.PassengerCount.HasValue) present.Add("passengerCount");
        if (item.WindowMinutes.HasValue) present.Add("windowMinutes");
        if (item.StaffCount.HasValue) present.Add("staffCount");
        if (item.DelayMinutes.HasValue) present.Add("delayMinutes");
        if (item.TargetCheckpointId.HasValue) present.Add("targetCheckpointId");
        return present;
    }

    /// <summary>Wire semantics for KpiSimulationRequest / KpiScenarioSnapshot.</summary>
    public static void Validate(JsonElement value, string path, List<ContractFieldError> errors)
    {
        if (!value.TryGetProperty("events", out var list)) return;
        var events = list.Deserialize<ScheduledEvent[]>(ContractJson.Options)!;
        if (events.Length == 0) return;
        if (!value.TryGetProperty("passengerGeneration", out var generation))
        {
            errors.Add(VersionedContract.Error(path + "/passengerGeneration", "required"));
            return;
        }
        if (generation.TryGetProperty("flow", out var flow) && flow.ValueKind != JsonValueKind.Null
            || value.GetProperty("engine").GetString() != KpiConventions.Engine)
        {
            errors.Add(VersionedContract.Error(path + "/events", "invalid_value"));
            return;
        }
        var zones = new HashSet<Guid>();
        var closed = new HashSet<Guid>();
        if (value.TryGetProperty("navigation", out var navigation))
        {
            foreach (var node in navigation.GetProperty("graph").GetProperty("nodes").EnumerateArray()) zones.Add(node.GetProperty("id").GetGuid());
            foreach (var zone in navigation.GetProperty("closedZoneIds").EnumerateArray()) closed.Add(zone.GetGuid());
        }
        var context = new EventContext(value.GetProperty("durationMinutes").GetInt32(),
            value.GetProperty("checkpoints").EnumerateArray().ToDictionary(cp => cp.GetProperty("checkpointId").GetGuid(), cp => cp.GetProperty("staffCount").GetInt32()),
            generation.GetProperty("flights").EnumerateArray().ToDictionary(flight => flight.GetProperty("flightId").GetGuid(), flight => flight.GetProperty("boardingCloseMinute").GetDouble()),
            zones, closed,
            value.TryGetProperty("staffSchedules", out var schedules)
                ? schedules.EnumerateArray().Select(item => item.GetProperty("checkpointId").GetGuid()).ToHashSet() : null,
            RollingGates: value.TryGetProperty("journey", out _));
        foreach (var (index, field, code) in Check(events, context, value.GetProperty("passengerCount").GetInt32()))
            errors.Add(VersionedContract.Error(index is null ? path + "/events" : $"{path}/events/{index}{(field.Length == 0 ? "" : "/" + field)}", code));
    }
}

/// <summary>A run request's event timeline, as the engine applies it: events at minute m act from m on.</summary>
public sealed class EventTimeline
{
    private readonly Dictionary<Guid, List<(int Minute, int Capacity, bool Available)>> capacity = new();
    private readonly Dictionary<string, (ScheduledEvent Event, int Count)> surges = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<(ScheduledEvent Event, int Index)> ordered;
    private readonly Dictionary<Guid, IReadOnlyList<StaffStep>> schedules;

    public EventTimeline(SimulationRequest request)
    {
        declared = request.Events is not null;
        Events = request.Events ?? [];
        ordered = EventContractValidation.Ordered(Events);
        var staff = request.Checkpoints.ToDictionary(cp => cp.CheckpointId, cp => cp.StaffCount);
        var available = staff.Keys.ToDictionary(key => key, _ => true);
        foreach (var (item, _) in ordered)
        {
            if (item.Type == "passenger_surge") surges[$"demo-surge-{item.EventId:N}-"] = (item, item.PassengerCount!.Value);
            if (item.CheckpointId is not { } checkpoint || item.Type is "passenger_surge" or "queue_redistribution") continue;
            if (item.Type == "checkpoint_unavailable") available[checkpoint] = false;
            else if (item.Type == "staff_reduction") staff[checkpoint] = item.StaffCount!.Value;
            else { available[checkpoint] = true; staff[checkpoint] = request.Checkpoints.Single(cp => cp.CheckpointId == checkpoint).StaffCount; }
            if (!capacity.TryGetValue(checkpoint, out var changes)) capacity[checkpoint] = changes = [];
            changes.Add((item.Minute, available[checkpoint] ? staff[checkpoint] : 0, available[checkpoint]));
        }
        Nominal = request.Checkpoints.ToDictionary(cp => cp.CheckpointId, cp => cp.StaffCount);
        schedules = request.StaffSchedules?.ToDictionary(item => item.CheckpointId, item => item.Steps) ?? [];
        SurgeTotal = surges.Values.Sum(item => item.Count);
        Close = EventContractValidation.EffectiveClose(Events,
            request.PassengerGeneration?.Flights.ToDictionary(flight => flight.FlightId, flight => flight.BoardingCloseMinute) ?? []);
    }

    public IReadOnlyList<ScheduledEvent> Events { get; }
    public IReadOnlyDictionary<Guid, int> Nominal { get; }
    public int SurgeTotal { get; }
    /// <summary>Final boarding close per flight after every flight change.</summary>
    public IReadOnlyDictionary<Guid, double> Close { get; }

    /// <summary>Staff on duty at a time, 0 while the control is unavailable.</summary>
    public int Capacity(Guid checkpoint, double time)
    {
        // A scheduled checkpoint follows its steps; only an outage (no staff events) overrides them.
        if (schedules.TryGetValue(checkpoint, out var steps))
            return Available(checkpoint, time) ? StaffScheduleContractValidation.PlannedAt(steps, time) : 0;
        var value = Nominal[checkpoint];
        if (capacity.TryGetValue(checkpoint, out var changes))
            foreach (var (minute, current, _) in changes) if (minute <= time) value = current;
        return value;
    }

    /// <summary>Whether the control accepts new demand at a time; staffing reductions keep it available.</summary>
    public bool Available(Guid checkpoint, double time)
    {
        var value = true;
        if (capacity.TryGetValue(checkpoint, out var changes))
            foreach (var (minute, _, current) in changes) if (minute <= time) value = current;
        return value;
    }

    /// <summary>The surge event that created a passenger, or null for generated or aggregate demand.</summary>
    public (ScheduledEvent Event, int Count)? Surge(string passengerId) =>
        passengerId.Length > 7 && surges.TryGetValue(passengerId[..^6], out var surge) ? surge : null;

    /// <summary>Whether a passenger is part of demand at a time: surge demand exists from its event minute.</summary>
    public bool Visible(ProcessPassenger person, double time) => Surge(person.Id) is not { } surge || surge.Event.Minute <= time;

    /// <summary>The applied-event log must exist when the request declares events, in application order; without events an
    /// engine that reroutes during the run may still report its empty log.</summary>
    public bool Matches(IReadOnlyList<AppliedEvent>? applied) => declared
        ? applied is not null && ordered.Select((pair, index) => new AppliedEvent(pair.Event.EventId, pair.Event.Type, pair.Event.Minute, index + 1)).SequenceEqual(applied)
        : applied is null or [];
    private readonly bool declared;
}

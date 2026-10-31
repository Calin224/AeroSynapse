using AeroSynapse.Domain.DigitalTwin;

namespace AeroSynapse.Application.Storage;

public static class ScenarioValidator
{
    public static void Validate(ScenarioWriteRequest request, SceneDetails layout)
    {
        var errors = new Dictionary<string, List<string>>();
        void Error(string field, string message)
        {
            if (!errors.TryGetValue(field, out var messages)) errors[field] = messages = [];
            messages.Add(message);
        }
        void Text(string? value, string field, int max, bool required = true)
        {
            if (value is null || (required && string.IsNullOrWhiteSpace(value))) Error(field, "A value is required.");
            else if (value.Length > max) Error(field, $"Must contain at most {max} characters.");
            else if (value.Contains('\0')) Error(field, "Must not contain NUL (U+0000) characters.");
        }
        void Utc(DateTime value, string field)
        {
            if (value == default || value.Kind != DateTimeKind.Utc) Error(field, "A UTC timestamp with the Z suffix is required.");
        }
        Text(request.Code, "code", 64);
        Text(request.Name, "name", 160);
        Text(request.Description, "description", 2000, required: false);
        if (request.SceneId != layout.Scene.Id) Error("sceneId", "The layout version belongs to a different scene.");
        var inputs = request.Inputs;
        if (inputs is null)
        {
            Error("inputs", "Scenario inputs are required.");
            Throw();
            return;
        }
        Utc(inputs.StartsAtUtc, "inputs.startsAtUtc");
        if (inputs.DurationMinutes is < 1 or > 10080) Error("inputs.durationMinutes", "Must be between 1 and 10080 minutes.");
        if (inputs.SnapshotIntervalMinutes is < 1 or > 5) Error("inputs.snapshotIntervalMinutes", "Use an interval of 1 to 5 virtual minutes.");
        if (inputs.MinimumDepartureSpacingMinutes is < 1 or > 10080)
            Error("inputs.minimumDepartureSpacingMinutes", "Use a minimum departure spacing of 1 to 10080 minutes.");
        if (inputs.Seed < 0) Error("inputs.seed", "Must be nonnegative.");
        if (inputs.TimeZone is null || (inputs.TimeZone != "UTC" && (!inputs.TimeZone.Contains('/') || !TimeZoneInfo.TryFindSystemTimeZoneById(inputs.TimeZone, out _))))
            Error("inputs.timeZone", "A supported IANA time zone is required.");
        // Check the horizon without allowing DateTime overflow to escape as a server error.
        var duration = TimeSpan.FromMinutes(Math.Clamp(inputs.DurationMinutes, 0, 10080));
        var end = inputs.StartsAtUtc;
        if (DateTime.MaxValue - inputs.StartsAtUtc < duration) Error("inputs.startsAtUtc", "The simulation end exceeds the supported timestamp range.");
        else end += duration;
        void InWindow(DateTime value, string field)
        {
            Utc(value, field);
            if (value < inputs.StartsAtUtc || value > end) Error(field, "Must fall within the scenario time window.");
        }
        var flights = Collection(inputs.Flights, "inputs.flights", x => x.Id);
        var populations = Collection(inputs.Populations, "inputs.populations", x => x.Id);
        var flows = Collection(inputs.Flows, "inputs.flows", x => x.Id);
        Collection(inputs.Events, "inputs.events", x => x.Id);
        var checkpoints = layout.Checkpoints.ToDictionary(x => x.Id);
        var gates = layout.Gates.ToDictionary(x => x.Id);
        var routes = layout.Routes.ToDictionary(x => x.Id);
        var staff = layout.Staff.ToDictionary(x => x.Id);
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (flight, index) in (inputs.Flights ?? []).Select((value, index) => (value, index)))
        {
            if (flight is null) continue;
            var field = $"inputs.flights[{index}]";
            Text(flight.Code, field + ".code", 64);
            if (!codes.Add(flight.Code?.Trim() ?? "")) Error(field + ".code", "Flight codes must be unique within the scenario.");
            if (flight.Code is { Length: > 0 } code && (code != code.Trim() || !char.IsAsciiLetterOrDigit(code[0])
                || code.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or ' ' or '/'))))
                Error(field + ".code", "Use a flight number beginning with a letter or digit, containing letters, digits, spaces, hyphens or slashes.");
            if (!gates.ContainsKey(flight.GateId)) Error(field + ".gateId", "Gate is not present in the selected layout version.");
            if (flight.PassengerCapacity <= 0) Error(field + ".passengerCapacity", "Must be positive.");
            InWindow(flight.DepartureAtUtc, field + ".departureAtUtc");
            if (!flight.IsSynthetic) Error(field + ".isSynthetic", "Flight schedules must be marked demo (isSynthetic: true).");
            if (flight.Status is null || !FlightScheduling.Statuses.Contains(flight.Status))
                Error(field + ".status", "Use scheduled, delayed, boarding, closed, departed or cancelled (demonstrative status).");
            if (flight.DelayMinutes is < 0 or > 10080) Error(field + ".delayMinutes", "Must be between 0 and 10080 minutes.");
            var resolved = FlightScheduling.Resolve(flight, inputs.StartsAtUtc);
            if (resolved.BoardingOpensAtUtc is not { } open) Error(field + ".boardingOpensAtUtc", "Both boarding timestamps are required.");
            else Utc(open, field + ".boardingOpensAtUtc");
            if (resolved.BoardingClosesAtUtc is not { } close) Error(field + ".boardingClosesAtUtc", "Both boarding timestamps are required.");
            else
            {
                Utc(close, field + ".boardingClosesAtUtc");
                if (close <= resolved.BoardingOpensAtUtc) Error(field + ".boardingClosesAtUtc", "Boarding must close after it opens.");
                if (close >= flight.DepartureAtUtc) Error(field + ".boardingClosesAtUtc", "Boarding must close strictly before departure.");
            }
            if (flight.DelayMinutes is >= 0 and <= 10080)
            {
                if (!FlightScheduling.TryDelayed(flight.DepartureAtUtc, flight.DelayMinutes, out var effectiveDeparture))
                    Error(field + ".delayMinutes", "Delayed departure exceeds the supported timestamp range.");
                else if (inputs.Journey is null) InWindow(effectiveDeparture, field + ".departureAtUtc");
            }
        }
        if (inputs.MinimumDepartureSpacingMinutes is >= 1 and <= 10080)
        {
            var minimum = inputs.MinimumDepartureSpacingMinutes.Value;
            var departures = (inputs.Flights ?? []).Select((flight, index) => (Flight: flight, Index: index))
                .Where(row => row.Flight is not null && row.Flight.Status != "cancelled"
                    && row.Flight.DepartureAtUtc != default && row.Flight.DepartureAtUtc.Kind == DateTimeKind.Utc
                    && FlightScheduling.TryDelayed(row.Flight.DepartureAtUtc, row.Flight.DelayMinutes, out _))
                .Select(row => (row.Flight, row.Index, Time: row.Flight.DepartureAtUtc.AddMinutes(row.Flight.DelayMinutes)))
                .OrderBy(row => row.Time).ToArray();
            for (var index = 1; index < departures.Length; index++)
            {
                var first = departures[index - 1];
                var second = departures[index];
                if (second.Time - first.Time >= TimeSpan.FromMinutes(minimum)) continue;
                var message = $"Plecările {first.Flight.Code} și {second.Flight.Code} trebuie distanțate cu minimum {minimum} de minute, inclusiv întârzierile configurate.";
                Error($"inputs.flights[{first.Index}].departureAtUtc", message);
                Error($"inputs.flights[{second.Index}].departureAtUtc", message);
            }
        }
        foreach (var conflict in inputs.Journey is not null ? [] : FlightScheduling.Conflicts(inputs with { Flights = (inputs.Flights ?? []).Where(x => x is not null).ToArray() }))
        {
            var first = flights.GetValueOrDefault(conflict.FirstFlightId);
            var second = flights.GetValueOrDefault(conflict.SecondFlightId);
            gates.TryGetValue(conflict.GateId, out var gate);
            Error("inputs.flights", $"Demo gate {gate?.Code ?? conflict.GateId.ToString()} is requested by {first?.Code} and {second?.Code} simultaneously ({conflict.StartsAtUtc:O}–{conflict.EndsAtUtc:O}).");
        }
        foreach (var (population, index) in (inputs.Populations ?? []).Select((value, index) => (value, index)))
        {
            if (population is null) continue;
            Text(population.Name, $"inputs.populations[{index}].name", 160);
            if (population.Count <= 0) Error($"inputs.populations[{index}].count", "Must be positive.");
        }
        foreach (var (flow, index) in (inputs.Flows ?? []).Select((value, index) => (value, index)))
        {
            if (flow is null) continue;
            var field = $"inputs.flows[{index}]";
            if (!populations.ContainsKey(flow.PopulationId)) Error(field + ".populationId", "Population is not present in this scenario.");
            if (!flights.TryGetValue(flow.FlightId, out var flight)) Error(field + ".flightId", "Flight is not present in this scenario.");
            if (!routes.TryGetValue(flow.RouteId, out var route)) Error(field + ".routeId", "Route is not present in the selected layout version.");
            if (route is not null && flight is not null && route.ToGateId != flight.GateId)
                Error(field + ".routeId", "Route must lead to the flight's gate.");
            if (flow.PassengerCount <= 0) Error(field + ".passengerCount", "Must be positive.");
            InWindow(flow.StartsAtUtc, field + ".startsAtUtc");
            InWindow(flow.EndsAtUtc, field + ".endsAtUtc");
            if (flow.EndsAtUtc <= flow.StartsAtUtc) Error(field + ".endsAtUtc", "Must be after the flow start.");
            if (flight is not null && FlightScheduling.Resolve(flight, inputs.StartsAtUtc).BoardingClosesAtUtc is { } boardingClose
                && FlightScheduling.TryDelayed(boardingClose, flight.DelayMinutes, out var effectiveClose) && flow.EndsAtUtc > effectiveClose)
                Error(field + ".endsAtUtc", "Flow must finish by the flight's effective boarding closure.");
        }
        foreach (var population in populations.Values)
            if (flows.Values.Where(x => x.PopulationId == population.Id).Sum(x => (long)x.PassengerCount) > population.Count)
                Error("inputs.flows", $"Passenger allocations exceed population {population.Id}'s available count ({population.Count}).");
        foreach (var flight in flights.Values)
            if (flows.Values.Where(x => x.FlightId == flight.Id).Sum(x => (long)x.PassengerCount) > flight.PassengerCapacity)
                Error("inputs.flows", $"Passenger allocations exceed flight {flight.Id}'s capacity ({flight.PassengerCapacity}).");
        if (inputs.StaffAllocations is null) Error("inputs.staffAllocations", "An array is required.");
        var allocated = new HashSet<Guid>();
        foreach (var (allocation, index) in (inputs.StaffAllocations ?? []).Select((value, index) => (value, index)))
        {
            var field = $"inputs.staffAllocations[{index}]";
            if (allocation is null) { Error(field, "An allocation is required."); continue; }
            if (!allocated.Add(allocation.StaffMemberId)) Error(field + ".staffMemberId", "A staff member can be allocated only once per scenario.");
            if (!staff.TryGetValue(allocation.StaffMemberId, out var member)) Error(field + ".staffMemberId", "Staff member is not available in the selected layout version.");
            if (!checkpoints.TryGetValue(allocation.CheckpointId, out var checkpoint)) Error(field + ".checkpointId", "Checkpoint is not present in the selected layout version.");
            if (member is not null && checkpoint is not null && member.TerminalId != checkpoint.TerminalId)
                Error(field + ".checkpointId", "Staff member and checkpoint must belong to the same terminal.");
        }
        foreach (var (item, index) in (inputs.Events ?? []).Select((value, index) => (value, index)))
        {
            if (item is null) continue;
            var field = $"inputs.events[{index}]";
            Text(item.Type, field + ".type", 64);
            InWindow(item.OccurredAtUtc, field + ".occurredAtUtc");
            if (item.CheckpointId.HasValue && !checkpoints.ContainsKey(item.CheckpointId.Value)) Error(field + ".checkpointId", "Checkpoint is not present in the selected layout version.");
            if (item.GateId.HasValue && !gates.ContainsKey(item.GateId.Value)) Error(field + ".gateId", "Gate is not present in the selected layout version.");
            if (item.FlightId.HasValue && !flights.ContainsKey(item.FlightId.Value)) Error(field + ".flightId", "Flight is not present in this scenario.");
            gates.TryGetValue(item.GateId ?? Guid.Empty, out var eventGate);
            flights.TryGetValue(item.FlightId ?? Guid.Empty, out var eventFlight);
            if (eventGate is not null && eventFlight is not null && eventGate.Id != eventFlight.GateId)
                Error(field + ".gateId", "Event gate must match the referenced flight's gate.");
            var targetGate = eventGate;
            if (targetGate is null && eventFlight is not null) gates.TryGetValue(eventFlight.GateId, out targetGate);
            if (targetGate is not null && checkpoints.TryGetValue(item.CheckpointId ?? Guid.Empty, out var eventCheckpoint)
                && targetGate.TerminalId != eventCheckpoint.TerminalId)
                Error(field + ".checkpointId", "Event targets must belong to the same terminal.");
            try
            {
                RunResultValidator.ObjectJson(item.PayloadJson, field + ".payloadJson");
            }
            catch (ScenarioValidationException exception)
            {
                foreach (var (key, messages) in exception.Errors)
                    foreach (var message in messages) Error(key, message);
            }
        }
        Throw();
        if (inputs.PassengerGeneration is not null || IncidentScenarios.DerivesProfiles(inputs)) PassengerScenarios.Validate(inputs, layout);
        CheckInScenarios.Validate(inputs, layout);
        OperatorScenarios.Validate(inputs, layout);
        StaffShiftScenarios.Validate(inputs, layout);
        NavigationScenarios.Validate(inputs,layout);
        ScheduledEvents.Validate(inputs, layout);
        IntegratedResourceScenarios.Validate(inputs, layout);
        JourneyScenarios.Validate(inputs, layout);
        AgentScenarios.Validate(inputs, layout);

        Dictionary<Guid, T> Collection<T>(IReadOnlyList<T>? items, string field, Func<T, Guid> id) where T : class
        {
            var values = new Dictionary<Guid, T>();
            if (items is null) { Error(field, "An array is required."); return values; }
            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                if (item is null) { Error($"{field}[{index}]", "An object is required."); continue; }
                var key = id(item);
                if (key == Guid.Empty) Error($"{field}[{index}].id", "A nonempty identifier is required.");
                if (!values.TryAdd(key, item)) Error($"{field}[{index}].id", "Identifiers must be unique within this collection.");
            }
            return values;
        }
        void Throw()
        {
            if (errors.Count > 0) throw new ScenarioValidationException(errors.ToDictionary(x => x.Key, x => x.Value.ToArray()));
        }
    }
}

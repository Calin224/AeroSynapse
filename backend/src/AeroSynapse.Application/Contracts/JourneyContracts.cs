using System.Text.Json;

namespace AeroSynapse.Application.Contracts;

public sealed record JourneyFlight(Guid FlightId, Guid GateId, double ArrivalMinute, double BoardingOpenMinute,
    double BoardingCloseMinute, double DepartureMinute, int DelayMinutes, int PassengerCapacity, bool IsCancelled,
    bool RequiresPassport, Guid? PreviousFlightId, int RotationMinutes, IReadOnlyList<Guid> EligibleGateIds);
public sealed record JourneyConnection(Guid Id, Guid InboundFlightId, Guid OutboundFlightId, int PassengerCount,
    double TransferMinutes, bool RequiresPassport);
public sealed record JourneyRoute(Guid CheckpointId, Guid GateId, double WalkingMinutes);
public sealed record JourneyInput(string JourneyVersion, int PassportStaffCount, double PassportServiceMinutes,
    int BoardingStaffPerGate, double BoardingServiceMinutes, double GateChangeWalkingMinutes, double RiskBufferMinutes,
    IReadOnlyList<JourneyFlight> Flights, IReadOnlyList<JourneyConnection> Connections, IReadOnlyList<JourneyRoute> Routes,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<JourneyGateChoice>? GateChoices = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<JourneyGateOpening>? GateOpenings = null);
/// <summary>A staff member opens a gate no flight is planned at; it adds a boarding lane for the flight from the minute on.</summary>
public sealed record JourneyGateOpening(Guid FlightId, Guid GateId, Guid StaffMemberId, int Minute);
/// <summary>How a flight_change is absorbed: "optimal" (rolling optimum) or "keep_gates" (every flight at its gate, when possible).</summary>
public sealed record JourneyGateChoice(Guid EventId, string Mode);
public sealed record JourneyVisit(string Stage, Guid ResourceId, double ArrivalMinute, double? StartedMinute, double? FinishedMinute);
public sealed record JourneyStep(double Minute, string Stage, Guid? GateId);
public sealed record JourneyPassenger(string Id, Guid? FlightId, Guid? ConnectionId, Guid? InboundFlightId,
    double? ArrivalMinute, Guid? GateId, double? GateArrivalMinute, double? BoardedMinute, string Stage,
    string? ConnectionStatus, IReadOnlyList<JourneyVisit> Visits, IReadOnlyList<JourneyStep> History);
public sealed record JourneyFlightPlan(Guid FlightId, Guid GateId, double ArrivalMinute, double BoardingOpenMinute,
    double BoardingCloseMinute, double DepartureMinute, double DelayMinutes, bool IsCancelled);
public sealed record JourneyReplan(double Minute, Guid? EventId, string SolverStatus, IReadOnlyList<JourneyFlightPlan> Flights);
public sealed record JourneySnapshot(int Minute, int NotArrivedCount, int PassportQueuedCount, int PassportBusyCount,
    int GateQueuedCount, int BoardingCount, int BoardedCount, int MissedConnectionCount, int AtRiskConnectionCount,
    int DelayedFlightCount, double TotalDelayMinutes);
public sealed record JourneyResult(string JourneyVersion, int HorizonMinutes, int PopulationCount, int ConnectionPassengerCount,
    int BoardedCount, int MissedConnectionCount, IReadOnlyList<JourneyReplan> Plans, IReadOnlyList<JourneySnapshot> Snapshots,
    IReadOnlyList<JourneyPassenger> Passengers, SimulationResponse UpstreamResult);
public sealed record JourneyReport(string JourneyVersion, int HorizonMinutes, int PopulationCount, int ConnectionPassengerCount,
    int BoardedCount, int MissedConnectionCount, IReadOnlyList<JourneyReplan> Plans, IReadOnlyList<JourneySnapshot> Snapshots,
    IReadOnlyList<JourneyPassenger> Passengers, int Offset, int ReturnedCount, bool HasMore);

public static class JourneyConventions
{
    public const string Engine = "journey-1.0";

    // Transfers are a subset of the outgoing cohort. The existing engine owns the remaining
    // check-in/security trace; flight changes and airside routing belong to the journey clock.
    public static SimulationRequest Upstream(SimulationRequest request)
    {
        var transfers = request.Journey!.Connections.GroupBy(c => c.OutboundFlightId)
            .ToDictionary(g => g.Key, g => g.Sum(c => c.PassengerCount));
        var population = request.PassengerCount - transfers.Values.Sum();
        return request with
        {
            Journey = null, Navigation = null, PassengerCount = population, ArrivalRatePerMinute = (double)population / request.DurationMinutes,
            Events = (request.Events ?? []).Where(e => e.Type is not ("flight_change" or "corridor_closure") && !(e.Type == "resume" && e.ZoneId is not null)).ToArray(),
            PassengerGeneration = request.PassengerGeneration! with
            {
                Batches = request.PassengerGeneration!.Batches.Select(b => transfers.TryGetValue(b.FlightId, out var count)
                    ? b with { ModeledCount = b.ModeledCount - count, CountMode = "subset" } : b).ToArray()
            }
        };
    }
}

public static class JourneyContractValidation
{
    public static void Request(JsonElement value, string path, List<ContractFieldError> errors)
    {
        if (errors.Count != 0) return; // Parent profiles must be valid before building ID maps.
        if (!value.TryGetProperty("journey", out var raw)) return;
        var input = raw.Deserialize<JourneyInput>(ContractJson.Options)!;
        void Invalid(string field = "", string code = "invalid_value") => errors.Add(VersionedContract.Error(path + "/journey" + field, code));
        if (!value.TryGetProperty("passengerGeneration", out var pg) || pg.TryGetProperty("flow", out _)) { Invalid(); return; }
        var generation = pg.Deserialize<PassengerGenerationRequest>(ContractJson.Options)!;
        var events = value.TryGetProperty("events", out var ev) ? ev.Deserialize<ScheduledEvent[]>(ContractJson.Options)! : [];
        var interval = value.TryGetProperty("snapshotIntervalMinutes", out var step) ? (int)step.GetDouble() : 1;
        var grid = ((int)value.GetProperty("durationMinutes").GetDouble() + interval - 1) / interval + 1;
        if (grid * (generation.Batches.Sum(b => (long)b.ModeledCount) + events.Sum(e => (long)(e.PassengerCount ?? 0))) > 10_000_000)
            Invalid("", "out_of_range");
        if (input.Flights.Select(f => f.FlightId).Distinct().Count() != input.Flights.Count) { Invalid("/flights", "duplicate_id"); return; }
        var flights = input.Flights.ToDictionary(f => f.FlightId);
        var predecessors = input.Flights.Where(f => f.PreviousFlightId is not null).Select(f => f.PreviousFlightId).ToArray();
        if (predecessors.Distinct().Count() != predecessors.Length) Invalid("/flights");
        var profiles = generation.Flights.ToDictionary(f => f.FlightId);
        if (!flights.Keys.ToHashSet().SetEquals(profiles.Keys)) Invalid("/flights");
        if (input.Connections.Select(c => c.Id).Distinct().Count() != input.Connections.Count) Invalid("/connections", "duplicate_id");
        var routes = input.Routes.Select(r => (r.CheckpointId, r.GateId)).ToHashSet();
        if (routes.Count != input.Routes.Count) Invalid("/routes", "duplicate_id");
        var checkpoints = generation.Checkpoints.Select(c => c.CheckpointId).ToHashSet();
        if (input.Routes.Any(r => !checkpoints.Contains(r.CheckpointId))) Invalid("/routes");
        foreach (var f in input.Flights)
        {
            if (!(f.ArrivalMinute <= f.BoardingOpenMinute && f.BoardingOpenMinute < f.BoardingCloseMinute && f.BoardingCloseMinute < f.DepartureMinute
                && f.DepartureMinute <= (int)value.GetProperty("durationMinutes").GetDouble())) Invalid("/flights");
            if (new[] { f.ArrivalMinute, f.BoardingOpenMinute, f.BoardingCloseMinute, f.DepartureMinute }.Any(t => Math.Abs(t * 60 - Math.Round(t * 60)) > 1e-6)) Invalid("/flights");
            if (!f.EligibleGateIds.Contains(f.GateId) || f.EligibleGateIds.Distinct().Count() != f.EligibleGateIds.Count) Invalid("/flights");
            if (profiles.TryGetValue(f.FlightId, out var profile))
            {
                if (profile.IsCancelled != f.IsCancelled || profile.PassengerCapacity != f.PassengerCapacity
                    || Math.Abs(profile.BoardingCloseMinute - Math.Max(0, f.BoardingCloseMinute + f.DelayMinutes)) > 1e-6) Invalid("/flights");
                var origins = (profile.CheckpointRoutes?.Select(r => r.CheckpointId) ?? []).Append(profile.CheckpointId).Distinct();
                if (origins.Any(cp => f.EligibleGateIds.Any(g => !routes.Contains((cp, g))))) Invalid("/routes");
            }
            var previous = f.PreviousFlightId;
            if (previous is { } id && (!flights.TryGetValue(id, out var predecessor) || predecessor.IsCancelled || f.IsCancelled)) Invalid("/flights");
            var seen = new HashSet<Guid> { f.FlightId };
            while (previous is { } key && flights.TryGetValue(key, out var parent))
            {
                if (!seen.Add(key)) { Invalid("/flights"); break; }
                previous = parent.PreviousFlightId;
            }
        }
        foreach (var c in input.Connections)
        {
            if (!flights.TryGetValue(c.InboundFlightId, out var inbound) || !flights.TryGetValue(c.OutboundFlightId, out var outbound)
                || inbound.IsCancelled || outbound.IsCancelled || inbound.FlightId == outbound.FlightId) { Invalid("/connections"); continue; }
            if (inbound.ArrivalMinute < 0 || inbound.ArrivalMinute >= outbound.BoardingCloseMinute) Invalid("/connections");
        }
        var counts = generation.Batches.ToDictionary(b => b.FlightId, b => b.ModeledCount);
        if (input.Connections.GroupBy(c => c.OutboundFlightId).Any(g => g.Sum(c => (long)c.PassengerCount) > counts.GetValueOrDefault(g.Key))) Invalid("/connections");
        if (input.Connections.GroupBy(c => c.InboundFlightId).Any(g => g.Sum(c => (long)c.PassengerCount) > flights.GetValueOrDefault(g.Key)?.PassengerCapacity)) Invalid("/connections");
        if (events.Any(e => e.Type == "passenger_surge" && e.FlightId is null)) Invalid();
        var changes = events.Where(e => e.Type == "flight_change").ToArray();
        if (changes.Length > 100) Invalid("/flights", "out_of_range");
        if (changes.Any(e => e.DelayMinutes <= 0 || e.FlightId is not { } id || !flights.TryGetValue(id, out var f) || f.IsCancelled)) Invalid("/flights");
        var choices = input.GateChoices?.Select(c => c.EventId).ToArray() ?? [];
        if (choices.Distinct().Count() != choices.Length || !choices.ToHashSet().IsSubsetOf(changes.Select(e => e.EventId))) Invalid("/gateChoices");
        var openings = input.GateOpenings ?? [];
        var duration = (int)value.GetProperty("durationMinutes").GetDouble();
        bool Occupied(JourneyGateOpening o) => flights.TryGetValue(o.FlightId, out var own) && input.Flights.Any(f => f.FlightId != o.FlightId
            && f.GateId == o.GateId && !f.IsCancelled && f.ArrivalMinute < own.DepartureMinute && f.DepartureMinute > o.Minute);
        if (openings.Select(o => o.GateId).Distinct().Count() != openings.Count || openings.Select(o => o.StaffMemberId).Distinct().Count() != openings.Count
            || openings.Any(o => !flights.TryGetValue(o.FlightId, out var f) || f.IsCancelled || !f.EligibleGateIds.Contains(o.GateId) || f.GateId == o.GateId
                || Occupied(o) || o.Minute >= duration))
            Invalid("/gateOpenings");
    }
}

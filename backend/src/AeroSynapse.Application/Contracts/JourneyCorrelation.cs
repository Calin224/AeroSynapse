namespace AeroSynapse.Application.Contracts;

/// <summary>Validate the captured upstream trace, gate occupancy, immutable past and passenger outcomes before persistence.</summary>
public static class JourneyCorrelation
{
    private static bool Near(double? a, double? b) => KpiContractValidation.Near(a, b);

    public static bool Valid(SimulationRequest request, SimulationResponse response)
    {
        if (request.Journey is not { } input) return response.JourneyResult is null;
        if (response.Status != "completed") return response.JourneyResult is null;
        if (response.JourneyResult is not { } result || response.ProcessResult is not null || response.KpiResult is not null
            || response.NavigationResult is not null || response.CheckInResult is not null || response.SimulatedMinutes != request.DurationMinutes) return false;
        if (result.HorizonMinutes != request.DurationMinutes || result.JourneyVersion != "1.0"
            || result.PopulationCount != request.PassengerCount + new EventTimeline(request).SurgeTotal
            || result.Passengers.Count != result.PopulationCount || result.Passengers.Select(p => p.Id).Distinct().Count() != result.PopulationCount
            || result.ConnectionPassengerCount != input.Connections.Sum(c => c.PassengerCount)
            || result.BoardedCount != result.Passengers.Count(p => p.BoardedMinute is not null)
            || result.MissedConnectionCount != result.Passengers.Count(p => p.ConnectionStatus == "missed")) return false;
        var upstreamRequest = JourneyConventions.Upstream(request);
        var upstream = result.UpstreamResult;
        if (ContractCorrelation.Validate(upstreamRequest, upstream).Count != 0 || upstream.Status != "completed"
            || !response.Metrics.SequenceEqual(upstream.Metrics) || !response.Snapshots.SequenceEqual(upstream.Snapshots)) return false;
        if (!Plans(request, result)) return false;
        var flights = input.Flights.ToDictionary(f => f.FlightId);
        var connections = input.Connections.ToDictionary(c => c.Id);
        var security = upstream.ProcessResult!.Passengers.ToDictionary(p => p.Id);
        var desks = (upstream.CheckInResult?.Passengers ?? []).ToDictionary(p => p.Id);
        var expected = security.Keys.Concat(desks.Keys).ToHashSet();
        foreach (var c in input.Connections)
            for (var i = 1; i <= c.PassengerCount; i++) expected.Add($"demo-transfer-{c.Id:N}-{i:000000}");
        if (!expected.SetEquals(result.Passengers.Select(p => p.Id))) return false;
        var horizon = request.DurationMinutes;
        foreach (var p in result.Passengers)
        {
            if (p.FlightId is not { } id || !flights.TryGetValue(id, out var flight) || flight.IsCancelled
                || p.ArrivalMinute > horizon || p.GateArrivalMinute > horizon || p.BoardedMinute > horizon
                || (p.Stage == "boarded") != (p.BoardedMinute is not null)
                || p.History.Count == 0 || p.History[0] != new JourneyStep(0, "not_arrived", null)
                || p.History[^1].Stage != p.Stage || p.History.Zip(p.History.Skip(1)).Any(pair => pair.First.Minute > pair.Second.Minute)
                || p.History.Any(step => step.Minute < 0 || step.Minute > horizon)) return false;
            JourneyConnection? connection = null;
            double? ready;
            if (p.ConnectionId is { } cid)
            {
                if (!connections.TryGetValue(cid, out connection) || connection.OutboundFlightId != id
                    || p.InboundFlightId != connection.InboundFlightId || !p.Id.StartsWith($"demo-transfer-{cid:N}-", StringComparison.Ordinal)
                    || p.Visits.Any(v => v.Stage is "security" or "check_in")) return false;
                var arrival = PlanAt(result, p.ArrivalMinute ?? horizon).Flights.Single(f => f.FlightId == connection.InboundFlightId).ArrivalMinute;
                if (!Near(p.ArrivalMinute, arrival <= horizon ? arrival : null)) return false;
                ready = p.ArrivalMinute;
            }
            else
            {
                if (p.InboundFlightId is not null || p.ConnectionStatus is not null) return false;
                security.TryGetValue(p.Id, out var s); desks.TryGetValue(p.Id, out var desk);
                if (s is null && desk is null || s is not null && s.FlightId != id) return false;
                if (!Near(p.ArrivalMinute, desk?.TerminalArrivalMinute ?? s!.ArrivalMinute)) return false;
                if (!VisitMatches(p, "check_in", desk?.TerminalArrivalMinute, desk?.StartedMinute, desk?.FinishedMinute,
                        upstream.CheckInResult?.ZoneId) || !VisitMatches(p, "security", s?.ArrivalMinute,
                        s?.ProcessingStartedMinute, s?.SecurityClearedMinute, s?.CheckpointId)) return false;
                ready = s?.SecurityClearedMinute;
            }
            var passport = flight.RequiresPassport || connection?.RequiresPassport == true;
            var visits = p.Visits.Where(v => v.Stage == "passport").ToArray();
            if (visits.Length != (passport && ready is not null ? 1 : 0)) return false;
            if (visits.Length == 1)
            {
                if (visits[0].ResourceId != request.ScenarioId || !Near(visits[0].ArrivalMinute, ready)
                    || !Service(visits[0], input.PassportServiceMinutes, horizon)) return false;
                ready = visits[0].FinishedMinute;
            }
            if (p.GateArrivalMinute is { } gateTime && (ready is null || gateTime < ready || p.GateId is null
                || p.GateId != PlanAt(result, gateTime).Flights.Single(f => f.FlightId == id).GateId)) return false;
            var boarding = p.Visits.Where(v => v.Stage == "boarding").ToArray();
            if (boarding.Length > 1 || p.Visits.Count != p.Visits.Select(v => v.Stage).Distinct().Count()) return false;
            if (boarding.Length == 1)
            {
                var v = boarding[0];
                if (v.StartedMinute is not { } start || !Service(v, input.BoardingServiceMinutes, horizon)
                    || !Near(v.ArrivalMinute, p.GateArrivalMinute) || !Near(v.FinishedMinute, p.BoardedMinute)) return false;
                var f = PlanAt(result, start).Flights.Single(f => f.FlightId == id);
                if (v.ResourceId != p.GateId || v.ResourceId != f.GateId || start < f.BoardingOpenMinute
                    || start + input.BoardingServiceMinutes > f.BoardingCloseMinute + 1e-9) return false;
            }
            else if (p.BoardedMinute is not null) return false;
            foreach (var v in p.Visits)
                if (v.ArrivalMinute < 0 || v.ArrivalMinute > horizon || v.StartedMinute < v.ArrivalMinute || v.StartedMinute > horizon
                    || v.FinishedMinute is not null && (v.StartedMinute is null || v.FinishedMinute < v.StartedMinute || v.FinishedMinute > horizon)) return false;
            if (connection is not null && p.ConnectionStatus != Risk(input, result, p, connection, horizon)) return false;
        }
        foreach (var stage in new[] { "passport", "boarding" })
            foreach (var group in result.Passengers.SelectMany(p => p.Visits).Where(v => v.Stage == stage).GroupBy(v => v.ResourceId))
                if (!Capacity(group, stage == "passport" ? input.PassportStaffCount : input.BoardingStaffPerGate)) return false;
        return Snapshots(request, result);
    }

    private static bool VisitMatches(JourneyPassenger p, string stage, double? arrival, double? start, double? finish, Guid? resource)
    {
        var visits = p.Visits.Where(v => v.Stage == stage).ToArray();
        return arrival is null ? visits.Length == 0 : visits.Length == 1 && visits[0].ResourceId == resource
            && Near(visits[0].ArrivalMinute, arrival) && Near(visits[0].StartedMinute, start) && Near(visits[0].FinishedMinute, finish);
    }

    private static bool Service(JourneyVisit v, double duration, int horizon) => v.StartedMinute is not { } start
        ? v.FinishedMinute is null : Near(v.FinishedMinute, start + duration <= horizon ? start + duration : null);

    private static bool Capacity(IEnumerable<JourneyVisit> visits, int capacity)
    {
        var busy = 0;
        foreach (var change in visits.Where(v => v.StartedMinute is not null).SelectMany(v => v.FinishedMinute is { } end
            ? new[] { (Time: v.StartedMinute!.Value, Change: 1), (Time: end, Change: -1) } : [(Time: v.StartedMinute!.Value, Change: 1)])
            .OrderBy(c => c.Time).ThenBy(c => c.Change))
            if ((busy += change.Change) < 0 || busy > capacity) return false;
        return true;
    }

    private static bool Plans(SimulationRequest request, JourneyResult result)
    {
        var input = request.Journey!;
        var events = EventContractValidation.Ordered(request.Events ?? []).Select(pair => pair.Event).Where(e => e.Type == "flight_change").ToArray();
        if (result.Plans.Count != events.Length + 1 || result.Plans[0].Minute != 0 || result.Plans[0].EventId is not null) return false;
        var original = input.Flights.ToDictionary(f => f.FlightId);
        var previous = input.Flights.ToDictionary(f => f.FlightId, f => new JourneyFlightPlan(f.FlightId, f.GateId,
            f.ArrivalMinute + f.DelayMinutes, f.BoardingOpenMinute + f.DelayMinutes, f.BoardingCloseMinute + f.DelayMinutes,
            f.DepartureMinute + f.DelayMinutes, f.DelayMinutes, f.IsCancelled));
        for (var i = 0; i < result.Plans.Count; i++)
        {
            var plan = result.Plans[i]; var ev = i == 0 ? null : events[i - 1];
            if (ev is not null && (plan.Minute != ev.Minute || plan.EventId != ev.EventId)) return false;
            if (plan.SolverStatus is not ("optimal" or "feasible") || plan.Flights.Count != original.Count
                || plan.Flights.Select(f => f.FlightId).Distinct().Count() != original.Count
                || !plan.Flights.Select(f => f.FlightId).ToHashSet().SetEquals(original.Keys)) return false;
            var current = plan.Flights.ToDictionary(f => f.FlightId);
            foreach (var f in plan.Flights)
            {
                var before = previous[f.FlightId]; var source = original[f.FlightId];
                if (f.IsCancelled != source.IsCancelled || !source.EligibleGateIds.Contains(f.GateId)
                    || !Near(f.DelayMinutes, f.DepartureMinute - source.DepartureMinute) || f.DelayMinutes < before.DelayMinutes
                    || f.DepartureMinute > 30240 || !(f.ArrivalMinute <= f.BoardingOpenMinute && f.BoardingOpenMinute < f.BoardingCloseMinute && f.BoardingCloseMinute < f.DepartureMinute)) return false;
                if (source.IsCancelled) { if (f != before) return false; continue; }
                if (before.DepartureMinute < plan.Minute && f != before) return false;
                if (before.ArrivalMinute < plan.Minute && (f.GateId != before.GateId || f.ArrivalMinute != before.ArrivalMinute)) return false;
                foreach (var (oldTime, newTime, scheduled) in new[] { (before.ArrivalMinute, f.ArrivalMinute, source.ArrivalMinute),
                    (before.BoardingOpenMinute, f.BoardingOpenMinute, source.BoardingOpenMinute), (before.BoardingCloseMinute, f.BoardingCloseMinute, source.BoardingCloseMinute) })
                    if (!Near(newTime, oldTime < plan.Minute || before.DepartureMinute < plan.Minute ? oldTime : scheduled + f.DelayMinutes)) return false;
                if (ev?.FlightId == f.FlightId && (before.DepartureMinute < plan.Minute
                    || f.DepartureMinute < before.DepartureMinute + ev.DelayMinutes)) return false;
                if (source.PreviousFlightId is { } predecessor && f.ArrivalMinute < current[predecessor].DepartureMinute + source.RotationMinutes) return false;
            }
            foreach (var gate in plan.Flights.Where(f => !f.IsCancelled).GroupBy(f => f.GateId))
            {
                var ordered = gate.OrderBy(f => f.ArrivalMinute).ToArray();
                if (ordered.Zip(ordered.Skip(1)).Any(pair => pair.First.DepartureMinute > pair.Second.ArrivalMinute)) return false;
            }
            previous = current;
        }
        return true;
    }

    private static JourneyReplan PlanAt(JourneyResult result, double minute) => result.Plans.Last(p => p.Minute <= minute);
    private static string StageAt(JourneyPassenger p, double minute) => p.History.Last(h => h.Minute <= minute).Stage;
    private static string Risk(JourneyInput input, JourneyResult result, JourneyPassenger p, JourneyConnection c, double minute)
    {
        if (p.BoardedMinute <= minute) return "boarded";
        var plan = PlanAt(result, minute);
        var close = plan.Flights.Single(f => f.FlightId == p.FlightId).BoardingCloseMinute;
        if (close <= minute) return "missed";
        var stage = StageAt(p, minute);
        var remaining = input.BoardingServiceMinutes;
        if (stage is "not_arrived" or "passport" or "walking" or "unreachable") remaining += c.TransferMinutes;
        if (stage == "not_arrived" && (c.RequiresPassport || input.Flights.Single(f => f.FlightId == p.FlightId).RequiresPassport)) remaining += input.PassportServiceMinutes;
        var ready = Math.Max(minute, plan.Flights.Single(f => f.FlightId == c.InboundFlightId).ArrivalMinute) + remaining;
        return ready + input.RiskBufferMinutes >= close ? "at_risk" : "pending";
    }

    private static bool Snapshots(SimulationRequest request, JourneyResult result)
    {
        if (!result.Snapshots.Select(s => s.Minute).SequenceEqual(KpiConventions.Minutes(request.DurationMinutes, request.SnapshotIntervalMinutes ?? 1))) return false;
        var input = request.Journey!; var people = result.Passengers;
        var connections = input.Connections.ToDictionary(c => c.Id);
        foreach (var s in result.Snapshots)
        {
            var busy = people.SelectMany(p => p.Visits).Count(v => v.Stage == "passport" && v.StartedMinute <= s.Minute && !(v.FinishedMinute <= s.Minute));
            var risks = people.Where(p => p.ConnectionId is not null).Select(p => Risk(input, result, p, connections[p.ConnectionId!.Value], s.Minute)).ToArray();
            var plan = PlanAt(result, s.Minute).Flights.Where(f => !f.IsCancelled).ToArray();
            if (s.NotArrivedCount != people.Count(p => !(p.ArrivalMinute <= s.Minute)) || s.PassportBusyCount != busy
                || s.PassportQueuedCount != people.Count(p => StageAt(p, s.Minute) == "passport") - busy
                || s.GateQueuedCount != people.Count(p => StageAt(p, s.Minute) == "gate_queue")
                || s.BoardingCount != people.Count(p => StageAt(p, s.Minute) == "boarding")
                || s.BoardedCount != people.Count(p => p.BoardedMinute <= s.Minute)
                || s.MissedConnectionCount != risks.Count(r => r == "missed") || s.AtRiskConnectionCount != risks.Count(r => r == "at_risk")
                || s.DelayedFlightCount != plan.Count(f => f.DelayMinutes > 0) || !Near(s.TotalDelayMinutes, plan.Sum(f => f.DelayMinutes))) return false;
        }
        return true;
    }
}

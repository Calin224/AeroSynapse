namespace AeroSynapse.Application.Contracts;

public static class ProcessCorrelation
{
    public static bool Valid(SimulationRequest request, SimulationResponse response)
    {
        // Stage routes have no single-control process report; every other captured SimPy run must return one.
        if (response.ProcessResult is not { } report)
            return request.Engine is null || request.PassengerGeneration?.Flow is not null || response.Status != "completed";
        var timeline = new EventTimeline(request);
        if (request.Engine != report.Engine || report.HorizonMinutes != request.DurationMinutes || response.SimulatedMinutes != request.DurationMinutes
            || report.PopulationCount != request.PassengerCount + timeline.SurgeTotal || report.Offset != 0 || report.HasMore
            || report.ReturnedCount != report.ArrivedCount || !timeline.Matches(report.Events)
            || report.Mode != (request.PassengerGeneration is null ? "aggregate" : "flight_profiles")) return false;
        var checkpoints = request.Checkpoints.ToDictionary(cp => cp.CheckpointId);
        if (!checkpoints.Keys.ToHashSet().SetEquals(report.Checkpoints.Select(cp => cp.CheckpointId))) return false;
        var groups = report.Passengers.ToLookup(person => person.CheckpointId);
        foreach (var cp in report.Checkpoints)
        {
            if (cp.Capacity != checkpoints[cp.CheckpointId].StaffCount) return false;
            var metrics = response.Metrics.Where(metric => metric.ZoneId == cp.CheckpointId).ToArray();
            var snapshots = response.Snapshots.Where(snapshot => snapshot.ZoneId == cp.CheckpointId).ToArray();
            if (metrics.Length != 1 || snapshots.Length != 1) return false;
            var metric = metrics[0]; var snapshot = snapshots[0];
            if (metric.ProcessedPassengers != cp.ProcessedCount
                || Math.Abs(metric.MeanWaitMinutes - cp.MeanWaitMinutes) > 1e-6
                || snapshot.QueuedPassengers != cp.QueuedCount || snapshot.ProcessedPassengers != cp.ProcessedCount) return false;
            var members = groups[cp.CheckpointId].ToArray();
            var finished = members.Where(person => person.SecurityClearedMinute is not null).ToArray();
            if (Math.Abs((finished.Length == 0 ? 0 : finished.Average(person => person.QueueWaitMinutes!.Value)) - cp.MeanWaitMinutes) > 1e-6) return false;
            // Service intervals are half-open: release before grant at the same virtual minute.
            var services = members.Where(person => person.ProcessingStartedMinute is not null
                && (person.SecurityClearedMinute is null || person.SecurityClearedMinute > person.ProcessingStartedMinute)).ToArray();
            var events = services
                .SelectMany(person => person.SecurityClearedMinute is { } end
                    ? new[] { (Time: person.ProcessingStartedMinute!.Value, Change: 1), (Time: end, Change: -1) }
                    : [(Time: person.ProcessingStartedMinute!.Value, Change: 1)])
                .OrderBy(item => item.Time).ThenBy(item => item.Change);
            var busy = 0;
            foreach (var item in events)
            {
                busy += item.Change;
                if (busy < 0 || busy > cp.Capacity || busy > cp.PeakBusy) return false;
                // Only new grants need free capacity; reductions never preempt running service.
                if (item.Change > 0 && busy > timeline.Capacity(cp.CheckpointId, item.Time)) return false;
            }
            if (cp.Capacity == 0 && members.Any(person => person.ProcessingStartedMinute is not null)) return false;
        }
        var flights = request.PassengerGeneration?.Flights.ToDictionary(flight => flight.FlightId);
        var gates = request.Navigation?.FlightGates.ToDictionary(flight => flight.FlightId, flight => flight.GateId);
        var batches = request.PassengerGeneration?.Batches.ToDictionary(batch => batch.FlightId);
        var generated = report.Passengers.Where(person => timeline.Surge(person.Id) is null).ToArray();
        // Passengers still at check-in at the horizon never arrive at a control; CheckInCorrelation accounts for them.
        var absent = CheckInCorrelation.Absent(response);
        if (batches is not null && (report.ArrivedCount != report.PopulationCount - absent.Length
            || batches.Any(batch => generated.Count(person => person.FlightId == batch.Key)
                + absent.Count(person => person.Id.StartsWith($"demo-pax-{batch.Key:N}-", StringComparison.Ordinal)) != batch.Value.ModeledCount))) return false;
        foreach (var surge in timeline.Events.Where(item => item.Type == "passenger_surge"))
            if (report.Passengers.Select(person => person.Id).Concat(absent.Select(person => person.Id))
                .Count(id => id.StartsWith($"demo-surge-{surge.EventId:N}-", StringComparison.Ordinal)) != surge.PassengerCount) return false;
        // A checked-in surge passenger entered the terminal during the window; its control arrival follows check-in.
        var terminal = response.CheckInResult?.Passengers.ToDictionary(person => person.Id, person => person.TerminalArrivalMinute, StringComparer.Ordinal) ?? [];
        foreach (var person in report.Passengers)
        {
            if (!checkpoints.ContainsKey(person.CheckpointId)) return false;
            var assigned = person.AssignedCheckpointId ?? person.CheckpointId;
            if (person.AssignedCheckpointId is { } original)
            {
                // Only arrivals meeting an unavailable control are redirected, to a control that can serve.
                if (!checkpoints.ContainsKey(original) || timeline.Available(original, person.ArrivalMinute)
                    || timeline.Capacity(person.CheckpointId, person.ArrivalMinute) == 0) return false;
            }
            if (timeline.Surge(person.Id) is { } surge)
            {
                var number = int.TryParse(person.Id.AsSpan(person.Id.Length - 6), out var parsed) ? parsed : 0;
                var flightId = surge.Event.FlightId;
                var entered = terminal.TryGetValue(person.Id, out var checkedIn) ? checkedIn : person.ArrivalMinute;
                if (number < 1 || number > surge.Count || assigned != surge.Event.CheckpointId || person.FlightId != flightId
                    || person.GateId != (flightId is { } gateFlight ? gates?.GetValueOrDefault(gateFlight) : null)
                    || person.BoardingCloseMinute != (flightId is { } closeFlight ? timeline.Close[closeFlight] : null)
                    || entered < surge.Event.Minute || entered >= surge.Event.Minute + surge.Event.WindowMinutes) return false;
                continue;
            }
            if (flights is null) { if (person.FlightId is not null || person.GateId is not null || person.BoardingCloseMinute is not null) return false; }
            else if (person.FlightId is not { } id || !flights.TryGetValue(id, out var flight)
                || !flight.Allows(assigned) || person.BoardingCloseMinute != timeline.Close[id]
                || person.GateId != gates?.GetValueOrDefault(id)) return false;
            else if (!person.Id.StartsWith($"demo-pax-{id:N}-", StringComparison.Ordinal)
                || !int.TryParse(person.Id.AsSpan(person.Id.Length - 6), out var number) || number < 1 || number > batches![id].ModeledCount) return false;
        }
        return true;
    }
}

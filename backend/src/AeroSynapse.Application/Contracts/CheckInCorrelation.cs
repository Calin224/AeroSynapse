namespace AeroSynapse.Application.Contracts;

/// <summary>Re-derives the check-in stage from its passenger records and ties it to the control arrivals.</summary>
public static class CheckInCorrelation
{
    /// <summary>Passengers still at check-in or walking at the horizon: demand their control never sees arrive.</summary>
    public static CheckInPassenger[] Absent(SimulationResponse response) =>
        response.CheckInResult?.Passengers.Where(person => person.SecurityArrivalMinute is null).ToArray() ?? [];

    /// <summary>Final boarding close of a check-in passenger's flight; null for a surge without a flight.</summary>
    public static double? BoardingClose(EventTimeline timeline, CheckInPassenger person) =>
        timeline.Surge(person.Id) is { } surge ? surge.Event.FlightId is { } flight ? timeline.Close[flight] : null
        : Guid.TryParseExact(person.Id.AsSpan(9, 32), "N", out var generated) && timeline.Close.TryGetValue(generated, out var close) ? close : null;

    public static bool Valid(SimulationRequest request, SimulationResponse response)
    {
        if (request.CheckIn is not { } input || response.Status != "completed") return response.CheckInResult is null;
        if (response.CheckInResult is not { } result || response.ProcessResult is not { } processes || request.PassengerGeneration is null) return false;
        if (result.ZoneId != input.ZoneId || result.Capacity != input.StaffCount) return false;
        double horizon = request.DurationMinutes;
        var checkpoints = request.Checkpoints.Select(cp => cp.CheckpointId).ToHashSet();
        var flights = request.PassengerGeneration.Flights.Select(flight => flight.FlightId).ToHashSet();
        var arrived = processes.Passengers.ToDictionary(person => person.Id, StringComparer.Ordinal);
        var timeline = new EventTimeline(request);
        foreach (var person in result.Passengers)
        {
            if (!checkpoints.Contains(person.CheckpointId) || person.TerminalArrivalMinute > horizon) return false;
            // Surge passengers check in only for a named flight, at their event's control, entering during its window.
            if (timeline.Surge(person.Id) is { } surge)
            {
                if (surge.Event.FlightId is not { } surgeFlight || !flights.Contains(surgeFlight) || person.CheckpointId != surge.Event.CheckpointId
                    || person.TerminalArrivalMinute < surge.Event.Minute || person.TerminalArrivalMinute >= surge.Event.Minute + surge.Event.WindowMinutes) return false;
            }
            else if (!person.Id.StartsWith("demo-pax-", StringComparison.Ordinal)
                || !Guid.TryParseExact(person.Id.AsSpan(9, 32), "N", out var flight) || !flights.Contains(flight)) return false;
            var finish = person.StartedMinute + person.ServiceMinutes;
            // A time is reported exactly when it falls within the horizon.
            if (person.StartedMinute is null ? person.FinishedMinute is not null
                : person.FinishedMinute is null ? finish <= horizon : !KpiContractValidation.Near(person.FinishedMinute, finish)) return false;
            if (person.FinishedMinute is { } finished)
            {
                var joins = finished + input.WalkingMinutes;
                if (person.SecurityArrivalMinute is { } reported ? !KpiContractValidation.Near(reported, joins)
                    : joins < horizon && !KpiContractValidation.Near(joins, horizon)) return false;
            }
            // The control sees the passenger at exactly the check-in join time, at the control the flight assigned.
            if (person.SecurityArrivalMinute is { } security)
            {
                if (!arrived.TryGetValue(person.Id, out var control) || control.ArrivalMinute != security
                    || (control.AssignedCheckpointId ?? control.CheckpointId) != person.CheckpointId) return false;
            }
            else if (arrived.ContainsKey(person.Id)) return false;
        }
        return Fifo(result.Passengers, input, horizon) && Snapshots(request, result);
    }

    // First come, first served on the first free desk, never more desks than open, and no open desk idle while someone waits.
    private static bool Fifo(IReadOnlyList<CheckInPassenger> people, CheckInInput input, double horizon)
    {
        var openings = input.Openings();
        var ordered = people.OrderBy(person => person.TerminalArrivalMinute).ThenBy(person => person.Id, StringComparer.Ordinal).ToArray();
        var last = double.NegativeInfinity; var waiting = false;
        foreach (var person in ordered)
        {
            if (person.StartedMinute is not { } start) { waiting = true; continue; }
            if (waiting || start < last) return false;
            last = start;
        }
        if (openings.Length == 0) return ordered.All(person => person.StartedMinute is null);
        var busy = 0;
        foreach (var (time, change) in people.Where(person => person.StartedMinute is not null)
            .SelectMany(person => person.FinishedMinute is { } end ? new[] { (person.StartedMinute!.Value, 1), (end, -1) } : [(person.StartedMinute!.Value, 1)])
            .OrderBy(item => item.Item1).ThenBy(item => item.Item2))
            if ((busy += change) > openings.Count(opened => opened <= time)) return false;
        int Active(double time, bool includeStart) => people.Count(other => other.StartedMinute is { } start
            && (includeStart ? start <= time : start < time) && (other.FinishedMinute ?? double.PositiveInfinity) >= time && (!includeStart || other.FinishedMinute is not { } end || end > time));
        foreach (var person in ordered)
        {
            // A later start means every desk open before it was busy until then; a desk opening at that minute frees one.
            if (person.StartedMinute is { } start ? start > person.TerminalArrivalMinute && Active(start, false) != openings.Count(opened => opened < start)
                : Active(horizon, true) != openings.Count(opened => opened <= horizon)) return false;
        }
        return true;
    }

    private static bool Snapshots(SimulationRequest request, CheckInResult result)
    {
        var grid = KpiConventions.Minutes(request.DurationMinutes, request.SnapshotIntervalMinutes ?? 1);
        if (!result.Snapshots.Select(row => row.Minute).SequenceEqual(grid)) return false;
        var people = result.Passengers;
        var input = request.CheckIn!; var openings = input.Openings();
        foreach (var row in result.Snapshots)
        {
            double time = row.Minute;
            // Desk-minutes open so far: staffCount × minute when every desk opens at 0.
            var offered = openings.Sum(opened => Math.Max(0, time - opened));
            var started = people.Where(person => person.StartedMinute <= time).ToArray();
            var finished = people.Where(person => person.FinishedMinute <= time).ToArray();
            var waits = finished.Select(person => person.StartedMinute!.Value - person.TerminalArrivalMinute).Order().ToArray();
            var busy = started.Sum(person => Math.Min(time, person.FinishedMinute ?? time) - person.StartedMinute!.Value);
            if (row.ZoneType != CheckInContractValidation.ZoneType || row.ZoneId != result.ZoneId || row.Capacity != input.OpenAt(time)
                || row.ArrivedPassengers != people.Count(person => person.TerminalArrivalMinute <= time)
                || row.QueuedPassengers != row.ArrivedPassengers - started.Length
                || row.ProcessingPassengers != started.Length - finished.Length || row.ProcessedPassengers != finished.Length
                || row.WalkingPassengers != finished.Count(person => !(person.SecurityArrivalMinute <= time))
                || row.MaximumQueue != MaximumQueue(people, time)
                || !KpiContractValidation.Near(row.MeanWaitMinutes, waits.Length == 0 ? null : waits.Average())
                || !KpiContractValidation.Near(row.MedianWaitMinutes, waits.Length == 0 ? null : (waits[waits.Length / 2] + waits[(waits.Length - 1) / 2]) / 2)
                || !KpiContractValidation.Near(row.P95WaitMinutes, waits.Length == 0 ? null : waits[(int)Math.Ceiling(.95 * waits.Length) - 1])
                || !KpiContractValidation.Near(row.BusyMinutes, busy)
                || !KpiContractValidation.Near(row.Utilization, row.Capacity > 0 && time > 0 ? offered > 0 ? Math.Min(1, busy / offered) : 0 : null)) return false;
        }
        return true;
    }

    // Longest queue after all arrivals and service starts of the same instant, up to the minute.
    private static int MaximumQueue(IReadOnlyList<CheckInPassenger> people, double time)
    {
        var changes = people.Where(person => person.TerminalArrivalMinute <= time).Select(person => (Time: person.TerminalArrivalMinute, Change: 1))
            .Concat(people.Where(person => person.StartedMinute <= time).Select(person => (Time: person.StartedMinute!.Value, Change: -1)))
            .GroupBy(item => item.Time).OrderBy(group => group.Key);
        int length = 0, peak = 0;
        foreach (var group in changes) peak = Math.Max(peak, length += group.Sum(item => item.Change));
        return peak;
    }
}

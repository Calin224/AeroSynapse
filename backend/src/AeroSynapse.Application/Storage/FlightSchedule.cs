using AeroSynapse.Domain.DigitalTwin;

namespace AeroSynapse.Application.Storage;

public sealed record ScheduleGate(Guid Id, string Code, string Name, int Capacity, bool IsSynthetic = true);
public sealed record FlightSchedule(Guid ScenarioId, int Revision, DateTime StartsAtUtc, int DurationMinutes,
    IReadOnlyList<ScenarioFlight> Flights, IReadOnlyList<ScheduleGate> Gates, bool IsSynthetic = true);
public sealed record GateConflict(Guid GateId, Guid FirstFlightId, Guid SecondFlightId,
    DateTime StartsAtUtc, DateTime EndsAtUtc);

public static class FlightScheduling
{
    public static readonly IReadOnlySet<string> Statuses = new HashSet<string>(StringComparer.Ordinal)
        { "scheduled", "delayed", "boarding", "closed", "departed", "cancelled" };

    public static ScenarioFlight Resolve(ScenarioFlight flight, DateTime startsAtUtc)
    {
        if (flight.BoardingOpensAtUtc is not null || flight.BoardingClosesAtUtc is not null) return flight;
        var available = Math.Max(0, flight.DepartureAtUtc.Ticks - startsAtUtc.Ticks);
        var open = flight.DepartureAtUtc.AddTicks(-Math.Min(TimeSpan.FromMinutes(30).Ticks, available));
        var close = flight.DepartureAtUtc.AddTicks(-Math.Min(TimeSpan.FromMinutes(5).Ticks,
            (flight.DepartureAtUtc.Ticks - open.Ticks) / 2));
        return flight with { BoardingOpensAtUtc = open, BoardingClosesAtUtc = close };
    }

    public static ScenarioInputs Resolve(ScenarioInputs inputs) => inputs with
        { Flights = inputs.Flights.Select(flight => Resolve(flight, inputs.StartsAtUtc)).ToArray() };

    public static bool TryDelayed(DateTime time, int minutes, out DateTime delayed)
    {
        delayed = time;
        if (minutes is < 0 or > 10080) return false;
        var delay = TimeSpan.FromMinutes(minutes);
        if (DateTime.MaxValue - time < delay) return false;
        delayed = time + delay;
        return true;
    }

    // A gate is reserved from effective boarding opening until effective departure, with an exclusive end.
    public static IReadOnlyList<GateConflict> Conflicts(ScenarioInputs inputs)
    {
        var conflicts = new List<GateConflict>();
        var windows = inputs.Flights.Where(flight => flight is not null && flight.Status != "cancelled")
            .Select(flight => Resolve(flight, inputs.StartsAtUtc))
            .Where(flight => flight.BoardingOpensAtUtc is { } open && flight.BoardingClosesAtUtc is { } close
                && open < close && close < flight.DepartureAtUtc
                && TryDelayed(open, flight.DelayMinutes, out _) && TryDelayed(flight.DepartureAtUtc, flight.DelayMinutes, out _))
            .Select(flight => (Flight: flight, Open: flight.BoardingOpensAtUtc!.Value.AddMinutes(flight.DelayMinutes),
                End: flight.DepartureAtUtc.AddMinutes(flight.DelayMinutes)));
        foreach (var gate in windows.GroupBy(window => window.Flight.GateId))
        {
            var ordered = gate.OrderBy(window => window.Open).ToArray();
            for (var first = 0; first < ordered.Length; first++)
                for (var second = first + 1; second < ordered.Length && ordered[second].Open < ordered[first].End; second++)
                    conflicts.Add(new(gate.Key, ordered[first].Flight.Id, ordered[second].Flight.Id,
                        ordered[second].Open, ordered[first].End < ordered[second].End ? ordered[first].End : ordered[second].End));
        }
        return conflicts;
    }
}

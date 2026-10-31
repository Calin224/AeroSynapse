using AeroSynapse.Application.Contracts;

namespace AeroSynapse.Management.Tests;

// Lifecycle tests use an engine double with no observed arrivals; numerical tests use the real Python engine.
internal static class KpiFixture
{
    public static KpiResult EmptyObserved(SimulationRequest request)
    {
        var rows = new List<KpiSnapshot>(); var interval = request.SnapshotIntervalMinutes!.Value;
        var grid = KpiConventions.Minutes(request.DurationMinutes, interval);
        var zones = request.Checkpoints.OrderBy(cp => cp.CheckpointId.ToString(), StringComparer.Ordinal).ToArray();
        foreach (var (minute, timeIndex) in grid.Select((minute, index) => (minute, index)))
            foreach (var (cp, index) in zones.Select((cp, index) => (cp, index)))
            {
                var start = timeIndex == 0 ? 0 : grid[timeIndex - 1];
                var population = request.PassengerCount / zones.Length + (index < request.PassengerCount % zones.Length ? 1 : 0);
                rows.Add(new("1.0", request.RunId, cp.CheckpointId, "checkpoint", minute, minute, start,
                    request.StartsAtUtc.AddMinutes(minute).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), population,
                    0, population, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                    null, null, null, minute > start ? 0 : null, minute > 0 ? 0 : null,
                    cp.StaffCount > 0 && minute > start ? 0 : null, cp.StaffCount > 0 && minute > 0 ? 0 : null,
                    null, cp.StaffCount, 0, 0, 0, null));
            }
        return new("1.0", KpiConventions.Engine, request.RunId, interval, request.DurationMinutes,
            request.PassengerCount, 0, request.PassengerCount, 0, request.PassengerCount, 0,
            rows.Count, rows.Count, 0, rows.Count, false, KpiConventions.Definitions, rows);
    }
}

using System.Text.Json.Serialization;
namespace AeroSynapse.Application.Contracts;

public sealed record KpiDefinitions(string WaitPopulation, string PercentileMethod, string ThroughputPopulation,
    string QueuePopulation, string UtilizationPopulation, string BoardingPopulation, string ReconciliationPopulation);
public sealed record KpiSnapshot(string KpiVersion, Guid RunId, Guid ZoneId, string ZoneType, int Minute,
    double ElapsedMinutes, int WindowStartMinute, string RecordedAtUtc, int PopulationCount,
    int ArrivedPassengers, int NotArrivedPassengers, int QueuedPassengers, int ProcessingPassengers,
    int ProcessedPassengers, int WalkingPassengers, int AtGatePassengers, int UnreachablePassengers,
    int WaitSampleCount, int CompletedInWindow, int MaximumQueue, int BoardingRiskPopulationCount,
    int BoardingAtRiskCount, int LateAtGateCount, int MissedDeadlineUnfinishedCount,
    double? MeanWaitMinutes, double? MedianWaitMinutes, double? P95WaitMinutes, double? ThroughputPerMinute,
    double? CumulativeThroughputPerMinute, double? Utilization, double? CumulativeUtilization,
    double? BoardingLateRisk, int Capacity, int PeakBusy, double BusyMinutes, double BusyMinutesInWindow, double? FirstQueueMinute,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? EffectiveCapacity = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? TransferredOutPassengers = null);
public sealed record KpiResult(string KpiVersion, string Engine, Guid RunId, int IntervalMinutes, int HorizonMinutes,
    int PopulationCount, int ProcessedCount, int UnprocessedCount, int ArrivedCount, int NotArrivedCount,
    int UnreachableCount, int SnapshotCount, int MatchedSnapshotCount, int Offset, int ReturnedSnapshotCount,
    bool HasMore, KpiDefinitions Definitions, IReadOnlyList<KpiSnapshot> Snapshots,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? EventCount = null);
public static class KpiConventions
{
    public const string Engine = "simpy-kpi-1.0";
    public const int MaxSnapshots = 50000;
    public static KpiDefinitions Definitions { get; } = new("service_completed_by_minute", "nearest_rank", "service_completed_in_window",
        "waiting_for_resource", "observed_busy_slots_including_unfinished_service", "modeled_passengers_with_boarding_deadline", "configured_modeled_demand");
    public static int[] Minutes(int horizon, int interval) => Enumerable.Range(0, (horizon + interval - 1) / interval)
        .Select(index => index * interval).Append(horizon).ToArray();
}

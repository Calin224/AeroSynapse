using System.Text.Json.Serialization;
using AeroSynapse.Domain.DigitalTwin;

namespace AeroSynapse.Application.Contracts;

public sealed record PassengerCheckpointRoute(Guid CheckpointId, Guid RouteId, double RouteTravelMinutes);
// CheckpointRoutes is present only for spread batches: every checkpoint routed to the gate, including the flight's own.
public sealed record PassengerFlightInput(Guid FlightId, int PassengerCapacity, double BoardingCloseMinute,
    bool IsCancelled, Guid CheckpointId, Guid RouteId, double RouteTravelMinutes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<PassengerCheckpointRoute>? CheckpointRoutes = null)
{
    // Whether a passenger of this flight may use the checkpoint (and route): its own, or any spread alternative.
    public bool Allows(Guid checkpointId, Guid? routeId = null) => CheckpointRoutes is { } lanes
        ? lanes.Any(lane => lane.CheckpointId == checkpointId && (routeId is null || lane.RouteId == routeId))
        : checkpointId == CheckpointId && (routeId is null || routeId == RouteId);
}
public sealed record PassengerGenerationRequest(string GeneratorVersion, int Seed, int DurationMinutes,
    IReadOnlyList<CheckpointInput> Checkpoints, IReadOnlyList<PassengerFlightInput> Flights,
    IReadOnlyList<PassengerBatchConfig> Batches, bool IsSynthetic = true,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PassengerFlowConfig? Flow = null);
public sealed record PassengerStageVisit(string Stage, double ServiceMinutes);
// Segment, transfer and stages are present only when a flow is configured.
public sealed record VirtualPassenger(string Id, Guid FlightId, Guid CheckpointId, Guid RouteId, string GroupId,
    string GroupKind, double ArrivalMinute, double ServiceMinutes, double TravelMinutes, bool InSurge, bool IsSynthetic,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Segment = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? TransferMinutes = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<PassengerStageVisit>? Stages = null);
public sealed record PassengerBatchSummary(Guid FlightId, int ConfiguredCount, int ModeledCount, int UnmodeledCount,
    string CountMode, int GroupCount, int SurgePassengerCount, bool IsSynthetic);
// ArrivalsPerQuarterHour regroups the same arrivals by 15 minutes, for staffing that follows a long day's waves.
public sealed record CheckpointDemandSummary(Guid CheckpointId, int ModeledCount, int PeakArrivalsPerMinute,
    double MeanServiceMinutes, int StaffCount, double EstimatedCapacityPerMinute, bool Overloaded,
    IReadOnlyList<int>? ArrivalsPerQuarterHour = null);
public sealed record PassengerGenerationResult(string GeneratorVersion, int Seed, bool IsSynthetic, string Rng,
    string NumpyVersion, string Fingerprint, int ConfiguredCount, int ModeledCount, bool HasSubset,
    int Offset, int ReturnedCount, bool HasMore, IReadOnlyList<PassengerBatchSummary> Batches,
    IReadOnlyList<CheckpointDemandSummary> CheckpointDemand, IReadOnlyList<VirtualPassenger> Passengers);
// Flow report: aggregates per stage and passenger type within the horizon; no passenger or group identifiers.
public sealed record PassengerStageStatistics(string Stage, int ArrivedPassengers, int ProcessedPassengers, int QueuedPassengers,
    double MeanWaitMinutes, double MaxWaitMinutes, double BlockedMinutes);
public sealed record PassengerSegmentStatistics(string Segment, int Passengers, int ReachedGatePassengers, int MissedBoardingPassengers,
    double MeanJourneyMinutes, double MaxJourneyMinutes, double MeanQueueMinutes, double MeanClosureDelayMinutes);
public sealed record PassengerFlowReport(string GeneratorVersion, int Seed, bool IsSynthetic, string Fingerprint, int DurationMinutes,
    int ModeledCount, IReadOnlyList<PassengerStageStatistics> Stages, IReadOnlyList<PassengerSegmentStatistics> Segments);
public interface IPassengerGenerator
{
    Task<PassengerGenerationResult> GenerateAsync(PassengerGenerationRequest request, int offset, int limit, CancellationToken token);
    Task<PassengerFlowReport> AnalyzeFlowsAsync(PassengerGenerationRequest request, CancellationToken token);
}
public sealed class PassengerGeneratorUnavailableException(string message) : Exception(message);

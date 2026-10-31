using AeroSynapse.Domain.DigitalTwin;

namespace AeroSynapse.Application.Contracts;

public sealed record NavigationPreviewRequest(PassengerGenerationRequest PassengerGeneration, NavigationInput Navigation);
public sealed record NavigationWaypoint(Guid ZoneId, double X, double Y, double Z);
public sealed record NavigationRoute(string RouteKey, Guid FlightId, Guid GateId, Guid CheckpointId, string GroupKind,
    IReadOnlyList<Guid> ZoneIds, IReadOnlyList<Guid> EdgeIds, double DistanceMeters, double BaseWalkingMinutes,
    IReadOnlyList<NavigationWaypoint> Waypoints, int PassengerCount);
public sealed record NavigationError(string Code, string Message);
public sealed record NavigationJourney(string Id, Guid FlightId, Guid GateId, Guid CheckpointId, string? RouteKey,
    NavigationError? Error, double? WalkingMinutes, double? QueueWaitMinutes, double ServiceMinutes, double ArrivalMinute,
    double? GateArrivalMinute, double BoardingCloseMinute, string BoardingRisk);
public sealed record NavigationFlightSummary(Guid FlightId, Guid GateId, int ModeledCount, int RoutedCount, int ErrorCount,
    int SafeCount, int LateCount, int UnservedCount, int PendingCount, double? MeanWalkingMinutes, double? MeanQueueWaitMinutes);
public sealed record NavigationResult(string NavigationVersion, string Algorithm, string GraphFingerprint, string PopulationFingerprint,
    bool IsSynthetic, int ModeledCount, int RoutedCount, int ErrorCount, bool Assessed,
    IReadOnlyList<NavigationRoute> Routes, IReadOnlyList<NavigationFlightSummary> Flights, IReadOnlyList<NavigationJourney> Journeys,
    int Offset, int ReturnedCount, bool HasMore);
public interface INavigationPlanner
{
    Task<NavigationResult> PreviewAsync(NavigationPreviewRequest request, int offset, int limit, CancellationToken token);
}
public sealed class NavigationPlannerUnavailableException() : Exception("The navigation service is unavailable. Check Intelligence and retry.");

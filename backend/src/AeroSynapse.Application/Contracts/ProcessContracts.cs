using System.Text.Json.Serialization;
namespace AeroSynapse.Application.Contracts;

public sealed record ProcessPassenger(string Id, Guid CheckpointId, Guid? FlightId, Guid? GateId, string Stage,
    double ArrivalMinute, double ServiceMinutes, double WalkingMinutes, double? BoardingCloseMinute,
    double? QueueEnteredMinute, double? ProcessingStartedMinute, double? SecurityClearedMinute,
    double? GateArrivalMinute, double? QueueWaitMinutes, string BoardingRisk, bool DeadlineMissed, string? ErrorCode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? AssignedCheckpointId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? RouteHoldMinutes = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? AwaitingRoute = null);
public sealed record ProcessCheckpoint(Guid CheckpointId, int Capacity, int PeakBusy, int PeakQueue,
    int ArrivedCount, int QueuedCount, int ProcessingCount, int ProcessedCount, double MeanWaitMinutes);
public sealed record ProcessResult(string ProcessVersion, string Engine, string Mode, double HorizonMinutes,
    int PopulationCount, int ArrivedCount, int NotArrivedCount, int QueuedCount, int ProcessingCount,
    int WalkingCount, int AtGateCount, int UnreachableCount, int DeadlineMissedCount,
    IReadOnlyList<ProcessCheckpoint> Checkpoints, IReadOnlyList<ProcessPassenger> Passengers,
    int Offset, int ReturnedCount, bool HasMore,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<AppliedEvent>? Events = null);

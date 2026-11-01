using System.Text.Json.Serialization;
namespace AeroSynapse.Application.Contracts;

public sealed record ProcessPassenger(string Id, Guid CheckpointId, Guid? FlightId, Guid? GateId, string Stage,
    double ArrivalMinute, double ServiceMinutes, double WalkingMinutes, double? BoardingCloseMinute,
    double? QueueEnteredMinute, double? ProcessingStartedMinute, double? SecurityClearedMinute,
    double? GateArrivalMinute, double? QueueWaitMinutes, string BoardingRisk, bool DeadlineMissed, string? ErrorCode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? AssignedCheckpointId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? RouteHoldMinutes = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? AwaitingRoute = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? RedistributedAtMinute = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? TransferMinutes = null,
    // Every queue move in order; RedistributedAtMinute and TransferMinutes repeat the last one.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ProcessTransfer>? Transfers = null)
{
    // The control a passenger belongs to at a time: the assigned one until arrival, then the one queued at
    // (a redirect on arrival when it differs), then each move's target from its minute.
    public Guid CheckpointAt(double time)
    {
        if (time < ArrivalMinute) return AssignedCheckpointId ?? CheckpointId;
        var at = Transfers is [var first, ..] ? first.FromCheckpointId : CheckpointId;
        foreach (var hop in Transfers ?? []) if (hop.Minute <= time) at = hop.ToCheckpointId;
        return at;
    }
}
public sealed record ProcessTransfer(Guid FromCheckpointId, Guid ToCheckpointId, int Minute, int WalkingMinutes);
public sealed record ProcessCheckpoint(Guid CheckpointId, int Capacity, int PeakBusy, int PeakQueue,
    int ArrivedCount, int QueuedCount, int ProcessingCount, int ProcessedCount, double MeanWaitMinutes);
public sealed record ProcessResult(string ProcessVersion, string Engine, string Mode, double HorizonMinutes,
    int PopulationCount, int ArrivedCount, int NotArrivedCount, int QueuedCount, int ProcessingCount,
    int WalkingCount, int AtGateCount, int UnreachableCount, int DeadlineMissedCount,
    IReadOnlyList<ProcessCheckpoint> Checkpoints, IReadOnlyList<ProcessPassenger> Passengers,
    int Offset, int ReturnedCount, bool HasMore,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<AppliedEvent>? Events = null);

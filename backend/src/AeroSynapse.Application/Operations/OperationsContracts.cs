using System.Text.Json.Serialization;

namespace AeroSynapse.Application.Operations;

// Explicit aggregate-only projections; names, staff, inputs and arbitrary JSON never enter the stream.
public sealed record ProgressZone(Guid ZoneId, int? QueuedPassengers, int? ProcessedPassengers,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? NotArrivedPassengers = null);
public sealed record EngineProgress(double SimulationMinute, IReadOnlyList<ProgressZone> Zones);
public sealed record ProgressSnapshot(IReadOnlyList<ProgressZone> Zones, int TotalZones);
public sealed record OptimizationSummary(Guid OptimizationId, Guid? BaselineRunId, Guid? RecommendedRunId, int ComparedRunCount);
public sealed record OperationsMessage(Guid ScenarioId, long Sequence, string Type, Guid? RunId = null,
    double? SimulationMinute = null, string? Status = null, bool? ResultsValid = null,
    ProgressSnapshot? Snapshot = null, OptimizationSummary? Optimization = null);
public sealed record OperationsRun(Guid RunId, string Status, int Revision, bool ResultsValid,
    double? SimulationMinute, ProgressSnapshot? Snapshot);
public sealed record ScenarioOperationsState(Guid ScenarioId, long Sequence, bool ResetRequired,
    IReadOnlyList<OperationsRun> Runs, OperationsRun? Run, OptimizationSummary? Optimization);

public interface IOperationsReader
{
    Task<long> CursorAsync(Guid scenarioId, CancellationToken token);
    Task<ScenarioOperationsState> StateAsync(Guid scenarioId, Guid? runId, long? afterSequence, CancellationToken token);
    Task<IReadOnlyList<OperationsMessage>> EventsAsync(Guid scenarioId, long afterSequence, int limit, CancellationToken token);
}

public interface IRunProgressRecorder
{
    Task RecordAsync(Guid runId, EngineProgress progress, CancellationToken token);
}

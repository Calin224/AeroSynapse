using AeroSynapse.Application.Contracts;
using AeroSynapse.Domain.DigitalTwin;
namespace AeroSynapse.Application.Management;

public sealed record RevisionRequest(int Revision);
public sealed record RunDetails(Guid Id, Guid SceneId, Guid? ScenarioId, int? ScenarioRevision,
    string Status, int Revision, int Seed, DateTime StartedAtUtc, DateTime? LaunchedAtUtc,
    DateTime? FinishedAtUtc, bool ResultsValid, string? FailureReason, string? InputSnapshotJson,
    string ParametersJson, bool IsSynthetic, bool ExecutionRequested, Guid? RetryOfRunId,
    IReadOnlyList<RunMetric> Metrics, IReadOnlyList<RunSnapshot> Snapshots, RunFailure? Error, RunProvenance? Provenance = null);

public sealed record RunProvenance(string HashVersion, string? InputHash, string? ResultHash, string? EngineVersion,
    string? EngineBuild, double? EngineMilliseconds, double? ExecutionMilliseconds, string CorrelationId);
public sealed record ComparedRun(Guid Id, string Status, bool ResultsValid, Guid? ScenarioId, int? ScenarioRevision, RunProvenance Provenance, string? FailureReason);
public sealed record MetricDifference(Guid ZoneId, string Name, string Unit, decimal? Baseline, decimal? Candidate, decimal? Delta,
    decimal? PercentChange = null, string? UnavailableReason = null, string ZoneType = "checkpoint",
    DateTime? BaselineRecordedAtUtc = null, DateTime? CandidateRecordedAtUtc = null);
public sealed record ComparisonIssue(string Code, string Message, string Severity = "blocking");
public sealed record RunComparability(bool Comparable, bool? PopulationMatches, string? BaselinePopulationHash,
    string? CandidatePopulationHash, IReadOnlyList<ComparisonIssue> Issues);
public sealed record RunComparison(ComparedRun Baseline, ComparedRun Candidate, bool? InputsMatch, bool? VersionsMatch,
    bool? ResultsMatch, IReadOnlyList<MetricDifference> Metrics, bool MetricsTruncated, RunComparability? Comparability = null);
public interface IRunComparisonService
{
    Task<RunComparison> CompareAsync(Guid baselineId, Guid candidateId, CancellationToken token);
}

public interface IRunExecutionService
{
    Task<SimulationRun> StartAsync(Guid scenarioId, CancellationToken token);
    Task<SimulationRun> RetryAsync(Guid runId, int revision, CancellationToken token);
    Task<RunDetails> GetAsync(Guid runId, CancellationToken token);
    Task<bool> DispatchNextAsync(CancellationToken token);
    Task ExecuteBatchRunAsync(Guid runId, AeroSynapse.Application.Simulation.ExecutionControl control, CancellationToken token);
}
public interface ISimulationEngine
{
    AeroSynapse.Application.Simulation.EngineDiagnostics? Diagnostics => null;
    Task<SimulationResponse> ExecuteAsync(SimulationRequest request, CancellationToken token);
    Task<SimulationResponse> ExecuteControlledAsync(SimulationRequest request, AeroSynapse.Application.Simulation.ExecutionControl control, CancellationToken token) => ExecuteAsync(request, token);
}
public sealed class SimulationEngineException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

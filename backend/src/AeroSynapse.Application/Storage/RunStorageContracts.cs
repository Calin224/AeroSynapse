using AeroSynapse.Domain.DigitalTwin;

namespace AeroSynapse.Application.Storage;

public sealed record RunTransitionRequest(string Status, int Revision, string? FailureReason = null);
public sealed record RunMetricRequest(DateTime RecordedAtUtc, string ZoneType, Guid ZoneId,
    string Name, decimal Value, string Unit);
public sealed record RunSnapshotRequest(DateTime RecordedAtUtc, string ZoneType, Guid ZoneId, string PayloadJson);
public sealed record RunResultQuery(DateTime? FromUtc = null, DateTime? ToUtc = null,
    string? ZoneType = null, Guid? ZoneId = null, int Limit = 50, bool IncludeInvalid = false);
public sealed record RunResults<T>(Guid RunId, string Status, bool IsValid, string? FailureReason, IReadOnlyList<T> Items);
public sealed record CandidateComparison(Guid RunId, int Rank, decimal Score, string AssessmentJson = "{}");
public sealed record RecommendationRequest(Guid BaselineRunId, string Objective, decimal BaselineScore,
    string Rationale, IReadOnlyList<CandidateComparison> Candidates);
public sealed record RecommendationDetails(RunRecommendation Recommendation, IReadOnlyList<RecommendationCandidate> Candidates)
{
    public Guid RecommendedRunId => Candidates.OrderBy(x => x.Rank).First().RunId;
}
public sealed record ScenarioDeletionResult(Guid ScenarioId, string Retention, bool Archived, int? Revision, int DeletedRuns);

public interface IRunStorageService
{
    Task<SimulationRun?> GetAsync(Guid runId, CancellationToken cancellationToken = default);
    Task<SimulationRun> TransitionAsync(Guid runId, RunTransitionRequest request, CancellationToken cancellationToken = default);
    Task<RunMetric> AddMetricAsync(Guid runId, RunMetricRequest request, CancellationToken cancellationToken = default);
    Task<RunSnapshot> AddSnapshotAsync(Guid runId, RunSnapshotRequest request, CancellationToken cancellationToken = default);
    Task<RunResults<RunMetric>> MetricsAsync(Guid runId, RunResultQuery query, CancellationToken cancellationToken = default);
    Task<RunResults<RunSnapshot>> SnapshotsAsync(Guid runId, RunResultQuery query, CancellationToken cancellationToken = default);
    Task<RecommendationDetails> RecommendAsync(RecommendationRequest request, CancellationToken cancellationToken = default);
    Task<RecommendationDetails?> RecommendationAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RecommendationDetails>> RecommendationsAsync(Guid runId, CancellationToken cancellationToken = default);
    Task<ScenarioDeletionResult> DeleteScenarioAsync(Guid id, string retention, int revision, CancellationToken cancellationToken = default);
}

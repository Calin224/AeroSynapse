using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;

namespace AeroSynapse.Application.Management;

public sealed record RecommendationResourceMove(Guid StaffMemberId, string Name, Guid? BeforeCheckpointId,
    string BeforeCheckpoint, Guid? AfterCheckpointId, string AfterCheckpoint);
public sealed record RecommendationCheckpointChange(Guid CheckpointId, string Name, int Before, int After);
public sealed record RecommendationEstimate(string Metric, string Unit, double Before, double After, double Delta);
public sealed record RecommendationExplanation(IReadOnlyList<RecommendationResourceMove> Resources,
    IReadOnlyList<RecommendationCheckpointChange> Checkpoints, IReadOnlyList<RecommendationEstimate> Estimates,
    IReadOnlyList<string> Assumptions, StaffingSolveRequest Constraints, StaffingAcceptancePolicy AcceptancePolicy,
    string Benefit, IReadOnlyList<string> Risks, string Ranking, int ReallocatedPeople, long ReallocationCost,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<RecommendationScheduleChange>? Schedule = null);
// People per 15-minute slot at a checkpoint, today and with the recommended shift plan.
public sealed record RecommendationScheduleChange(Guid CheckpointId, string Name, IReadOnlyList<int> Before, IReadOnlyList<int> After);
public sealed record RecommendationReviewDetails(Guid Id, Guid EvaluationId, Guid ScenarioId, int ScenarioRevision,
    Guid BaselineRunId, Guid CandidateRunId, string Status, DateTime CreatedAtUtc,
    RecommendationExplanation Explanation, IReadOnlyList<RecommendationAudit> Audit);
public sealed record RecommendationOutcome(string Status, string Message, RecommendationReviewDetails? Recommendation);
public sealed record RecommendationDecisionRequest(Guid CommandId, string Actor, string Reason = "");
public sealed record RecommendationDecisionResult(RecommendationReviewDetails Recommendation, ScenarioDetails? Scenario);

public interface IRecommendationReviewService
{
    Task<RecommendationOutcome> GetForEvaluationAsync(Guid evaluationId, CancellationToken token);
    Task<RecommendationOutcome> CreateAsync(Guid evaluationId, CancellationToken token);
    Task<RecommendationDecisionResult> DecideAsync(Guid id, string action, RecommendationDecisionRequest request, CancellationToken token);
}

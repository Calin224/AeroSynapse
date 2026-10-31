namespace AeroSynapse.Domain.DigitalTwin;

// An actionable review of a simulator-validated recommendation. Legacy comparison
// records remain read-only; only this aggregate can change a synthetic scenario.
public sealed class RecommendationReview : TwinEntity
{
    public Guid SceneId { get; set; }
    public Guid EvaluationId { get; set; }
    public Guid ScenarioId { get; set; }
    public int ScenarioRevision { get; set; }
    public Guid CandidateRunId { get; set; }
    public string ExplanationJson { get; set; } = "{}";
    public string Status { get; set; } = "pending";
}

public sealed class RecommendationAudit : TwinEntity
{
    public Guid SceneId { get; set; }
    public Guid RecommendationId { get; set; }
    public Guid CommandId { get; set; }
    public string Action { get; set; } = "";
    public string Actor { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTime RecordedAtUtc { get; set; }
    public int ScenarioRevisionBefore { get; set; }
    public int ScenarioRevisionAfter { get; set; }
    public string BeforeJson { get; set; } = "{}";
    public string AfterJson { get; set; } = "{}";
}

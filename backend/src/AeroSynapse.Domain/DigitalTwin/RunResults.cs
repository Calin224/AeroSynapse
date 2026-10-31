namespace AeroSynapse.Domain.DigitalTwin;

public sealed class RunMetric : TwinEntity
{
    public Guid SceneId { get; set; }
    public Guid RunId { get; set; }
    public DateTime RecordedAtUtc { get; set; }
    public string ZoneType { get; set; } = "";
    public Guid ZoneId { get; set; }
    public string Name { get; set; } = "";
    public decimal Value { get; set; }
    public string Unit { get; set; } = "";
}

// An output state at simulation time, distinct from the immutable input snapshot.
public sealed class RunSnapshot : TwinEntity
{
    public Guid SceneId { get; set; }
    public Guid RunId { get; set; }
    public DateTime RecordedAtUtc { get; set; }
    public string ZoneType { get; set; } = "";
    public Guid ZoneId { get; set; }
    public string PayloadJson { get; set; } = "{}";
}

public sealed class RunRecommendation : TwinEntity
{
    public Guid SceneId { get; set; }
    public Guid BaselineRunId { get; set; }
    public string Objective { get; set; } = "";
    public decimal BaselineScore { get; set; }
    public string Rationale { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class RecommendationCandidate : TwinEntity
{
    public Guid SceneId { get; set; }
    public Guid RecommendationId { get; set; }
    public Guid RunId { get; set; }
    public int Rank { get; set; }
    public decimal Score { get; set; }
    public string AssessmentJson { get; set; } = "{}";
}

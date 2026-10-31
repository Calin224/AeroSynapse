namespace AeroSynapse.Domain.DigitalTwin;

// No foreign keys to mutable aggregates: evidence survives archive/purge.
public sealed class AuditEvent
{
    public long Sequence { get; set; }
    public Guid Id { get; set; }
    public string ActorId { get; set; } = "system";
    public string Actor { get; set; } = "system";
    public string CorrelationId { get; set; } = "";
    public string Action { get; set; } = "";
    public string ResourceType { get; set; } = "";
    public Guid ResourceId { get; set; }
    public Guid? ScenarioId { get; set; }
    public Guid? RunId { get; set; }
    public Guid? RecommendationId { get; set; }
    public int? ScenarioVersionBefore { get; set; }
    public int? ScenarioVersionAfter { get; set; }
    public DateTime RecordedAtUtc { get; set; }
    public double? VirtualMinute { get; set; }
    public bool IsSynthetic { get; set; }
    public string ChangesJson { get; set; } = "{}";
}

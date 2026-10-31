namespace AeroSynapse.Domain.DigitalTwin;

public sealed class CopilotWorkflowRecord
{
    public Guid Id { get; set; }
    public Guid SceneId { get; set; }
    public Guid ScenarioId { get; set; }
    public Guid RunId { get; set; }
    public Guid ZoneId { get; set; }
    public int Minute { get; set; }
    public string PlaybookId { get; set; } = "";
    public string Status { get; set; } = "pending";
    public int Revision { get; set; } = 1;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime DeadlineAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public bool CancellationRequested { get; set; }
    public bool CancellationPending { get; set; }
    public string? StopCode { get; set; }
    public Guid? ReplayOfId { get; set; }
    public Guid? EvaluationId { get; set; }
    public string RequestJson { get; set; } = "{}";
    public string DefinitionJson { get; set; } = "{}";
    public string StepsJson { get; set; } = "[]";
    public string? ConclusionJson { get; set; }
    public string ManifestHash { get; set; } = "";
    public string SourceHash { get; set; } = "";
    public int Seed { get; set; }
    public string? EngineVersion { get; set; }
    public string? EngineBuild { get; set; }
    public DateTime NextAttemptAtUtc { get; set; }
    public int? PolicyVersion { get; set; }
    public string? PromptVersion { get; set; }
}

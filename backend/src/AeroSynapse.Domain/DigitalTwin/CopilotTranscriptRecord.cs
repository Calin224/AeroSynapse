namespace AeroSynapse.Domain.DigitalTwin;

public sealed class CopilotTranscriptRecord
{
    public Guid Id { get; set; }
    public Guid SceneId { get; set; }
    public Guid ScenarioId { get; set; }
    public Guid RunId { get; set; }
    public Guid ZoneId { get; set; }
    public int Minute { get; set; }
    public string Kind { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public bool Redacted { get; set; }
    public string? Question { get; set; }
    public string ExplanationJson { get; set; } = "{}";
}

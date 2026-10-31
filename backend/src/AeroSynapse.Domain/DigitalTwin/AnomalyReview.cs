namespace AeroSynapse.Domain.DigitalTwin;

// Demo decision on one alert episode. Append-only: the latest decision for an alert is current.
// Reviews record an analyst's judgement only; nothing is dispatched to any operational system.
public sealed class AnomalyReview : TwinEntity
{
    public Guid SceneId { get; set; }
    public Guid RunId { get; set; }
    public Guid BatchId { get; set; }
    public string SettingsKey { get; set; } = "";
    public string SettingsJson { get; set; } = "{}";
    public string DetectorVersion { get; set; } = "";
    public Guid CheckpointId { get; set; }
    public int OnsetMinute { get; set; }
    public string Decision { get; set; } = "";
    public Guid CommandId { get; set; }
    public string Actor { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTime RecordedAtUtc { get; set; }
    public string EpisodeJson { get; set; } = "{}";
}

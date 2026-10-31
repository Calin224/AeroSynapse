namespace AeroSynapse.Domain.DigitalTwin;

// Each immutable revision is explicitly activated by an administrator.
public sealed class AiPolicyRecord
{
    public Guid Id { get; set; }
    public int Version { get; set; }
    public DateTime RecordedAtUtc { get; set; }
    public string ActorId { get; set; } = "system";
    public string PromptVersion { get; set; } = "topic-selector/1.0";
    public string AllowedToolsJson { get; set; } = "[]";
    public bool ModelEnabled { get; set; }
    public int DailyCallLimit { get; set; }
    public decimal DailyBudgetUsd { get; set; }
    public decimal CallReservationUsd { get; set; }
}

// Separate from simulator telemetry. Reservations survive failure/restart and
// are never silently refunded when the provider's actual charge is unknown.
public sealed class AiAgentEvent
{
    public Guid Id { get; set; }
    public Guid AttemptId { get; set; }
    public DateTime RecordedAtUtc { get; set; }
    public string ActorId { get; set; } = "system";
    public string CorrelationId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Status { get; set; } = "";
    public string? Tool { get; set; }
    public Guid? ScenarioId { get; set; }
    public Guid? RunId { get; set; }
    public int PolicyVersion { get; set; }
    public string PromptVersion { get; set; } = "";
    public string PromptHash { get; set; } = "";
    public decimal ReservedUsd { get; set; }
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public double? DurationMs { get; set; }
}

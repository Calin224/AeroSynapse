namespace AeroSynapse.Domain.DigitalTwin;

public sealed class OperationalNotification : TwinEntity
{
    public Guid ScenarioId { get; set; }
    public Guid RunId { get; set; }
    public Guid ZoneId { get; set; }
    public string ScenarioName { get; set; } = "";
    public string ZoneName { get; set; } = "";
    public string Source { get; set; } = "queue_forecast";
    public string RuleKey { get; set; } = "";
    public int WarningThreshold { get; set; }
    public int CriticalThreshold { get; set; }
    public int HorizonMinutes { get; set; }
    public string Level { get; set; } = "warning";
    public int Revision { get; set; } = 1;
    public int Observations { get; set; }
    public int LatestFromMinute { get; set; }
    public DateTime FirstObservedAtUtc { get; set; }
    public DateTime LastObservedAtUtc { get; set; }
    public DateTime SimulationAtUtc { get; set; }
    public string EvidenceJson { get; set; } = "{}";
    public string Assignee { get; set; } = "";
    public DateTime? AcknowledgedAtUtc { get; set; }
    public string AcknowledgedBy { get; set; } = "";
}

public sealed class NotificationActivity : TwinEntity
{
    public Guid AlertId { get; set; }
    public string Kind { get; set; } = "";
    public string Actor { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime RecordedAtUtc { get; set; }
    public int AlertRevision { get; set; }
    public string? ObservationKey { get; set; }
    public string EvidenceJson { get; set; } = "{}";
}

public sealed class NotificationCommand : TwinEntity
{
    public Guid ScenarioId { get; set; }
    public string RequestHash { get; set; } = "";
    public string ReceiptJson { get; set; } = "{}";
}

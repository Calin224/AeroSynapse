namespace AeroSynapse.Domain.DigitalTwin;

public sealed class OperationsCursor
{
    public Guid ScenarioId { get; set; }
    public long Sequence { get; set; }
}

public sealed class OperationsEvent
{
    public Guid ScenarioId { get; set; }
    public long Sequence { get; set; }
    public string Type { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
}

public sealed class RunProgress
{
    public Guid RunId { get; set; }
    public double SimulationMinute { get; set; }
    public string ZonesJson { get; set; } = "[]";
    public DateTime? PublishedAtUtc { get; set; }
}

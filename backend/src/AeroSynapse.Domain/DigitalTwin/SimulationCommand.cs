namespace AeroSynapse.Domain.DigitalTwin;

// A durable outbox entry. A recorded dispatch is never automatically sent again.
public sealed class SimulationCommand
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public Guid ScenarioId { get; set; }
    public int ScenarioRevision { get; set; }
    public string CorrelationId { get; set; } = "";
    public string RequestJson { get; set; } = "{}";
    public DateTime NextAttemptAtUtc { get; set; }
    public DateTime? DispatchedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public string? ErrorCode { get; set; }
}

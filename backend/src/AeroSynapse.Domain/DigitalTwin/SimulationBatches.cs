namespace AeroSynapse.Domain.DigitalTwin;

public sealed class SimulationBatch : TwinEntity
{
    public Guid? ReplayOfBatchId { get; set; }
    public string Name { get; set; } = "";
    public string RequestJson { get; set; } = "{}";
    public string SeedsJson { get; set; } = "[]";
    public int TimeLimitSeconds { get; set; }
    public string Status { get; set; } = "pending";
    public int Revision { get; set; } = 1;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime DeadlineAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public DateTime? CancellationRequestedAtUtc { get; set; }
    public string? FailureReason { get; set; }
    public string? EngineBuild { get; set; }
}

public sealed class BatchVariant : TwinEntity
{
    public Guid BatchId { get; set; }
    public int Position { get; set; }
    public Guid ScenarioId { get; set; }
    public int ScenarioRevision { get; set; }
    public string Name { get; set; } = "";
    public string InputSnapshotJson { get; set; } = "{}";
}

public sealed class BatchReplication : TwinEntity
{
    public Guid BatchId { get; set; }
    public Guid VariantId { get; set; }
    public Guid RunId { get; set; }
    public int SeedIndex { get; set; }
    public int Seed { get; set; }
    public string Status { get; set; } = "pending";
    public bool RemoteCancellationPending { get; set; }
    public DateTime? NextCancellationAttemptAtUtc { get; set; }
}

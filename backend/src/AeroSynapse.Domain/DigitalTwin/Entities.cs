namespace AeroSynapse.Domain.DigitalTwin;

// Synthetic scene storage model; airport maps are defined in Domain/Airports.
public abstract class TwinEntity
{
    public Guid Id { get; set; }
    public bool IsSynthetic { get; set; }
}

public sealed class Scene : TwinEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
}

public sealed class Terminal : TwinEntity
{
    public Guid SceneId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class Checkpoint : TwinEntity
{
    public Guid SceneId { get; set; }
    public Guid TerminalId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public int CapacityPerMinute { get; set; }
}

public sealed class Gate : TwinEntity
{
    public Guid SceneId { get; set; }
    public Guid TerminalId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public int Capacity { get; set; }
}

// Check-in desks before security. Service and the walk to the controls belong to the layout; staffing to the scenario.
public sealed class CheckInArea : TwinEntity
{
    public Guid SceneId { get; set; }
    public Guid TerminalId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public int DeskCount { get; set; }
    public int ServiceSeconds { get; set; }
    public int WalkingSeconds { get; set; }
}

public sealed class StaffMember : TwinEntity
{
    public Guid SceneId { get; set; }
    public Guid TerminalId { get; set; }
    public Guid? CheckpointId { get; set; }
    public string Code { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Role { get; set; } = "";
}

public sealed class PassengerRoute : TwinEntity
{
    public Guid SceneId { get; set; }
    public Guid TerminalId { get; set; }
    public Guid FromCheckpointId { get; set; }
    public Guid ToGateId { get; set; }
    public string Code { get; set; } = "";
    public decimal DistanceMeters { get; set; }
    public int TravelSeconds { get; set; }
}

public sealed class SimulationRun : TwinEntity
{
    public string? InputHash { get; set; }
    public string? EngineVersion { get; set; }
    public string? EngineBuild { get; set; }
    public string? ResultHash { get; set; }
    public double? EngineMilliseconds { get; set; }
    public bool ExecutionRequested { get; set; }
    public Guid? RetryOfRunId { get; set; }
    public Guid SceneId { get; set; }
    public Guid? ScenarioId { get; set; }
    public int? ScenarioRevision { get; set; }
    public string? InputSnapshotJson { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? LaunchedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public string? FailureReason { get; set; }
    public int Revision { get; set; } = 1;
    public string Status { get; set; } = "planned";
    public bool ResultsValid => Status == "completed";
    public int Seed { get; set; }
    public string ParametersJson { get; set; } = "{}";
}

public sealed class SceneEvent : TwinEntity
{
    public Guid SceneId { get; set; }
    public Guid? SimulationRunId { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public string Type { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
}

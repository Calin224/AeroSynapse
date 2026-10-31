namespace AeroSynapse.Domain.DigitalTwin;

// A published layout is immutable. Its document contains the complete scene graph.
public sealed class LayoutVersion : TwinEntity
{
    public Guid SceneId { get; set; }
    public int Version { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string LayoutJson { get; set; } = "{}";
}

// Operational inputs are one aggregate, persisted atomically as a typed JSON document.
public sealed class Scenario : TwinEntity
{
    public Guid SceneId { get; set; }
    public Guid LayoutVersionId { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int Revision { get; set; } = 1;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
    public string ConfigurationJson { get; set; } = "{}";
    public string? DataSourceJson { get; set; }
}

public sealed record ScenarioInputs
{
    public DateTime StartsAtUtc { get; init; }
    public int DurationMinutes { get; init; }
    public int Seed { get; init; } = 42;
    public string TimeZone { get; init; } = "UTC";
    public IReadOnlyList<ScenarioFlight> Flights { get; init; } = [];
    public IReadOnlyList<PassengerPopulation> Populations { get; init; } = [];
    public IReadOnlyList<PassengerFlow> Flows { get; init; } = [];
    public IReadOnlyList<StaffAllocation> StaffAllocations { get; init; } = [];
    public IReadOnlyList<ScenarioInputEvent> Events { get; init; } = [];
    public PassengerGenerationConfig? PassengerGeneration { get; init; }
    public NavigationConfig? Navigation { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IntegratedResourceConfig? IntegratedResources { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? SnapshotIntervalMinutes { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ScenarioCheckIn? CheckIn { get; init; }
    // When present, these shifts staff the checkpoints over the day instead of the fixed StaffAllocations.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ScenarioStaffShifts? StaffShifts { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ScenarioJourney? Journey { get; init; }
    // When present, an AI agent decides during each run of this scenario; see ScenarioAgent.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ScenarioAgent? Agent { get; init; }
    // The people the user entered, each with one specialisation; see ScenarioOperator.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ScenarioOperator>? Operators { get; init; }
}

// Role is security or checkIn. A security operator names a layout staff slot (Id) placed by StaffAllocations;
// check-in operators staff the CheckIn desks, one each.
public sealed record ScenarioOperator(Guid Id, string Name, string Role);

// Who works where and when, in minutes from the scenario start. A person moving straight from one checkpoint
// to another starts serving TransferMinutes after the segment begins.
public sealed record ScenarioStaffShifts(int SlotMinutes, int TransferMinutes, IReadOnlyList<ScenarioStaffShift> Shifts);
public sealed record ScenarioStaffShift(Guid StaffMemberId, IReadOnlyList<ScenarioShiftSegment> Segments);
public sealed record ScenarioShiftSegment(Guid CheckpointId, int StartMinute, int EndMinute);

// Staffed desks of a layout check-in area and the share of passengers checking hold baggage there.
public sealed record ScenarioCheckIn(Guid AreaId, int StaffCount, decimal HoldBaggageShare);

// Optional boarding fields preserve existing scenario documents; the scheduling layer resolves legacy defaults.
public sealed record ScenarioFlight(Guid Id, string Code, Guid GateId, DateTime DepartureAtUtc, int PassengerCapacity,
    DateTime? BoardingOpensAtUtc = null, DateTime? BoardingClosesAtUtc = null, int DelayMinutes = 0,
    string Status = "scheduled", bool IsSynthetic = true);
public sealed record PassengerPopulation(Guid Id, string Name, int Count);
public sealed record PassengerFlow(Guid Id, Guid PopulationId, Guid FlightId, Guid RouteId,
    int PassengerCount, DateTime StartsAtUtc, DateTime EndsAtUtc);
// One person can be assigned once per scenario. Availability belongs to the selected layout version.
public sealed record StaffAllocation(Guid StaffMemberId, Guid CheckpointId);
public sealed record ScenarioInputEvent(Guid Id, string Type, DateTime OccurredAtUtc,
    Guid? CheckpointId, Guid? GateId, Guid? FlightId, string PayloadJson = "{}");

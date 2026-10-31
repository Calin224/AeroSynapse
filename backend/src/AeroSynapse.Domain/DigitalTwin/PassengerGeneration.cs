using System.Text.Json.Serialization;

namespace AeroSynapse.Domain.DigitalTwin;

// Times are simulated minutes; service/travel distributions are dimensionless multipliers of layout durations.
public sealed record PassengerDistribution(string Kind, double Minimum, double Maximum, double Mean, double StdDev, double Mode);
public sealed record PassengerGroupProfile(string Kind, double Weight, int MinSize, int MaxSize,
    double ServiceMultiplier, double TravelMultiplier);
public sealed record PassengerDemandSurge(double StartsMinute, double EndsMinute, double Share);
// CheckpointMode "spread" lets each group use any staffed checkpoint routed to the flight's gate;
// CheckpointId stays the flight's own control. Absent means "fixed", as in configurations saved before it existed.
public sealed record PassengerBatchConfig(Guid FlightId, Guid CheckpointId, string CountMode, int ModeledCount,
    PassengerDistribution Arrival, PassengerDistribution Service, PassengerDistribution Travel,
    IReadOnlyList<PassengerGroupProfile> Groups, IReadOnlyList<PassengerDemandSurge> Surges,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CheckpointMode = null)
{
    public const string Spread = "spread";
}

// Passenger types: the kind is a statistics label; routes and durations come only from the explicit fields.
// Optional members are omitted when absent, matching the shared contract where they are not nullable.
public sealed record PassengerSegmentProfile(string Kind, double Share, bool HoldBaggage, string Control,
    double ServiceMultiplier, double TravelMultiplier,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PassengerDistribution? TransferMinutes = null);
// Stage service is in minutes per passenger; security uses the checkpoint staffing and batch service instead.
public sealed record PassengerStageConfig(string Kind, int StaffCount, PassengerDistribution Service);
public sealed record PassengerStageClosure(string Stage, double StartsMinute, double EndsMinute);
public sealed record PassengerFlowConfig(IReadOnlyList<PassengerSegmentProfile> Segments,
    IReadOnlyList<PassengerStageConfig> Stages, IReadOnlyList<PassengerStageClosure> Closures);

public sealed record PassengerGenerationConfig(string GeneratorVersion, IReadOnlyList<PassengerBatchConfig> Batches,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PassengerFlowConfig? Flow = null);

namespace AeroSynapse.Domain.DigitalTwin;

// Immutable, validated reads. No credentials, endpoint URLs or free-form provider payloads.
public sealed class ExternalDataSnapshot
{
    public Guid Id { get; set; }
    public Guid ScenarioId { get; set; }
    public int ScenarioRevision { get; set; }
    public string ProviderId { get; set; } = "";
    public bool IsSynthetic { get; set; }
    public DateTime RetrievedAtUtc { get; set; }
    public string ContentHash { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
}

public sealed record SourceMapping(string Kind, string ExternalId, Guid InternalId, DateTime ObservedAtUtc);
public sealed record DataSourceProvenance(string ContractVersion, Guid SnapshotId, string ProviderId,
    string Origin, DateTime RetrievedAtUtc, string ContentHash, Guid SourceScenarioId, int SourceScenarioRevision,
    int ImportedAtRevision, IReadOnlyList<SourceMapping> Mapping, bool SyntheticPassengerProfiles = false);

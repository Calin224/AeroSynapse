namespace AeroSynapse.Domain.Airports;

// Flat collections and explicit IDs keep the snapshot free of navigation cycles.
public sealed class AirportMap
{
    public Airport Airport { get; set; } = new();
    public List<Terminal> Terminals { get; set; } = [];
    public List<Floor> Floors { get; set; } = [];
    public List<Zone> Zones { get; set; } = [];
    public List<Route> Routes { get; set; } = [];
    public List<Checkpoint> Checkpoints { get; set; } = [];
    public List<Gate> Gates { get; set; } = [];
    public List<ResourcePool> ResourcePools { get; set; } = [];

    public IEnumerable<IMapElement> Elements() =>
        new IMapElement[] { Airport }
            .Concat(Terminals).Concat(Floors).Concat(Zones).Concat(Routes)
            .Concat(Checkpoints).Concat(Gates).Concat(ResourcePools);
}

public enum MapIssueSeverity { Error, Warning }

public sealed record MapIssue(
    string Code, Guid? ObjectId, string Field, string Message,
    MapIssueSeverity Severity = MapIssueSeverity.Error);

public sealed class AirportMapValidationException(IReadOnlyList<MapIssue> issues)
    : Exception("Invalid airport map.")
{
    public IReadOnlyList<MapIssue> Issues { get; } = issues;
}

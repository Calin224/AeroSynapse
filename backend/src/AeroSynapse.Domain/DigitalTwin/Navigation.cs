namespace AeroSynapse.Domain.DigitalTwin;

// These are operational zone anchors in meters, independent of renderer frames and interpolation.
public sealed record NavigationNode(Guid Id, string Name, string Kind, double X, double Y, double Z,
    bool IsActive, bool IsStepFree, IReadOnlyList<string> RequiredTags);
public sealed record NavigationEdge(Guid Id, Guid FromZoneId, Guid ToZoneId, double DistanceMeters,
    double TravelSeconds, bool IsBidirectional, bool IsActive, bool IsStepFree, IReadOnlyList<string> RequiredTags);
public sealed record NavigationGraph(IReadOnlyList<NavigationNode> Nodes, IReadOnlyList<NavigationEdge> Edges);
public sealed record NavigationBinding(Guid ResourceId, Guid ZoneId);
public sealed record NavigationAccessProfile(string GroupKind, bool RequireStepFree, IReadOnlyList<string> AccessTags,
    IReadOnlyList<Guid> ForbiddenZoneIds, double WalkingMultiplier);
public record NavigationConfig(string NavigationVersion, NavigationGraph Graph,
    IReadOnlyList<NavigationBinding> CheckpointBindings, IReadOnlyList<NavigationBinding> GateBindings,
    IReadOnlyList<NavigationAccessProfile> AccessProfiles, IReadOnlyList<Guid> ClosedZoneIds, IReadOnlyList<Guid> ClosedEdgeIds);

using AeroSynapse.Domain.Airports;

namespace AeroSynapse.Application.Airports;

public sealed record AirportMapSnapshot(AirportMap Map, IReadOnlyList<MapIssue> Issues)
{
    public int SchemaVersion => 1;
    public bool IsNavigable => Map.Airport.IsActive && Map.Terminals.Any(terminal => terminal.IsActive)
        && Issues.All(issue => issue.Severity != MapIssueSeverity.Error);
}

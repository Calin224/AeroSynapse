using AeroSynapse.Application.Airports;
using AeroSynapse.Domain.Airports;

namespace AeroSynapse.Tests;

public sealed class NavigationGraphImporterTests
{
    [Fact]
    public void Import_preserves_identity_coordinates_bindings_and_route_direction()
    {
        var map = MapFixture.Create();
        map.Zones[0].X = map.Zones[0].Y = map.Zones[0].Z = 0;
        map.Zones[1].X = 3;
        map.Zones[1].Y = 4;
        map.Zones[1].Z = 0;
        map.Routes[0].IsBidirectional = false;

        var config = NavigationGraphImporter.FromMap(map);

        Assert.Equal("1.0", config.NavigationVersion);
        Assert.Equal(map.Zones.Select(zone => zone.Id), config.Graph.Nodes.Select(node => node.Id));
        var edge = config.Graph.Edges.Single(item => item.Id == map.Routes[0].Id);
        Assert.Equal(5, edge.DistanceMeters);
        Assert.Equal(map.Routes[0].TravelDurationSeconds, edge.TravelSeconds);
        Assert.Equal(map.Routes[0].FromZoneId, edge.FromZoneId);
        Assert.Equal(map.Routes[0].ToZoneId, edge.ToZoneId);
        Assert.False(edge.IsBidirectional);
        var node = config.Graph.Nodes.Single(item => item.Id == map.Zones[1].Id);
        Assert.Equal((3d, 4d, 0d), (node.X, node.Y, node.Z));
        Assert.Equal(map.Gates[0].Id, Assert.Single(config.GateBindings).ResourceId);
        Assert.Equal(map.Gates[0].ZoneId, Assert.Single(config.GateBindings).ZoneId);
        Assert.Equal(map.Checkpoints[0].Id, Assert.Single(config.CheckpointBindings).ResourceId);
        Assert.Equal(new[] { "solo", "family", "business", "tour" }, config.AccessProfiles.Select(profile => profile.GroupKind));
    }

    [Theory]
    [InlineData("airport")]
    [InlineData("terminal")]
    [InlineData("floor")]
    public void Inactive_parents_disable_every_node_and_edge(string parent)
    {
        var map = MapFixture.Create();
        if (parent == "airport") map.Airport.IsActive = false;
        else if (parent == "terminal") map.Terminals[0].IsActive = false;
        else map.Floors[0].IsActive = false;

        var config = NavigationGraphImporter.FromMap(map);

        Assert.All(config.Graph.Nodes, node => Assert.False(node.IsActive));
        Assert.All(config.Graph.Edges, edge => Assert.False(edge.IsActive));
    }

    [Theory]
    [InlineData("gate")]
    [InlineData("checkpoint")]
    [InlineData("pool")]
    [InlineData("poolZone")]
    [InlineData("zone")]
    public void Unavailable_resources_or_zones_disable_connected_edges(string mutation)
    {
        var map = MapFixture.Create();
        var affectedZone = map.Zones[1].Id;
        switch (mutation)
        {
            case "gate": map.Gates[0].IsActive = false; affectedZone = map.Gates[0].ZoneId; break;
            case "checkpoint": map.Checkpoints[0].IsActive = false; break;
            case "pool": map.ResourcePools[0].IsActive = false; break;
            case "poolZone": map.ResourcePools[0].ZoneId = map.Zones[2].Id; map.Zones[2].IsActive = false; break;
            case "zone": map.Zones[1].IsActive = false; break;
        }

        var config = NavigationGraphImporter.FromMap(map);

        Assert.False(config.Graph.Nodes.Single(node => node.Id == affectedZone).IsActive);
        Assert.All(config.Graph.Edges.Where(edge => edge.FromZoneId == affectedZone || edge.ToZoneId == affectedZone),
            edge => Assert.False(edge.IsActive));
    }

    [Fact]
    public void Stairs_disable_step_free_access_and_inactive_routes_remain_inactive()
    {
        var map = MapFixture.Create();
        map.Zones[1].Type = ZoneType.Stairs;
        map.Routes[0].IsActive = false;

        var config = NavigationGraphImporter.FromMap(map);

        Assert.False(config.Graph.Nodes.Single(node => node.Id == map.Zones[1].Id).IsStepFree);
        Assert.All(config.Graph.Edges, edge => Assert.False(edge.IsStepFree));
        Assert.False(config.Graph.Edges.Single(edge => edge.Id == map.Routes[0].Id).IsActive);
    }

    [Theory]
    [InlineData("reference")]
    [InlineData("coordinate")]
    [InlineData("name")]
    [InlineData("duration")]
    [InlineData("minDuration")]
    [InlineData("distance")]
    [InlineData("gateKind")]
    public void Invalid_maps_or_values_outside_navigation_contract_bounds_are_rejected(string mutation)
    {
        var map = MapFixture.Create();
        switch (mutation)
        {
            case "reference": map.Routes[0].ToZoneId = Guid.NewGuid(); break;
            case "coordinate": map.Zones[0].X = 1_000_001; break;
            case "name": map.Zones[0].Name = new string('x', 161); break;
            case "duration": map.Routes[0].TravelDurationSeconds = 604_801; break;
            case "minDuration": map.Routes[0].TravelDurationSeconds = .0001; break;
            case "distance": map.Zones[0].X = -1_000_000; map.Zones[1].X = 1_000_000; break;
            case "gateKind": map.Zones[2].Type = ZoneType.Lounge; break;
        }

        var failure = Assert.Throws<AirportMapValidationException>(() => NavigationGraphImporter.FromMap(map));
        Assert.NotEmpty(failure.Issues);
        Assert.All(failure.Issues, issue => Assert.Equal(MapIssueSeverity.Error, issue.Severity));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2001)]
    public void Navigation_zone_count_limits_are_enforced(int count)
    {
        var map = MapFixture.Create();
        var template = map.Zones[0];
        map.Zones = Enumerable.Range(0, count).Select(index => new Zone
        {
            Id = Guid.NewGuid(), Name = $"Zone {index}", AirportId = template.AirportId,
            TerminalId = template.TerminalId, FloorId = template.FloorId, Type = ZoneType.Walkway, Capacity = 10
        }).ToList();
        map.Routes.Clear();
        map.Gates.Clear();
        map.Checkpoints.Clear();
        map.ResourcePools.Clear();

        var failure = Assert.Throws<AirportMapValidationException>(() => NavigationGraphImporter.FromMap(map));
        Assert.Contains(failure.Issues, issue => issue.Code == "navigation_limit" && issue.Field == "zones");
    }
}

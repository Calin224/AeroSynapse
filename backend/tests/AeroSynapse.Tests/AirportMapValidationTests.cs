using AeroSynapse.Application.Airports;
using AeroSynapse.Domain.Airports;

namespace AeroSynapse.Tests;

public sealed class AirportMapValidationTests
{
    [Fact]
    public void Connected_map_with_checkpoint_is_valid()
    {
        Assert.Empty(AirportMapValidator.Validate(MapFixture.Create()));
    }

    [Theory]
    [InlineData("empty", "invalid_id")]
    [InlineData("duplicate", "duplicate_id")]
    [InlineData("airport", "cross_airport_reference")]
    [InlineData("terminal", "missing_terminal")]
    [InlineData("floor", "missing_floor")]
    [InlineData("zone", "missing_zone")]
    [InlineData("pool", "missing_resource_pool")]
    public void Invalid_identity_or_reference_is_reported(string mutation, string code)
    {
        var map = MapFixture.Create();
        switch (mutation)
        {
            case "empty": map.Zones[0].Id = Guid.Empty; break;
            case "duplicate": map.Zones[0].Id = map.Airport.Id; break;
            case "airport": map.Zones[0].AirportId = Guid.NewGuid(); break;
            case "terminal": map.Zones[0].TerminalId = Guid.NewGuid(); break;
            case "floor": map.Zones[0].FloorId = Guid.NewGuid(); break;
            case "zone": map.Routes[0].ToZoneId = Guid.NewGuid(); break;
            case "pool": map.Checkpoints[0].ResourcePoolId = Guid.NewGuid(); break;
        }
        Assert.Contains(AirportMapValidator.Validate(map), issue => issue.Code == code);
    }

    [Theory]
    [InlineData("route")]
    [InlineData("checkpoint")]
    public void References_to_another_terminal_are_rejected(string mutation)
    {
        var map = MapFixture.Create();
        var terminal = new Terminal { Id = Guid.NewGuid(), AirportId = map.Airport.Id, Name = "Terminal 2", IsActive = false };
        map.Terminals.Add(terminal);
        if (mutation == "route") map.Routes[0].TerminalId = terminal.Id;
        else map.ResourcePools[0].TerminalId = terminal.Id;
        Assert.Contains(AirportMapValidator.Validate(map), issue => issue.Code == "cross_terminal_reference");
    }

    [Theory]
    [InlineData("zoneCapacity")]
    [InlineData("checkpointCapacity")]
    [InlineData("gateCapacity")]
    [InlineData("poolCapacity")]
    [InlineData("processingRate")]
    [InlineData("boardingRate")]
    [InlineData("duration")]
    public void Nonpositive_capacities_rates_and_durations_are_rejected(string mutation)
    {
        var map = MapFixture.Create();
        switch (mutation)
        {
            case "zoneCapacity": map.Zones[0].Capacity = 0; break;
            case "checkpointCapacity": map.Checkpoints[0].Capacity = -1; break;
            case "gateCapacity": map.Gates[0].Capacity = 0; break;
            case "poolCapacity": map.ResourcePools[0].Capacity = 0; break;
            case "processingRate": map.Checkpoints[0].ProcessingRatePerMinute = -1; break;
            case "boardingRate": map.Gates[0].BoardingRatePerMinute = 0; break;
            case "duration": map.Routes[0].TravelDurationSeconds = 0; break;
        }
        Assert.Contains(AirportMapValidator.Validate(map), issue => issue.Code == "invalid_value");
    }

    [Fact]
    public void Nonfinite_values_are_rejected_but_negative_local_coordinates_are_valid()
    {
        var map = MapFixture.Create();
        map.Zones[0].X = -10;
        Assert.Empty(AirportMapValidator.Validate(map));
        map.Zones[0].Y = double.NaN;
        map.Floors[0].ElevationMeters = double.NegativeInfinity;
        map.Checkpoints[0].ProcessingRatePerMinute = double.PositiveInfinity;
        var issues = AirportMapValidator.Validate(map);
        Assert.Contains(issues, issue => issue.Code == "invalid_coordinate" && issue.Field == "y");
        Assert.Contains(issues, issue => issue.Field == "elevationMeters");
        Assert.Contains(issues, issue => issue.Code == "invalid_value" && issue.Field == "processingRatePerMinute");
    }

    [Fact]
    public void Duplicate_floor_levels_and_self_routes_are_rejected()
    {
        var map = MapFixture.Create();
        map.Floors.Add(new Floor { Id = Guid.NewGuid(), AirportId = map.Airport.Id, TerminalId = map.Terminals[0].Id, Name = "Duplicate", Level = 0 });
        map.Routes[0].ToZoneId = map.Routes[0].FromZoneId;
        var issues = AirportMapValidator.Validate(map);
        Assert.Contains(issues, issue => issue.Code == "duplicate_floor_level");
        Assert.Contains(issues, issue => issue.Code == "self_route");
    }

    [Theory]
    [InlineData("disconnected")]
    [InlineData("oneWay")]
    [InlineData("inactiveRoute")]
    [InlineData("inactiveBridge")]
    [InlineData("checkpoint")]
    [InlineData("resources")]
    public void Unreachable_zones_are_reported_with_their_id(string mutation)
    {
        var map = MapFixture.Create();
        switch (mutation)
        {
            case "disconnected": map.Routes.RemoveAt(1); break;
            case "oneWay":
                (map.Routes[1].FromZoneId, map.Routes[1].ToZoneId) = (map.Routes[1].ToZoneId, map.Routes[1].FromZoneId);
                map.Routes[1].IsBidirectional = false;
                break;
            case "inactiveRoute": map.Routes[1].IsActive = false; break;
            case "inactiveBridge": map.Zones[1].IsActive = false; break;
            case "checkpoint": map.Checkpoints[0].IsActive = false; break;
            case "resources": map.ResourcePools[0].IsActive = false; break;
        }
        var issues = AirportMapValidator.Validate(map);
        Assert.Contains(issues, issue => issue.Code == "unreachable_zone" && issue.ObjectId == map.Zones[2].Id);
        if (mutation is "checkpoint" or "resources")
            Assert.Contains(issues, issue => issue.Code == "checkpoint_unavailable" && issue.ObjectId == map.Zones[1].Id);
    }

    [Fact]
    public void An_inactive_leaf_is_reported_as_a_warning()
    {
        var map = MapFixture.Create();
        map.Zones[2].IsActive = false;
        var issue = Assert.Single(AirportMapValidator.Validate(map));
        Assert.Equal("inactive_zone", issue.Code);
        Assert.Equal(MapIssueSeverity.Warning, issue.Severity);
        Assert.True(new AirportMapSnapshot(map, [issue]).IsNavigable);
        map.Airport.IsActive = false;
        Assert.False(new AirportMapSnapshot(map, AirportMapValidator.Validate(map)).IsNavigable);
    }

    [Theory]
    [InlineData("removed")]
    [InlineData("inactive")]
    public void Separate_components_are_reported_even_when_each_has_an_entrance(string mutation)
    {
        var map = MapFixture.Create();
        map.Zones[2].Type = ZoneType.Entrance;
        if (mutation == "removed") map.Routes.RemoveAt(1);
        else map.Routes[1].IsActive = false;

        var issues = AirportMapValidator.Validate(map);
        Assert.Contains(issues, issue => issue.Code == "disconnected_zone"
            && issue.ObjectId is not null && issue.Severity == MapIssueSeverity.Error);
        Assert.DoesNotContain(issues, issue => issue.Code == "unreachable_zone");
        Assert.False(new AirportMapSnapshot(map, issues).IsNavigable);
    }

    [Fact]
    public void Multiple_entrances_connected_by_a_one_way_route_are_valid()
    {
        var map = MapFixture.Create();
        map.Zones[2].Type = ZoneType.Entrance;
        map.Routes[1].IsBidirectional = false;
        Assert.Empty(AirportMapValidator.Validate(map));
    }

    [Theory]
    [InlineData("zone")]
    [InlineData("floor")]
    public void Resources_in_an_inactive_zone_or_floor_cannot_operate_a_checkpoint(string mutation)
    {
        var map = MapFixture.Create();
        var floor = new Floor
        {
            Id = Guid.NewGuid(), AirportId = map.Airport.Id, TerminalId = map.Terminals[0].Id,
            Name = "Resource floor", Level = 1, IsActive = mutation != "floor"
        };
        var zone = new Zone
        {
            Id = Guid.NewGuid(), AirportId = map.Airport.Id, TerminalId = map.Terminals[0].Id,
            FloorId = floor.Id, Name = "Resource room", Type = ZoneType.Lounge,
            Capacity = 10, IsActive = mutation != "zone"
        };
        map.Floors.Add(floor);
        map.Zones.Add(zone);
        map.ResourcePools[0].ZoneId = zone.Id;

        var issues = AirportMapValidator.Validate(map);
        Assert.Contains(issues, issue => issue.Code == "checkpoint_unavailable" && issue.ObjectId == map.Zones[1].Id);
        Assert.Contains(issues, issue => issue.Code == "unreachable_zone" && issue.ObjectId == map.Zones[2].Id);
    }

    [Fact]
    public void Missing_entrance_and_null_collections_are_reported()
    {
        var map = MapFixture.Create();
        map.Zones[0].Type = ZoneType.Walkway;
        Assert.Contains(AirportMapValidator.Validate(map), issue => issue.Code == "missing_entrance");
        map.Zones = null!;
        Assert.Contains(AirportMapValidator.Validate(map), issue => issue.Code == "required" && issue.Field == "zones");
    }
}

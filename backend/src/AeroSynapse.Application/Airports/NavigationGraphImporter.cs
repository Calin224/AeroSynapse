using AeroSynapse.Domain.Airports;
using AeroSynapse.Domain.DigitalTwin;

namespace AeroSynapse.Application.Airports;

public static class NavigationGraphImporter
{
    public static NavigationConfig FromMap(AirportMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        // Inactive and disconnected zones remain in the exported graph.
        var issues = AirportMapValidator.Validate(map, validateNavigation: false).ToList();
        if (issues.Count > 0) throw new AirportMapValidationException(issues);

        void Invalid(Guid? id, string field, string message) =>
            issues.Add(new MapIssue("navigation_limit", id, field, message));

        if (map.Zones.Count is < 1 or > 2000) Invalid(null, "zones", "Navigation requires 1 to 2000 zones.");
        if (map.Routes.Count > 10000) Invalid(null, "routes", "Navigation supports at most 10000 routes.");
        if (map.Checkpoints.Count > 1000) Invalid(null, "checkpoints", "Navigation supports at most 1000 checkpoints.");
        if (map.Gates.Count > 1000) Invalid(null, "gates", "Navigation supports at most 1000 gates.");
        foreach (var zone in map.Zones)
        {
            if (zone.Name.Length > 160) Invalid(zone.Id, "name", "Navigation zone names must not exceed 160 characters.");
            if (Math.Abs(zone.X) > 1_000_000) Invalid(zone.Id, "x", "Navigation coordinates must be within +/-1000000 meters.");
            if (Math.Abs(zone.Y) > 1_000_000) Invalid(zone.Id, "y", "Navigation coordinates must be within +/-1000000 meters.");
            if (Math.Abs(zone.Z) > 1_000_000) Invalid(zone.Id, "z", "Navigation coordinates must be within +/-1000000 meters.");
        }
        foreach (var route in map.Routes)
            if (route.TravelDurationSeconds is < .001 or > 604800)
                Invalid(route.Id, "travelDurationSeconds", "Navigation travel time must be between 0.001 and 604800 seconds.");
        if (issues.Count > 0) throw new AirportMapValidationException(issues);

        var floors = map.Floors.ToDictionary(item => item.Id);
        var terminals = map.Terminals.ToDictionary(item => item.Id);
        var zones = map.Zones.ToDictionary(item => item.Id);
        var pools = map.ResourcePools.ToDictionary(item => item.Id);
        var activeZones = map.Zones.Where(zone => map.Airport.IsActive && zone.IsActive
            && floors[zone.FloorId].IsActive && terminals[zone.TerminalId].IsActive).Select(zone => zone.Id).ToHashSet();
        bool ResourceAvailable(Zone zone) => zone.Type switch
        {
            ZoneType.Gate => !map.Gates.Any(gate => gate.ZoneId == zone.Id)
                || map.Gates.Any(gate => gate.ZoneId == zone.Id && gate.IsActive),
            ZoneType.Security or ZoneType.PassportControl => map.Checkpoints.Any(checkpoint => checkpoint.ZoneId == zone.Id
                && checkpoint.IsActive && (checkpoint.ResourcePoolId is null
                    || pools[checkpoint.ResourcePoolId.Value].IsActive && activeZones.Contains(pools[checkpoint.ResourcePoolId.Value].ZoneId))),
            _ => true
        };
        var nodes = map.Zones.Select(zone => new NavigationNode(zone.Id, zone.Name, zone.Type.ToString(), zone.X, zone.Y, zone.Z,
            activeZones.Contains(zone.Id) && ResourceAvailable(zone), zone.Type != ZoneType.Stairs, [])).ToArray();
        var byId = nodes.ToDictionary(item => item.Id);
        var edges = map.Routes.Select(route =>
        {
            var from = byId[route.FromZoneId];
            var to = byId[route.ToZoneId];
            var dx = from.X - to.X;
            var dy = from.Y - to.Y;
            var dz = from.Z - to.Z;
            var distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (distance > 1_000_000) Invalid(route.Id, "distanceMeters", "Navigation route distance must not exceed 1000000 meters.");
            return new NavigationEdge(route.Id, from.Id, to.Id, distance, route.TravelDurationSeconds,
                route.IsBidirectional, route.IsActive && from.IsActive && to.IsActive, from.IsStepFree && to.IsStepFree, []);
        }).ToArray();
        foreach (var gate in map.Gates)
            if (zones[gate.ZoneId].Type != ZoneType.Gate)
                issues.Add(new MapIssue("invalid_gate_zone", gate.Id, "zoneId", "Navigation gates must bind to a Gate zone."));
        if (issues.Count > 0) throw new AirportMapValidationException(issues);

        return new NavigationConfig("1.0", new(nodes, edges),
            map.Checkpoints.Select(item => new NavigationBinding(item.Id, item.ZoneId)).ToArray(),
            map.Gates.Select(item => new NavigationBinding(item.Id, item.ZoneId)).ToArray(),
            new[] { "solo", "family", "business", "tour" }.Select(kind => new NavigationAccessProfile(kind, false, [], [], 1)).ToArray(), [], []);
    }
}

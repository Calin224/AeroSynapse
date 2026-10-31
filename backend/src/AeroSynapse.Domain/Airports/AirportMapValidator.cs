using System.Collections;

namespace AeroSynapse.Domain.Airports;

public static class AirportMapValidator
{
    public static IReadOnlyList<MapIssue> Validate(AirportMap map, bool validateNavigation = true)
    {
        var issues = new List<MapIssue>();
        void Add(string code, Guid? id, string field, string message) =>
            issues.Add(new MapIssue(code, id, field, message));

        if (map.Airport is null)
            Add("required", null, "airport", "Airport is required.");

        (string Name, IEnumerable? Items)[] collections =
        [
            ("terminals", map.Terminals), ("floors", map.Floors), ("zones", map.Zones),
            ("routes", map.Routes), ("checkpoints", map.Checkpoints),
            ("gates", map.Gates), ("resourcePools", map.ResourcePools)
        ];
        foreach (var (name, items) in collections)
        {
            if (items is null)
                Add("required", null, name, "Collection cannot be null.");
            else if (items.Cast<object?>().Any(item => item is null))
                Add("required", null, name, "Collection cannot contain null elements.");
        }
        if (issues.Count > 0) return issues;

        var ids = new HashSet<Guid>();
        foreach (var element in map.Elements())
        {
            if (element.Id == Guid.Empty)
                Add("invalid_id", element.Id, "id", "A non-empty GUID is required.");
            else if (!ids.Add(element.Id))
                Add("duplicate_id", element.Id, "id", "Every map object must have a unique ID.");
            if (string.IsNullOrWhiteSpace(element.Name) || element.Name.Length > 200)
                Add("invalid_name", element.Id, "name", "Name must contain 1 to 200 characters.");
            if (element is IAirportElement owned && owned.AirportId != map.Airport!.Id)
                Add("cross_airport_reference", element.Id, "airportId", "Object belongs to a different airport.");
        }

        // Invalid identities cannot safely be used to construct lookup dictionaries.
        if (issues.Any(issue => issue.Code is "invalid_id" or "duplicate_id")) return issues;

        var terminals = map.Terminals.ToDictionary(item => item.Id);
        var floors = map.Floors.ToDictionary(item => item.Id);
        var zones = map.Zones.ToDictionary(item => item.Id);
        var pools = map.ResourcePools.ToDictionary(item => item.Id);

        void Positive(double value, Guid id, string field)
        {
            if (!double.IsFinite(value) || value <= 0)
                Add("invalid_value", id, field, "Value must be finite and greater than zero.");
        }
        void Finite(double value, Guid id, string field)
        {
            if (!double.IsFinite(value))
                Add("invalid_coordinate", id, field, "Coordinate must be finite.");
        }
        void ZoneReference(ITerminalElement element, Guid zoneId, string field)
        {
            if (!zones.TryGetValue(zoneId, out var zone))
                Add("missing_zone", element.Id, field, "Referenced zone is not in this airport map.");
            else if (zone.AirportId != element.AirportId || zone.TerminalId != element.TerminalId)
                Add("cross_terminal_reference", element.Id, field, "Referenced zone must belong to the same airport and terminal.");
        }

        foreach (var element in map.Elements().OfType<ITerminalElement>())
        {
            if (!terminals.TryGetValue(element.TerminalId, out var terminal))
                Add("missing_terminal", element.Id, "terminalId", "Referenced terminal is not in this airport map.");
            else if (terminal.AirportId != element.AirportId)
                Add("cross_airport_reference", element.Id, "terminalId", "Terminal belongs to a different airport.");
        }
        foreach (var floor in map.Floors)
            Finite(floor.ElevationMeters, floor.Id, "elevationMeters");
        foreach (var duplicate in map.Floors.GroupBy(item => (item.TerminalId, item.Level)).Where(group => group.Count() > 1))
            foreach (var floor in duplicate)
                Add("duplicate_floor_level", floor.Id, "level", "Floor levels must be unique within a terminal.");

        foreach (var zone in map.Zones)
        {
            Positive(zone.Capacity, zone.Id, "capacity");
            Finite(zone.X, zone.Id, "x");
            Finite(zone.Y, zone.Id, "y");
            Finite(zone.Z, zone.Id, "z");
            if (!Enum.IsDefined(zone.Type) || zone.Type == ZoneType.Unknown)
                Add("invalid_zone_type", zone.Id, "type", "A supported zone type is required.");
            if (!floors.TryGetValue(zone.FloorId, out var floor))
                Add("missing_floor", zone.Id, "floorId", "Referenced floor is not in this airport map.");
            else if (floor.AirportId != zone.AirportId || floor.TerminalId != zone.TerminalId)
                Add("cross_terminal_reference", zone.Id, "floorId", "Floor must belong to the same airport and terminal.");
        }
        foreach (var route in map.Routes)
        {
            ZoneReference(route, route.FromZoneId, "fromZoneId");
            ZoneReference(route, route.ToZoneId, "toZoneId");
            Positive(route.TravelDurationSeconds, route.Id, "travelDurationSeconds");
            if (route.FromZoneId == route.ToZoneId)
                Add("self_route", route.Id, "toZoneId", "Route must connect two different zones.");
        }
        foreach (var pool in map.ResourcePools)
        {
            ZoneReference(pool, pool.ZoneId, "zoneId");
            Positive(pool.Capacity, pool.Id, "capacity");
            if (!Enum.IsDefined(pool.Type) || pool.Type == ResourceType.Unknown)
                Add("invalid_resource_type", pool.Id, "type", "A supported resource type is required.");
        }
        foreach (var checkpoint in map.Checkpoints)
        {
            ZoneReference(checkpoint, checkpoint.ZoneId, "zoneId");
            Positive(checkpoint.Capacity, checkpoint.Id, "capacity");
            Positive(checkpoint.ProcessingRatePerMinute, checkpoint.Id, "processingRatePerMinute");
            if (checkpoint.ResourcePoolId is { } poolId)
            {
                if (!pools.TryGetValue(poolId, out var pool))
                    Add("missing_resource_pool", checkpoint.Id, "resourcePoolId", "Referenced resource pool is not in this airport map.");
                else if (pool.AirportId != checkpoint.AirportId || pool.TerminalId != checkpoint.TerminalId)
                    Add("cross_terminal_reference", checkpoint.Id, "resourcePoolId", "Resource pool must belong to the same airport and terminal.");
            }
        }
        foreach (var gate in map.Gates)
        {
            ZoneReference(gate, gate.ZoneId, "zoneId");
            Positive(gate.Capacity, gate.Id, "capacity");
            Positive(gate.BoardingRatePerMinute, gate.Id, "boardingRatePerMinute");
        }

        if (issues.Count == 0 && validateNavigation) ValidateNavigation(map, issues);
        return issues;
    }

    private static void ValidateNavigation(AirportMap map, List<MapIssue> issues)
    {
        var floors = map.Floors.ToDictionary(item => item.Id);
        var pools = map.ResourcePools.ToDictionary(item => item.Id);
        var checkpoints = map.Checkpoints.GroupBy(item => item.ZoneId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var terminal in map.Terminals)
        {
            var terminalZones = map.Zones.Where(zone => zone.TerminalId == terminal.Id).ToArray();
            var activeZones = terminalZones.Where(zone => map.Airport.IsActive && terminal.IsActive
                && floors[zone.FloorId].IsActive && zone.IsActive).Select(zone => zone.Id).ToHashSet();
            var available = new HashSet<Guid>();
            foreach (var zone in terminalZones)
            {
                if (!activeZones.Contains(zone.Id))
                {
                    issues.Add(new("inactive_zone", zone.Id, "isActive", "Zone is unavailable because it or its parent is inactive.", MapIssueSeverity.Warning));
                    continue;
                }
                if (zone.Type is ZoneType.Security or ZoneType.PassportControl)
                {
                    var operational = checkpoints.TryGetValue(zone.Id, out var controls)
                        && controls.Any(control => control.IsActive
                            && (control.ResourcePoolId is null || (pools[control.ResourcePoolId.Value].IsActive
                                && activeZones.Contains(pools[control.ResourcePoolId.Value].ZoneId))));
                    if (!operational)
                    {
                        issues.Add(new("checkpoint_unavailable", zone.Id, "checkpoints", "Control zone has no active checkpoint with available resources."));
                        continue;
                    }
                }
                available.Add(zone.Id);
            }

            if (!map.Airport.IsActive || !terminal.IsActive) continue;
            var entries = terminalZones.Where(zone => zone.Type == ZoneType.Entrance && available.Contains(zone.Id))
                .OrderBy(zone => zone.Id).ToArray();
            if (terminalZones.Length == 0)
                issues.Add(new("missing_zones", terminal.Id, "zones", "Terminal requires at least one zone."));
            if (entries.Length == 0 && terminalZones.Length > 0)
                issues.Add(new("missing_entrance", terminal.Id, "zones", "Terminal requires an active entrance zone."));

            var adjacency = available.ToDictionary(id => id, _ => new List<Guid>());
            var connections = available.ToDictionary(id => id, _ => new List<Guid>());
            foreach (var route in map.Routes.Where(route => route.TerminalId == terminal.Id && route.IsActive))
            {
                if (!available.Contains(route.FromZoneId) || !available.Contains(route.ToZoneId)) continue;
                adjacency[route.FromZoneId].Add(route.ToZoneId);
                if (route.IsBidirectional) adjacency[route.ToZoneId].Add(route.FromZoneId);
                connections[route.FromZoneId].Add(route.ToZoneId);
                connections[route.ToZoneId].Add(route.FromZoneId);
            }

            var visited = ReachableFrom(entries.Select(zone => zone.Id), adjacency);
            foreach (var zone in terminalZones.Where(zone => available.Contains(zone.Id) && !visited.Contains(zone.Id)))
                issues.Add(new("unreachable_zone", zone.Id, "routes", "Zone cannot be reached from any active terminal entrance."));

            // Separate islands can each have an entrance and still be disconnected.
            if (entries.Length > 0)
            {
                var connected = ReachableFrom([entries[0].Id], connections);
                foreach (var zone in terminalZones.Where(zone => available.Contains(zone.Id) && !connected.Contains(zone.Id)))
                    issues.Add(new("disconnected_zone", zone.Id, "routes", "Zone belongs to a separate, disconnected terminal component."));
            }
        }
        if (map.Terminals.Count == 0)
            issues.Add(new("missing_terminals", map.Airport.Id, "terminals", "Airport requires at least one terminal."));
    }

    private static HashSet<Guid> ReachableFrom(IEnumerable<Guid> starts, Dictionary<Guid, List<Guid>> adjacency)
    {
        var visited = starts.ToHashSet();
        var queue = new Queue<Guid>(visited);
        while (queue.TryDequeue(out var current))
            foreach (var next in adjacency[current])
                if (visited.Add(next)) queue.Enqueue(next);
        return visited;
    }
}

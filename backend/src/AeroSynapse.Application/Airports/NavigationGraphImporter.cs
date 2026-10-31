using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.Airports;
using AeroSynapse.Domain.DigitalTwin;

namespace AeroSynapse.Application.Airports;

public static class NavigationGraphImporter
{
    // Resource IDs from the imported graph are preserved. Binding them to operational scene resources is explicit in the UI/API.
    public static NavigationConfig FromMap(AirportMap map)
    {
        var floors = map.Floors.ToDictionary(item => item.Id); var terminals = map.Terminals.ToDictionary(item => item.Id);
        bool ResourceAvailable(Zone zone) => zone.Type switch
        {
            ZoneType.Gate => !map.Gates.Any(gate => gate.ZoneId == zone.Id) || map.Gates.Any(gate => gate.ZoneId == zone.Id && gate.IsActive),
            ZoneType.Security or ZoneType.PassportControl => map.Checkpoints.Any(checkpoint => checkpoint.ZoneId == zone.Id && checkpoint.IsActive
                && (checkpoint.ResourcePoolId is null || map.ResourcePools.Any(pool => pool.Id == checkpoint.ResourcePoolId && pool.IsActive))),
            _ => true
        };
        var nodes = map.Zones.Select(zone => new NavigationNode(zone.Id,zone.Name,zone.Type.ToString(),zone.X,zone.Y,zone.Z,
            map.Airport.IsActive && zone.IsActive && floors[zone.FloorId].IsActive && terminals[zone.TerminalId].IsActive && ResourceAvailable(zone),
            zone.Type != ZoneType.Stairs,[])).ToArray();
        var byId = nodes.ToDictionary(item => item.Id);
        var edges = map.Routes.Select(route =>
        {
            var from = byId[route.FromZoneId]; var to = byId[route.ToZoneId];
            var dx = from.X-to.X; var dy = from.Y-to.Y; var dz = from.Z-to.Z;
            return new NavigationEdge(route.Id,from.Id,to.Id,Math.Sqrt(dx*dx+dy*dy+dz*dz),route.TravelDurationSeconds,
                route.IsBidirectional,route.IsActive && from.IsActive && to.IsActive,from.IsStepFree && to.IsStepFree,[]);
        }).ToArray();
        var config = new NavigationConfig("1.0",new(nodes,edges),
            map.Checkpoints.Select(item => new NavigationBinding(item.Id,item.ZoneId)).ToArray(),
            map.Gates.Select(item => new NavigationBinding(item.Id,item.ZoneId)).ToArray(),
            new[] { "solo","family","business","tour" }.Select(kind => new NavigationAccessProfile(kind,false,[],[],1)).ToArray(),[],[]);
        return NavigationScenarios.Shape(config);
    }
}

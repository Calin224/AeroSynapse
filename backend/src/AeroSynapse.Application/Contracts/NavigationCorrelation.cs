using AeroSynapse.Domain.DigitalTwin;

namespace AeroSynapse.Application.Contracts;

public static class NavigationCorrelation
{
    public static bool Valid(PassengerGenerationRequest generation,NavigationInput navigation,NavigationResult result,bool assessed,int offset,int limit,
        SimulationRequest? run = null,ProcessResult? processes = null)
    {
        // The route plan stays the captured one; with events, assessment follows the process report.
        var timeline = run is null ? null : new EventTimeline(run);
        var actual = timeline is { Events.Count: > 0 } ? processes?.Passengers.ToDictionary(item => item.Id) : null;
        if (timeline is { Events.Count: > 0 } && actual is null) return false;
        var count = generation.Batches.Sum(batch => batch.ModeledCount);
        var returned = Math.Min(limit,Math.Max(0,count-offset));
        if (result.Assessed != assessed || result.ModeledCount != count || result.Offset != offset || result.ReturnedCount != returned
            || result.HasMore != (offset+returned<count) || result.Journeys.Count != returned
            || result.Journeys.Select(item => item.Id).Distinct().Count() != returned
            || result.Routes.Select(item => item.RouteKey).Distinct().Count() != result.Routes.Count) return false;
        var flights = generation.Flights.ToDictionary(item => item.FlightId);
        var destinations = navigation.FlightGates.ToDictionary(item => item.FlightId,item => item.GateId);
        var batches = generation.Batches.ToDictionary(item => item.FlightId);
        if (!destinations.Keys.ToHashSet().SetEquals(result.Flights.Select(item => item.FlightId)) || result.Flights.Count != destinations.Count) return false;
        foreach (var flight in result.Flights)
            if (flight.GateId != destinations[flight.FlightId] || flight.ModeledCount != batches[flight.FlightId].ModeledCount
                || flight.RoutedCount+flight.ErrorCount != flight.ModeledCount
                || flight.SafeCount+flight.LateCount+flight.UnservedCount+flight.PendingCount+flight.ErrorCount != flight.ModeledCount
                || (assessed ? flight.PendingCount != 0 : flight.SafeCount+flight.LateCount+flight.UnservedCount != 0)) return false;
        var nodes = navigation.Graph.Nodes.ToDictionary(item => item.Id); var edges = navigation.Graph.Edges.ToDictionary(item => item.Id);
        var profiles = navigation.AccessProfiles.ToDictionary(item => item.GroupKind);
        var checkpoints = navigation.CheckpointBindings.ToDictionary(item => item.ResourceId,item => item.ZoneId);
        var gates = navigation.GateBindings.ToDictionary(item => item.ResourceId,item => item.ZoneId);
        var routes = result.Routes.ToDictionary(item => item.RouteKey);
        foreach (var route in result.Routes)
        {
            if (!flights.TryGetValue(route.FlightId,out var flight) || route.GateId != destinations[route.FlightId] || !(flight.Allows(route.CheckpointId) || timeline?.Events.Any(e => e.Type == "queue_redistribution" && e.TargetCheckpointId == route.CheckpointId) == true)
                || !profiles.TryGetValue(route.GroupKind,out var profile) || !batches[route.FlightId].Groups.Any(group => group.Kind == route.GroupKind)
                || route.EdgeIds.Count+1 != route.ZoneIds.Count || route.Waypoints.Count != route.ZoneIds.Count
                || !checkpoints.TryGetValue(route.CheckpointId,out var source) || !gates.TryGetValue(route.GateId,out var target)
                || route.ZoneIds[0] != source || route.ZoneIds[^1] != target) return false;
            var seconds = 0d; var distance = 0d;
            for (var index = 0; index < route.ZoneIds.Count; index++)
            {
                if (!nodes.TryGetValue(route.ZoneIds[index],out var node) || !node.IsActive || navigation.ClosedZoneIds.Contains(node.Id)
                    || profile.ForbiddenZoneIds.Contains(node.Id) || node.RequiredTags.Any(tag => !profile.AccessTags.Contains(tag))
                    || profile.RequireStepFree && (!node.IsStepFree || node.Kind == "Stairs")) return false;
                var waypoint = route.Waypoints[index];
                if (waypoint.ZoneId != node.Id || waypoint.X != node.X || waypoint.Y != node.Y || waypoint.Z != node.Z) return false;
                if (index == 0) continue;
                if (!edges.TryGetValue(route.EdgeIds[index-1],out var edge) || !edge.IsActive || navigation.ClosedEdgeIds.Contains(edge.Id)
                    || profile.RequireStepFree && !edge.IsStepFree || edge.RequiredTags.Any(tag => !profile.AccessTags.Contains(tag))
                    || !(edge.FromZoneId == route.ZoneIds[index-1] && edge.ToZoneId == node.Id
                         || edge.IsBidirectional && edge.ToZoneId == route.ZoneIds[index-1] && edge.FromZoneId == node.Id)) return false;
                seconds += edge.TravelSeconds; distance += edge.DistanceMeters;
            }
            if (Math.Abs(route.BaseWalkingMinutes-seconds/60)>1e-6 || Math.Abs(route.DistanceMeters-distance)>1e-6) return false;
        }
        foreach (var person in result.Journeys)
        {
            if (!flights.TryGetValue(person.FlightId,out var flight) || person.GateId != destinations[person.FlightId]
                || !(flight.Allows(person.CheckpointId) || actual?.GetValueOrDefault(person.Id) is { RedistributedAtMinute: not null } moved && moved.CheckpointId == person.CheckpointId) || person.BoardingCloseMinute != (timeline?.Close[person.FlightId] ?? flight.BoardingCloseMinute)
                || !person.Id.StartsWith($"demo-pax-{person.FlightId:N}-",StringComparison.Ordinal)
                || !int.TryParse(person.Id.AsSpan(person.Id.Length-6),out var number) || number<1 || number>batches[person.FlightId].ModeledCount) return false;
            if (person.Error is not null)
            {
                if (person.RouteKey is not null || person.WalkingMinutes is not null || person.GateArrivalMinute is not null
                    || person.QueueWaitMinutes is not null || person.BoardingRisk != "unreachable") return false;
                continue;
            }
            if (person.RouteKey is null || !routes.TryGetValue(person.RouteKey,out var route) || route.FlightId != person.FlightId
                || route.GateId != person.GateId || route.CheckpointId != person.CheckpointId || person.WalkingMinutes is null) return false;
            if (actual is not null && assessed)
            {
                if (!actual.TryGetValue(person.Id,out var process) || person.CheckpointId != process.CheckpointId && process.RedistributedAtMinute is not null) return false;
                if (process.GateArrivalMinute is { } reached
                        ? person.GateArrivalMinute != reached || person.QueueWaitMinutes != process.QueueWaitMinutes
                          || person.BoardingRisk != (reached <= person.BoardingCloseMinute ? "safe" : "late")
                        : person.GateArrivalMinute is not null || person.QueueWaitMinutes is not null || person.BoardingRisk != "unserved") return false;
                continue;
            }
            if (!assessed || person.BoardingRisk == "unserved")
            {
                if (person.QueueWaitMinutes is not null || person.GateArrivalMinute is not null || person.BoardingRisk != (assessed ? "unserved" : "pending")) return false;
            }
            else
            {
                if (person.QueueWaitMinutes is not { } wait || person.GateArrivalMinute is not { } arrival
                    || Math.Abs(arrival-person.ArrivalMinute-wait-person.ServiceMinutes-person.WalkingMinutes.Value)>1e-6
                    || person.BoardingRisk != (arrival<=person.BoardingCloseMinute ? "safe" : "late")) return false;
            }
        }
        return true;
    }
}

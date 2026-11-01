"""Control balancing: each arrival is assigned to the control where it will have the least total time.

Passengers are walked through in arrival order against an FIFO estimate of every control (staff on duty at that
minute, people still inside). Only the past and the staffing already in effect enter a decision, so the assignment of
an arrival never depends on later staffing steps and an observation up to minute T stays valid for the full run."""
from bisect import bisect_right, insort

from app.navigation.graph import NavigationGraph
from app.simulation.events import planned_at

MOVE_MINUTES = 2.0  # Extra walk to a control other than the assigned one.
MOVE_GAIN_MINUTES = 2.0  # A move must save at least this much, so near-equal controls do not swap passengers.


class Walks:
    """Walking minutes from any control to a passenger's gate, with the sampled access, travel and route profile."""

    def __init__(self, navigation, samples, flights):
        self.graph = NavigationGraph(navigation)
        self.samples, self.flights = samples, flights
        self.checkpoints = {item['resourceId']: item['zoneId'] for item in navigation['checkpointBindings']}
        self.gates = {item['resourceId']: item['zoneId'] for item in navigation['gateBindings']}
        self.destinations = {item['flightId']: item['gateId'] for item in navigation['flightGates']}

    def minutes(self, person, checkpoint):
        sampled = self.samples.get(person['id'])
        source = self.checkpoints.get(checkpoint)
        target = self.gates.get(self.destinations.get(person['flightId']))
        if sampled is None or source is None or target is None:
            return None
        route = self.graph.route(source, target, sampled['groupKind'])
        if route['error'] is not None:
            return None
        flight = self.flights[person['flightId']]
        walking = route['baseWalkingMinutes'] * sampled['travelMinutes'] / flight.travel_minutes(sampled['routeId']) \
            * self.graph.profiles[sampled['groupKind']]['walkingMultiplier']
        return round(walking, 12) if walking <= 10080 else None


def lanes(flight, checkpoints):
    """Staffed controls routed to the gate of a spread flight, with their layout travel time; empty for fixed flights."""
    found = {str(route.checkpoint_id): route.route_travel_minutes for route in flight.checkpoint_routes or []
             if str(route.checkpoint_id) in checkpoints and checkpoints[str(route.checkpoint_id)].staff_count > 0}
    return found if len(found) > 1 else {}


def balance(people, pinned, checkpoints, flights, schedules, walks=None):
    """Reassign the people of spread flights (sorted by arrival) in place and return the ids moved.

    Everyone else, `pinned` ids and people with an error keep their control but still load it. A group moves together."""
    active = {key: [] for key in checkpoints}  # Sorted finish times of everyone assigned and not yet done.
    moved, groups = set(), {}

    def start_at(key, arrival):
        capacity = planned_at(checkpoints[key], schedules.get(key), arrival)
        if capacity <= 0:
            return None
        finishes = active[key]
        return arrival if len(finishes) < capacity else finishes[len(finishes) - capacity]

    for person in people:
        arrival = person['arrivalMinute']
        for finishes in active.values():
            del finishes[:bisect_right(finishes, arrival)]
        own = person['checkpointId']
        options = lanes(flights[person['flightId']], checkpoints) if person['flightId'] in flights else {}
        if person['id'] in pinned or person['errorCode'] or own not in options:
            options = {own: None}
        group = (person['flightId'], person.get('groupId'))
        scale = {key: checkpoints[own].service_rate_per_minute / checkpoints[key].service_rate_per_minute for key in options}
        costs = {}
        for key in options:
            if group in groups and groups[group] != key:
                continue
            begin = start_at(key, arrival)
            if begin is None:
                continue
            walk = 0.0
            if key != own and walks is not None:
                walk = walks.minutes(person, key)
                if walk is None:
                    continue
            elif key != own:
                walk = person['walkingMinutes'] * options[key] / options[own] - person['walkingMinutes']
            costs[key] = (begin - arrival + walk + (0.0 if key == own else MOVE_MINUTES), begin)
        chosen = own
        if group in groups and groups[group] in costs:
            chosen = groups[group]
        elif own in costs and len(costs) > 1:
            best = min(costs, key=lambda key: (costs[key][0], key))
            if costs[own][0] - costs[best][0] >= MOVE_GAIN_MINUTES:
                chosen = best
        if len(options) > 1 and person.get('groupId') is not None:
            groups.setdefault(group, chosen)
        start = costs[chosen][1] if chosen in costs else (start_at(chosen, arrival) or arrival)
        service = person['serviceMinutes'] * scale.get(chosen, 1.0)
        insort(active[chosen], start + service)
        if chosen != own:
            moved.add(person['id'])
            person['checkpointId'], person['serviceMinutes'] = chosen, service
            person['walkingMinutes'] = walks.minutes(person, chosen) if walks is not None else person['walkingMinutes'] * options[chosen] / options[own]
    return moved

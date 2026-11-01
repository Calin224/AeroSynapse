"""Rerouting during the run: every few minutes the arrivals of the next window are sent to the control where they
will have the least total time (queue wait now, plus the Dijkstra walk from that control to their gate).

Only controls with an operator on duty are candidates."""
from app.navigation.graph import NavigationGraph

INTERVAL_MINUTES = 5
MOVE_MINUTES = 2.0  # Extra effort to use a control other than the assigned one.
MOVE_GAIN_MINUTES = 2.0  # A move must save at least this much, so near-equal controls do not swap passengers.


class Walks:
    """Walking minutes from any control to a passenger's gate, with the sampled access, travel and route profile."""

    def __init__(self, navigation, samples, flights):
        self.graph = NavigationGraph(navigation)
        self.samples, self.flights = samples, flights
        self.checkpoints = {item['resourceId']: item['zoneId'] for item in navigation['checkpointBindings']}
        self.gates = {item['resourceId']: item['zoneId'] for item in navigation['gateBindings']}
        self.destinations = {item['flightId']: item['gateId'] for item in navigation['flightGates']}

    def minutes(self, person, checkpoint, graph=None):
        """`graph` carries the closures in effect; the static graph is used without it."""
        sampled = self.samples.get(person['id'])
        source = self.checkpoints.get(checkpoint)
        target = self.gates.get(self.destinations.get(person['flightId']))
        if sampled is None or source is None or target is None:
            return None
        graph = graph or self.graph
        route = graph.route(source, target, sampled['groupKind'])
        if route['error'] is not None:
            return None
        flight = self.flights[person['flightId']]
        walking = route['baseWalkingMinutes'] * sampled['travelMinutes'] / flight.travel_minutes(sampled['routeId']) \
            * graph.profiles[sampled['groupKind']]['walkingMultiplier']
        return round(walking, 12) if walking <= 10080 else None


def decide(batch, now, state, lanes_of, groups, walks=None, graph=None):
    """Best control for each person arriving in the coming window, as {person id: control} for the ones that move.

    `state` maps a control to its live numbers: capacity (operators on duty), rate and ahead (queued + in service).
    People decided earlier in the call load their control for those after them; `groups` keeps a group together."""
    pending = {key: 0 for key in state}
    moves = {}
    for person in sorted(batch, key=lambda item: (item['arrivalMinute'], item['id'])):
        own = person['checkpointId']
        options = lanes_of(person)
        group = (person['flightId'], person.get('groupId'))
        costs = {}
        for key in options:
            load = state[key]
            if load['capacity'] <= 0:
                continue
            service = person['serviceMinutes'] * state[own]['rate'] / load['rate']
            if key == own:
                walk = person['walkingMinutes']
            else:
                walk = walks.minutes(person, key, graph) if walks is not None else person['walkingMinutes'] * options[key] / options[own]
                if walk is None:
                    continue
            # What is still queued when they arrive, then the wait for a free operator.
            ahead = max(0.0, load['ahead'] + pending[key] - (person['arrivalMinute'] - now) * load['capacity'] / service)
            wait = max(0.0, ahead - load['capacity'] + 1) * service / load['capacity']
            costs[key] = wait + walk + (0.0 if key == own else MOVE_MINUTES)
        chosen = own
        if group in groups and groups[group] in costs:
            chosen = groups[group]
        elif costs:
            best = min(costs, key=lambda key: (costs[key], key))
            if own not in costs or costs[own] - costs[best] >= MOVE_GAIN_MINUTES:
                chosen = best
        if person.get('groupId') is not None:
            groups.setdefault(group, chosen)
        pending[chosen] += 1
        if chosen != own:
            moves[person['id']] = chosen
    return moves


def lanes(flight, checkpoints):
    """Staffed controls routed to the gate of a spread flight, with their layout travel time; empty for fixed flights."""
    found = {str(route.checkpoint_id): route.route_travel_minutes for route in flight.checkpoint_routes or []
             if str(route.checkpoint_id) in checkpoints and checkpoints[str(route.checkpoint_id)].staff_count > 0}
    return found if len(found) > 1 else {}

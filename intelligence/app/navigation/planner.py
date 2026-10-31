"""Flight-linked route plans and boarding assessments, with explicit errors for every unroutable passenger."""
from app.navigation.graph import NavigationGraph, fingerprint


def plan_population(population, generation, navigation):
    graph = NavigationGraph(navigation)
    checkpoints = {binding['resourceId']: binding['zoneId'] for binding in navigation['checkpointBindings']}
    gates = {binding['resourceId']: binding['zoneId'] for binding in navigation['gateBindings']}
    destinations = {item['flightId']: item['gateId'] for item in navigation['flightGates']}
    flights = {str(flight.flight_id): flight for flight in generation.flights}
    routes, journeys = {}, []
    graph_hash = fingerprint(navigation)
    for passenger in population['passengers']:
        flight = flights[passenger['flightId']]
        gate = destinations[passenger['flightId']]
        source, target = checkpoints.get(passenger['checkpointId']), gates.get(gate)
        if source is None:
            route = {'error': {'code': 'checkpoint_zone_missing', 'message': 'The assigned checkpoint has no explicit zone binding.'}}
        elif target is None:
            route = {'error': {'code': 'gate_zone_missing', 'message': 'The assigned flight gate has no explicit zone binding.'}}
        else:
            route = graph.route(source, target, passenger['groupKind'])
        walking, route_key = None, None
        error = route['error']
        if error is None:
            factor = passenger['travelMinutes'] / flight.travel_minutes(passenger['routeId'])
            walking = route['baseWalkingMinutes'] * factor * graph.profiles[passenger['groupKind']]['walkingMultiplier']
            if walking > 10080:
                error = {'code': 'route_time_limit_exceeded', 'message': 'The sampled walking duration exceeds 10080 minutes.'}
                walking = None
            else:
                walking = round(walking, 12)
                route_key = 'nav-' + fingerprint({'graph': graph_hash, 'flight': passenger['flightId'], 'profile': passenger['groupKind'], 'edges': route['edgeIds']})[:24]
                if route_key not in routes:
                    routes[route_key] = {key: value for key, value in route.items() if key != 'error'} | {
                        'routeKey': route_key, 'flightId': passenger['flightId'], 'gateId': gate, 'checkpointId': passenger['checkpointId'],
                        'groupKind': passenger['groupKind'], 'passengerCount': 0}
                routes[route_key]['passengerCount'] += 1
        journeys.append({'id': passenger['id'], 'flightId': passenger['flightId'], 'gateId': gate, 'checkpointId': passenger['checkpointId'],
                         'routeKey': route_key, 'error': error, 'walkingMinutes': walking, 'queueWaitMinutes': None,
                         'serviceMinutes': passenger['serviceMinutes'], 'arrivalMinute': passenger['arrivalMinute'], 'gateArrivalMinute': None,
                         'boardingCloseMinute': flight.boarding_close_minute, 'boardingRisk': 'unreachable' if error else 'pending'})
    result = {'navigationVersion': '1.0', 'algorithm': 'dijkstra', 'graphFingerprint': graph_hash, 'populationFingerprint': population['fingerprint'],
              'isSynthetic': True, 'modeledCount': len(journeys), 'routedCount': sum(item['error'] is None for item in journeys),
              'errorCount': sum(item['error'] is not None for item in journeys), 'assessed': False,
              'routes': sorted(routes.values(), key=lambda route: route['routeKey']), 'flights': [], 'journeys': journeys,
              'offset': 0, 'returnedCount': len(journeys), 'hasMore': False}
    summarize(result, navigation)
    return result


def summarize(result, navigation):
    by_flight = {}
    for journey in result['journeys']:
        by_flight.setdefault(journey['flightId'], []).append(journey)
    result['flights'] = []
    for destination in sorted(navigation['flightGates'], key=lambda item: item['flightId']):
        people = by_flight.get(destination['flightId'], [])
        def mean(name):
            values = [person[name] for person in people if person[name] is not None]
            return round(sum(values) / len(values), 12) if values else None
        result['flights'].append({'flightId': destination['flightId'], 'gateId': destination['gateId'], 'modeledCount': len(people),
            'routedCount': sum(person['error'] is None for person in people), 'errorCount': sum(person['error'] is not None for person in people),
            'safeCount': sum(person['boardingRisk'] == 'safe' for person in people), 'lateCount': sum(person['boardingRisk'] == 'late' for person in people),
            'unservedCount': sum(person['boardingRisk'] == 'unserved' for person in people), 'pendingCount': sum(person['boardingRisk'] == 'pending' for person in people),
            'meanWalkingMinutes': mean('walkingMinutes'), 'meanQueueWaitMinutes': mean('queueWaitMinutes')})


def page(result, offset=0, limit=100):
    journeys = result['journeys'][offset:offset + limit]
    return result | {'journeys': journeys, 'offset': offset, 'returnedCount': len(journeys), 'hasMore': offset + len(journeys) < result['modeledCount']}

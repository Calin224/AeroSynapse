import copy
import json
from concurrent.futures import ThreadPoolExecutor

import pytest
from fastapi.testclient import TestClient

from app.contracts.models import NavigationInput, PassengerGenerationRequest, SimulationRequest
from app.contracts.validation import CONTRACTS, validate
from app.main import app
from app.navigation.graph import NavigationGraph
from app.navigation.planner import plan_population
from app.passengers.generator import generate_population
from app.simulation.engine import simulate

ROOT = CONTRACTS / 'navigation/v1/examples'


def payload(name='preview-request'):
    return json.loads((ROOT / (name + '.json')).read_text(encoding='utf-8'))


def plan(value=None):
    wire = value or payload()
    assert not validate('NavigationPreviewRequest', wire)
    generation = PassengerGenerationRequest.model_validate(wire['passengerGeneration'])
    return plan_population(generate_population(generation), generation, NavigationInput.model_validate(wire['navigation']).model_dump(mode='json', by_alias=True))


def test_shortest_time_routes_are_deterministic_and_keep_every_passenger_flight_and_gate():
    first = plan()
    assert first == plan()
    assert first['modeledCount'] == first['routedCount'] == 2 and first['errorCount'] == 0
    assert len(first['routes']) == 1 and first['routes'][0]['distanceMeters'] == 100
    assert first['routes'][0]['baseWalkingMinutes'] == 2 and first['routes'][0]['passengerCount'] == 2
    assert all(person['walkingMinutes'] == 2 and person['boardingRisk'] == 'pending' for person in first['journeys'])
    assert first['routes'][0]['waypoints'][0]['zoneId'] == payload()['navigation']['checkpointBindings'][0]['zoneId']
    assert first['routes'][0]['waypoints'][-1]['zoneId'] == first['journeys'][0]['gateId']
    assert not validate('NavigationResult', first)
    wire = payload()
    for name in ('nodes', 'edges'): wire['navigation']['graph'][name].reverse()
    wire['navigation']['accessProfiles'].reverse()
    assert plan(wire) == first
    with ThreadPoolExecutor(max_workers=4) as pool:
        assert all(result == first for result in pool.map(lambda _: plan(), range(8)))


def test_dijkstra_minimizes_time_instead_of_distance_and_handles_direction_and_cycles():
    value = payload(); navigation = value['navigation']
    navigation['graph']['edges'][0]['travelSeconds'] = 1000
    route = plan(value)['routes'][0]
    assert route['baseWalkingMinutes'] == 3 and route['distanceMeters'] == 140
    query = payload('route-query')
    graph = NavigationGraph(query['navigation'])
    assert graph.route(query['toZoneId'], query['fromZoneId'], 'solo')['error']['code'] == 'route_unreachable'
    for edge in query['navigation']['graph']['edges']: edge['isBidirectional'] = True
    assert NavigationGraph(query['navigation']).route(query['toZoneId'], query['fromZoneId'], 'solo')['error'] is None
    same = NavigationGraph(query['navigation']).route(query['fromZoneId'], query['fromZoneId'], 'solo')
    assert same['baseWalkingMinutes'] == 0 and len(same['waypoints']) == 1


def test_closing_a_corridor_replans_the_next_execution_without_reusing_a_stale_cache():
    before = plan(); wire = payload()
    closed = wire['navigation']['graph']['nodes'][1]['id']
    wire['navigation']['closedZoneIds'] = [closed]
    after = plan(wire)
    assert after['populationFingerprint'] == before['populationFingerprint']
    assert after['graphFingerprint'] != before['graphFingerprint']
    assert all(closed not in route['zoneIds'] and route['baseWalkingMinutes'] == 3 for route in after['routes'])
    assert before == plan()
    wire['navigation']['closedZoneIds'].append(wire['navigation']['graph']['nodes'][2]['id'])
    unavailable = plan(wire)
    assert unavailable['routedCount'] == 0 and unavailable['errorCount'] == 2
    assert len(unavailable['journeys']) == 2
    assert all(person['error']['code'] == 'route_unreachable' and person['boardingRisk'] == 'unreachable' for person in unavailable['journeys'])


@pytest.mark.parametrize('restriction', ['inactive-zone', 'inactive-edge', 'closed-edge', 'tags', 'forbidden-zone', 'step-free'])
def test_access_filters_never_traverse_prohibited_zones_or_edges(restriction):
    wire = payload(); nav = wire['navigation']; main = nav['graph']['nodes'][1]; edge = nav['graph']['edges'][0]
    if restriction == 'inactive-zone': main['isActive'] = False
    elif restriction == 'inactive-edge': edge['isActive'] = False
    elif restriction == 'closed-edge': nav['closedEdgeIds'] = [edge['id']]
    elif restriction == 'tags': main['requiredTags'] = ['staff']
    elif restriction == 'forbidden-zone': nav['accessProfiles'][0]['forbiddenZoneIds'] = [main['id']]
    else:
        nav['accessProfiles'][0]['requireStepFree'] = True
        nav['closedZoneIds'] = [main['id']]
    result = plan(wire)
    assert all(main['id'] not in route['zoneIds'] for route in result['routes'])
    if restriction == 'step-free': assert result['errorCount'] == 2
    else: assert result['routedCount'] == 2


def test_missing_bindings_or_profiles_produce_explicit_passenger_errors():
    for name, code in [('checkpointBindings','checkpoint_zone_missing'),('gateBindings','gate_zone_missing')]:
        wire = payload(); wire['navigation'][name] = []
        assert all(person['error']['code'] == code for person in plan(wire)['journeys'])
    wire = payload(); wire['navigation']['accessProfiles'] = wire['navigation']['accessProfiles'][1:]
    assert all(person['error']['code'] == 'access_profile_missing' for person in plan(wire)['journeys'])


def test_queue_service_and_walking_determine_boarding_risk_at_the_captured_closure():
    wire = payload('routed-simulation-request')
    result = simulate(SimulationRequest.model_validate(wire))
    assert result == simulate(SimulationRequest.model_validate(wire))
    navigation = result.navigation_result
    assert navigation['assessed']
    first, second = navigation['journeys']
    assert (first['queueWaitMinutes'], first['serviceMinutes'], first['walkingMinutes'], first['gateArrivalMinute'], first['boardingRisk']) == (0,1,2,3,'safe')
    assert (second['queueWaitMinutes'], second['gateArrivalMinute'], second['boardingRisk']) == (1,4,'late')
    assert navigation['flights'][0]['lateCount'] == 1
    wire['navigation']['closedZoneIds'] = [wire['navigation']['graph']['nodes'][1]['id']]
    closed = simulate(SimulationRequest.model_validate(wire))
    assert closed.navigation_result['flights'][0]['lateCount'] == 2
    assert not validate('RoutedSimulationResponse', result.model_dump(mode='json', by_alias=True))


def test_zero_staff_and_unreachable_passengers_have_explicit_risks_without_fabricated_gate_arrivals():
    wire = payload('routed-simulation-request')
    wire['checkpoints'][0]['staffCount'] = wire['passengerGeneration']['checkpoints'][0]['staffCount'] = 0
    result = simulate(SimulationRequest.model_validate(wire)).navigation_result
    assert result['flights'][0]['unservedCount'] == 2
    assert all(person['gateArrivalMinute'] is None and person['queueWaitMinutes'] is None for person in result['journeys'])
    wire['navigation']['closedZoneIds'] = [node['id'] for node in wire['navigation']['graph']['nodes'][1:3]]
    result = simulate(SimulationRequest.model_validate(wire))
    assert result.navigation_result['errorCount'] == 2 and result.metrics[0].processed_passengers == 0


@pytest.mark.parametrize('path,replacement', [
    (('navigation','graph','edges',0,'travelSeconds'),0),
    (('navigation','graph','edges',0,'toZoneId'),'60000000-0000-0000-0000-000000009999'),
    (('navigation','closedZoneIds'),['60000000-0000-0000-0000-000000009999']),
    (('navigation','accessProfiles',0,'walkingMultiplier'),0),
    (('navigation','accessProfiles',0,'accessTags'),['staff','staff']),
    (('navigation','flightGates'),[]),
])
def test_invalid_navigation_inputs_are_rejected_with_fields(path,replacement):
    wire = payload(); parent = wire
    for key in path[:-1]: parent = parent[key]
    parent[path[-1]] = replacement
    errors = validate('NavigationPreviewRequest',wire)
    assert errors
    with TestClient(app) as client:
        reply = client.post('/navigation/v1/preview',json=wire)
        assert reply.status_code == 422 and reply.json()['errors'] == errors


def test_http_preview_pagination_and_routed_simulation_use_the_same_logical_routes():
    expected = plan()
    with TestClient(app) as client:
        first = client.post('/navigation/v1/preview?offset=0&limit=1',json=payload()).json()
        second = client.post('/navigation/v1/preview?offset=1&limit=1',json=payload()).json()
        assert first['journeys'] + second['journeys'] == expected['journeys']
        assert first['routes'] == second['routes'] == expected['routes']
        assert first['hasMore'] and not second['hasMore']
        assert client.post('/navigation/v1/route',json=payload('route-query')).status_code == 200
        wire = payload('routed-simulation-request')
        result = client.post('/simulation/navigation-run',json=wire)
        assert result.status_code == 200 and result.json()['navigationResult']['flights'][0]['lateCount'] == 1
        assert client.post('/simulation/passenger-run',json=wire).status_code == 422


def test_walking_time_scales_by_the_route_of_the_passengers_own_lane():
    # A spread lane may be slower than the flight's default route; the sampled travel time already
    # includes that, so the graph walk must be scaled by the same lane, not the default route.
    wire = payload()
    flight = wire['passengerGeneration']['flights'][0]
    flight['checkpointRoutes'] = [{'checkpointId': flight['checkpointId'], 'routeId': flight['routeId'], 'routeTravelMinutes': 3}]
    wire['passengerGeneration']['batches'][0]['checkpointMode'] = 'spread'
    assert [person['walkingMinutes'] for person in plan(wire)['journeys']] == [person['walkingMinutes'] for person in plan()['journeys']]

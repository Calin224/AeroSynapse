import copy
import hashlib
import json
import subprocess
import sys
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from app.contracts.models import PassengerGenerationRequest, SimulationRequest
from app.contracts.validation import CONTRACTS, validate
from app.main import app
from app.passengers.generator import generate_population
from app.simulation.engine import simulate

EXAMPLES = CONTRACTS / 'passengers/v1/examples'

def payload():
    return json.loads((EXAMPLES / 'generation-request.json').read_text(encoding='utf-8'))

def generate(value=None):
    return generate_population(PassengerGenerationRequest.model_validate(value or payload()))

def test_identical_seed_config_and_fresh_process_produce_identical_logical_passengers():
    first = generate()
    assert first == generate()
    code = 'import json; from app.contracts.models import PassengerGenerationRequest; from app.passengers.generator import generate_population; print(json.dumps(generate_population(PassengerGenerationRequest.model_validate(json.load(__import__("sys").stdin)))))'
    other = subprocess.check_output([sys.executable, '-c', code], input=json.dumps(payload()).encode(), cwd=Path(__file__).resolve().parents[1])
    assert json.loads(other) == first
    assert generate(payload() | {'seed': 43})['fingerprint'] != first['fingerprint']
    assert first['fingerprint'] == hashlib.sha256(json.dumps({'generatorVersion': first['generatorVersion'], 'seed': first['seed'], 'passengers': first['passengers']}, sort_keys=True, separators=(',', ':'), allow_nan=False).encode()).hexdigest()

def test_generation_is_thread_safe_and_collection_order_and_extra_flights_do_not_change_existing_batches():
    first = generate()
    value = payload()
    for name in ('flights', 'batches', 'checkpoints'):
        value[name].reverse()
    for batch in value['batches']:
        batch['groups'].reverse()
    assert generate(value) == first
    with ThreadPoolExecutor(max_workers=4) as pool:
        assert all(result == first for result in pool.map(lambda _: generate(), range(8)))
    value = payload()
    value['flights'].pop(); value['batches'].pop()
    assert generate(value)['passengers'] == [person for person in first['passengers'] if person['flightId'] == value['flights'][0]['flightId']]
    # Staffing affects load estimates and queue capacity, never the generated logical arrivals/services.
    value = payload(); value['checkpoints'][0]['staffCount'] = 0
    assert generate(value)['fingerprint'] == first['fingerprint']

def test_each_passenger_has_a_flight_and_exact_counts_synthetic_ids_and_bounded_durations():
    result = generate()
    assert result['modeledCount'] == result['configuredCount'] == 180
    assert not result['hasSubset']
    assert sum(batch['modeledCount'] for batch in result['batches']) == len(result['passengers'])
    assert len({person['id'] for person in result['passengers']}) == 180
    flights = {flight['flightId']: flight for flight in payload()['flights']}
    for person in result['passengers']:
        assert person['isSynthetic'] and person['id'].startswith('demo-pax-') and person['groupId'].startswith('demo-group-')
        flight = flights[person['flightId']]
        assert person['routeId'] == flight['routeId'] and person['checkpointId'] == flight['checkpointId']
        assert 0 <= person['arrivalMinute'] <= flight['boardingCloseMinute']
        assert 0 < person['serviceMinutes'] <= 240 and 0 < person['travelMinutes'] <= 240
        assert set(person) == {'id', 'flightId', 'checkpointId', 'routeId', 'groupId', 'groupKind', 'arrivalMinute', 'serviceMinutes', 'travelMinutes', 'inSurge', 'isSynthetic'}
    groups = {}
    for person in result['passengers']:
        groups.setdefault(person['groupId'], []).append(person)
    profiles = {group['kind']: group for group in payload()['batches'][0]['groups']}
    for people in groups.values():
        profile = profiles[people[0]['groupKind']]
        assert profile['minSize'] <= len(people) <= profile['maxSize']
        assert len({person['arrivalMinute'] for person in people}) == 1
        assert len({person['travelMinutes'] for person in people}) == 1

def test_subset_and_cancelled_flight_are_reported_explicitly():
    value = payload(); value['batches'][0].update(countMode='subset', modeledCount=25)
    value['flights'][1]['isCancelled'] = True
    value['batches'][1].update(countMode='subset', modeledCount=0)
    result = generate(value)
    assert result['hasSubset'] and result['modeledCount'] == 25 and result['configuredCount'] == 180
    assert sum(batch['unmodeledCount'] for batch in result['batches']) == 155
    assert all(person['flightId'] == value['flights'][0]['flightId'] for person in result['passengers'])

@pytest.mark.parametrize('kind', ['constant', 'uniform', 'normal', 'exponential', 'triangular'])
def test_all_distribution_kinds_are_bounded_and_reproducible(kind):
    value = payload()
    for batch in value['batches']:
        for name in ('arrival', 'service', 'travel'):
            distribution = batch[name]; distribution['kind'] = kind
            if kind == 'constant':
                distribution['maximum'] = distribution['minimum']
            elif kind == 'normal':
                distribution['stdDev'] = (distribution['maximum'] - distribution['minimum']) / 6
    result = generate(value)
    assert result == generate(value)
    by_flight = {batch['flightId']: batch for batch in value['batches']}
    assert all(by_flight[person['flightId']]['arrival']['minimum'] <= person['arrivalMinute'] <= by_flight[person['flightId']]['arrival']['maximum'] for person in result['passengers'])

@pytest.mark.parametrize('path,replacement', [
    (('seed',), -1), (('seed',), True), (('isSynthetic',), False),
    (('batches', 0, 'modeledCount'), 101), (('batches', 0, 'countMode'), 'other'),
    (('batches', 0, 'arrival', 'maximum'), 60), (('batches', 0, 'arrival', 'stdDev'), 0),
    (('batches', 0, 'arrival', 'mean'), 100), (('batches', 0, 'service', 'minimum'), 0),
    (('batches', 0, 'travel', 'maximum'), 21), (('batches', 0, 'groups', 0, 'weight'), .1),
    (('batches', 0, 'groups', 1, 'maxSize'), 1), (('batches', 0, 'groups', 0, 'kind'), 'person-name'),
    (('flights', 0, 'boardingCloseMinute'), 100), (('generatorVersion',), '2.0'),
])
def test_invalid_parameters_return_fields_and_never_generate(path, replacement):
    value = payload(); parent = value
    for key in path[:-1]: parent = parent[key]
    parent[path[-1]] = replacement
    assert validate('PassengerGenerationRequest', value)
    with pytest.raises(ValueError): PassengerGenerationRequest.model_validate(value)
    with TestClient(app) as client:
        result = client.post('/passengers/v1/generate', json=value)
        assert result.status_code == 422 and result.json()['errors']


def test_profile_multiplier_at_the_decimal_lower_bound_is_accepted_over_http():
    value = payload()
    for batch in value['batches']:
        for group in batch['groups']:
            group['serviceMultiplier'] = group['travelMultiplier'] = 0.1
    value['flow'] = {'segments': [{'kind': 'standard', 'share': 1, 'holdBaggage': False,
        'control': 'security', 'serviceMultiplier': 0.1, 'travelMultiplier': 0.1}], 'stages': [], 'closures': []}
    with TestClient(app) as client:
        response = client.post('/passengers/v1/generate?limit=1', json=value)
        assert response.status_code == 200, response.text
        assert response.json()['modeledCount'] == 180


def test_duplicate_missing_flight_and_operational_limits_are_rejected():
    value = payload(); value['batches'][1] = copy.deepcopy(value['batches'][0])
    assert any(item['code'] == 'duplicate_id' for item in validate('PassengerGenerationRequest', value))
    value = payload(); value['batches'].pop()
    assert validate('PassengerGenerationRequest', value)
    value = payload(); value['checkpoints'][0]['serviceRatePerMinute'] = .000001
    assert validate('PassengerGenerationRequest', value)
    value = payload()
    for flight, batch in zip(value['flights'], value['batches']):
        flight['passengerCapacity'] = 100000; batch['modeledCount'] = 100000
    assert any(item['field'] == '/batches' and item['code'] == 'out_of_range' for item in validate('PassengerGenerationRequest', value))

def test_normal_and_overload_profiles_preserve_counts_and_expose_temporary_demand_peaks():
    normal = payload()
    for batch in normal['batches']:
        batch.update(countMode='subset', modeledCount=20)
        batch['service'].update(kind='constant', minimum=.1, maximum=.1)
    normal_result = generate(normal)
    assert not any(item['overloaded'] for item in normal_result['checkpointDemand'])
    overloaded = json.loads((EXAMPLES / 'generation-overload.json').read_text())
    result = generate(overloaded)
    assert result['modeledCount'] == 180
    assert all(person['inSurge'] and 20 <= person['arrivalMinute'] <= 20.2 for person in result['passengers'])
    assert all(item['overloaded'] for item in result['checkpointDemand'])

def test_http_pagination_is_explicit_and_full_export_reconstructs_the_same_logical_list():
    expected = generate()
    with TestClient(app) as client:
        first = client.post('/passengers/v1/generate?offset=0&limit=17', json=payload()).json()
        second = client.post('/passengers/v1/generate?offset=17&limit=163', json=payload()).json()
        assert first['passengers'] + second['passengers'] == expected['passengers']
        assert first['fingerprint'] == second['fingerprint'] == expected['fingerprint']
        assert first['returnedCount'] == 17 and first['hasMore'] and not second['hasMore']
        assert first['modeledCount'] == 180 and not first['hasSubset']
        assert client.post('/passengers/v1/generate?limit=100001', json=payload()).status_code == 422

def test_queue_engine_consumes_generated_arrivals_and_service_durations_for_the_assigned_checkpoint():
    wire = json.loads((EXAMPLES / 'profiled-simulation-request.json').read_text())
    generation = wire['passengerGeneration']
    generation['flights'] = generation['flights'][:1]; generation['batches'] = generation['batches'][:1]
    generation['durationMinutes'] = wire['durationMinutes'] = 2
    generation['flights'][0].update(passengerCapacity=3, boardingCloseMinute=2)
    batch = generation['batches'][0]; batch['modeledCount'] = wire['passengerCount'] = 3
    batch['arrival'].update(kind='constant', minimum=0, maximum=0)
    batch['service'].update(kind='constant', minimum=1, maximum=1)
    batch['groups'] = [dict(kind='solo', weight=1, minSize=1, maxSize=1, serviceMultiplier=1, travelMultiplier=1)]
    wire['checkpoints'][0].update(serviceRatePerMinute=1, staffCount=1)
    generation['checkpoints'] = copy.deepcopy(wire['checkpoints'])
    assert validate('ProfiledSimulationRequest', wire) == []
    request = SimulationRequest.model_validate(wire)
    first = simulate(request)
    assert first == simulate(request)
    assert first.metrics[0].processed_passengers == 2 and first.snapshots[0].queued_passengers == 1
    assert first.metrics[1].processed_passengers == 0 and first.snapshots[1].queued_passengers == 0
    with TestClient(app) as client:
        result = client.post('/simulation/passenger-run', json=wire)
        assert result.status_code == 200 and result.json() == first.model_dump(mode='json', by_alias=True)
        # Published v1 remains strict and frozen; profiled execution has its own envelope.
        assert client.post('/simulation/v1/run', json=wire).status_code == 422


def test_tiny_positive_route_durations_do_not_round_to_zero():
    value = payload()
    for flight in value['flights']:
        flight['routeTravelMinutes'] = 1e-15
    result = generate(value)
    assert all(0 < person['travelMinutes'] < 1e-12 for person in result['passengers'])
    value['flights'][0]['routeTravelMinutes'] = 5e-324
    value['batches'][0]['travel'].update(kind='constant', minimum=.01, maximum=.01)
    assert any(item['field'] == '/batches/0/travel/minimum' for item in validate('PassengerGenerationRequest', value))


def test_surge_order_is_irrelevant_and_zero_subsets_return_complete_empty_summaries():
    value = payload()
    for batch in value['batches']:
        batch['surges'] = [dict(startsMinute=15, endsMinute=16, share=.4),
                           dict(startsMinute=20, endsMinute=21, share=.6)]
    first = generate(value)
    for batch in value['batches']:
        batch['surges'].reverse()
    assert generate(value) == first
    for batch in value['batches']:
        batch.update(countMode='subset', modeledCount=0)
    empty = generate(value)
    assert empty['hasSubset'] and empty['modeledCount'] == 0 and empty['passengers'] == []
    assert len(empty['batches']) == 2 and len(empty['checkpointDemand']) == 2
    assert all(batch['unmodeledCount'] == batch['configuredCount'] for batch in empty['batches'])
    assert all(not checkpoint['overloaded'] for checkpoint in empty['checkpointDemand'])


def spread():
    return json.loads((EXAMPLES / 'generation-spread.json').read_text(encoding='utf-8'))


def test_spread_flight_splits_groups_over_staffed_lanes_in_proportion_to_capacity():
    fixed, result = generate(), generate(spread())
    home, other = '60000000-0000-0000-0000-000000000101', '60000000-0000-0000-0000-000000000102'
    flight = spread()['flights'][0]['flightId']
    routes = {route['checkpointId']: route for route in spread()['flights'][0]['checkpointRoutes']}
    people = [person for person in result['passengers'] if person['flightId'] == flight]
    shares = {checkpoint: sum(person['checkpointId'] == checkpoint for person in people) / len(people) for checkpoint in routes}
    # Capacity is staff × rate: 2 × 7 at the home checkpoint, 2 × 8 at the other.
    assert shares[home] == pytest.approx(14 / 30, abs=.03) and shares[other] == pytest.approx(16 / 30, abs=.03)
    for person in people:
        route = routes[person['checkpointId']]
        assert person['routeId'] == route['routeId']
        assert .8 * route['routeTravelMinutes'] * .9 - 1e-9 <= person['travelMinutes'] <= 1.3 * route['routeTravelMinutes'] * 1.15 + 1e-9
    groups = {}
    for person in people:
        groups.setdefault(person['groupId'], set()).add(person['checkpointId'])
    assert all(len(lanes) == 1 for lanes in groups.values())
    # The lane changes no draw: arrivals and groups match the fixed batch, and the other flight is untouched.
    lane_free = lambda person: (person['id'], person['groupId'], person['arrivalMinute'], person['inSurge'])
    assert sorted(map(lane_free, people)) == sorted(lane_free(person) for person in fixed['passengers'] if person['flightId'] == flight)
    assert [person for person in result['passengers'] if person['flightId'] != flight] == \
        [person for person in fixed['passengers'] if person['flightId'] != flight]
    demand = {item['checkpointId']: item['modeledCount'] for item in result['checkpointDemand']}
    assert demand[home] + demand[other] == 180 and demand[other] > 80


def test_spread_skips_closed_lanes_ignores_route_order_and_falls_back_to_the_flight_route():
    first = generate(spread())
    value = spread(); value['flights'][0]['checkpointRoutes'].reverse()
    assert generate(value) == first
    value = spread(); value['checkpoints'][1]['staffCount'] = 0
    flight = value['flights'][0]['flightId']
    assert {person['checkpointId'] for person in generate(value)['passengers'] if person['flightId'] == flight} == {
        '60000000-0000-0000-0000-000000000101'}
    value = spread()
    for checkpoint in value['checkpoints']:
        checkpoint['staffCount'] = 0
    closed = [person for person in generate(value)['passengers'] if person['flightId'] == flight]
    assert {(person['checkpointId'], person['routeId']) for person in closed} == {
        ('60000000-0000-0000-0000-000000000101', '60000000-0000-0000-0000-000000000400')}


def test_event_surge_passengers_stay_at_the_event_checkpoint_of_a_spread_flight():
    from app.simulation.processes import surge
    request = PassengerGenerationRequest.model_validate(spread())
    flight = request.flights[0]
    checkpoint = '60000000-0000-0000-0000-000000000102'
    wire = type('Wire', (), {'seed': 42, 'passenger_generation': request})()
    stations = {str(item.checkpoint_id): {'input': item} for item in request.checkpoints}
    event = {'eventId': '80000000-0000-0000-0000-000000000001', 'passengerCount': 12, 'windowMinutes': 4, 'minute': 10,
             'checkpointId': checkpoint, 'flightId': str(flight.flight_id)}
    people = surge(wire, event, stations, {str(flight.flight_id): flight}, {}, {})
    assert len(people) == 12 and {person['checkpointId'] for person in people} == {checkpoint}


def test_staffing_moves_spread_groups_between_lanes_but_never_changes_arrivals_or_groups():
    first = generate(spread())
    value = spread(); value['checkpoints'][0]['staffCount'] = 6
    second = generate(value)
    pairing = lambda result: sorted((person['id'], person['groupId'], person['arrivalMinute']) for person in result['passengers'])
    assert pairing(first) == pairing(second)
    lanes = lambda result: [person['checkpointId'] for person in sorted(result['passengers'], key=lambda person: person['id'])]
    assert lanes(first) != lanes(second)


def test_quarter_hour_demand_regroups_the_same_arrivals_as_the_peak_minute():
    value = spread()
    result = generate(value)
    quarters = -(-value['durationMinutes'] // 15)
    by_checkpoint = {}
    for person in result['passengers']:
        by_checkpoint.setdefault(person['checkpointId'], []).append(person)
    for item in result['checkpointDemand']:
        counts = item['arrivalsPerQuarterHour']
        assert len(counts) == quarters and sum(counts) == item['modeledCount']
        assert item['peakArrivalsPerMinute'] <= max(counts, default=0)
        security = [person for person in by_checkpoint.get(item['checkpointId'], [])
                    if person.get('stages', [{'stage': 'security'}])[-1]['stage'] == 'security']
        for index, count in enumerate(counts):
            assert count == sum(index * 15 <= min(person['arrivalMinute'], value['durationMinutes'] - 1e-9) < index * 15 + 15
                                for person in security)

import copy
import json

import pytest
from fastapi.testclient import TestClient

from app.contracts.models import PassengerGenerationRequest, SimulationRequest
from app.contracts.validation import CONTRACTS, validate
from app.main import app
from app.passengers.generator import generate_population
from app.simulation.engine import simulate
from app.simulation.flows import report, simulate_journeys

EXAMPLES = CONTRACTS / 'passengers/v1/examples'
NEUTRAL = {'segments': [{'kind': 'standard', 'share': 1, 'holdBaggage': False, 'control': 'security',
                         'serviceMultiplier': 1, 'travelMultiplier': 1}], 'stages': [], 'closures': []}
BASE_FIELDS = ('id', 'flightId', 'checkpointId', 'routeId', 'groupId', 'groupKind', 'arrivalMinute', 'serviceMinutes', 'travelMinutes', 'inSurge', 'isSynthetic')


def flows():
    return json.loads((EXAMPLES / 'generation-flows.json').read_text(encoding='utf-8'))


def generate(value):
    return generate_population(PassengerGenerationRequest.model_validate(value))


def analyze(value):
    return report(PassengerGenerationRequest.model_validate(value))


def by_segment(result):
    return {item['segment']: item for item in result['segments']}


def by_stage(result):
    return {item['stage']: item for item in result['stages']}


def test_types_change_only_their_explicitly_configured_parameters():
    without = {key: child for key, child in flows().items() if key != 'flow'}
    base = generate(without)['passengers']
    neutral = generate(without | {'flow': NEUTRAL})['passengers']
    assert [{key: person[key] for key in BASE_FIELDS} for person in neutral] == base
    assert all(person['segment'] == 'standard' and person['transferMinutes'] == 0
               and person['stages'] == [{'stage': 'security', 'serviceMinutes': person['serviceMinutes']}] for person in neutral)
    first = generate(flows())['passengers']
    # Types are drawn per group from separate streams: arrivals and groups never move.
    assert [(person['id'], person['groupId'], person['arrivalMinute']) for person in first] == [
        (person['id'], person['groupId'], person['arrivalMinute']) for person in base]
    segments = {segment['kind']: segment for segment in flows()['flow']['segments']}
    for person in first:
        segment = segments[person['segment']]
        assert [visit['stage'] for visit in person['stages']] == ['bagDrop'] * segment['holdBaggage'] + [segment['control']]
        assert (person['transferMinutes'] > 0) == ('transferMinutes' in segment)
    # The type label is behaviour-free: swapping labels swaps only the reported segment.
    value = flows()
    segments = value['flow']['segments']
    segments[2]['kind'], segments[3]['kind'] = segments[3]['kind'], segments[2]['kind']
    swap = {'reducedMobility': 'transfer', 'transfer': 'reducedMobility'}
    assert generate(value)['passengers'] == [person | {'segment': swap.get(person['segment'], person['segment'])} for person in first]
    value = flows()
    value['flow']['segments'][2]['travelMultiplier'] = 3
    changed = generate(value)['passengers']
    for before, after in zip(first, changed):
        if before['segment'] == 'reducedMobility':
            assert after['travelMinutes'] == pytest.approx(before['travelMinutes'] * 1.5)
            assert {key: child for key, child in after.items() if key != 'travelMinutes'} == {key: child for key, child in before.items() if key != 'travelMinutes'}
        else:
            assert after == before


def test_base_security_to_gate_flow_matches_the_v1_engine_exactly():
    value = json.loads((EXAMPLES / 'profiled-simulation-request.json').read_text(encoding='utf-8'))
    routed = copy.deepcopy(value)
    routed['passengerGeneration']['flow'] = NEUTRAL
    frames, routed_frames = [], []
    assert simulate(SimulationRequest.model_validate(value), frames.append) == simulate(SimulationRequest.model_validate(routed), routed_frames.append)
    assert frames == routed_frames
    result = analyze(value['passengerGeneration'])
    assert [item['stage'] for item in result['stages']] == ['security']
    assert by_segment(result)['standard']['passengers'] == by_segment(result)['standard']['reachedGatePassengers'] == 180


def test_statistics_are_segmented_aggregates_without_identifiers():
    result = analyze(flows())
    assert result['isSynthetic'] and result['fingerprint'] == generate(flows())['fingerprint']
    assert [item['segment'] for item in result['segments']] == ['standard', 'holdBaggage', 'reducedMobility', 'transfer']
    assert sum(item['passengers'] for item in result['segments']) == result['modeledCount'] == 180
    assert [item['stage'] for item in result['stages']] == ['bagDrop', 'security', 'assistedSecurity', 'transferSecurity']
    assert sum(item['arrivedPassengers'] for item in result['stages'] if item['stage'] != 'bagDrop') == 180
    text = json.dumps(result)
    assert 'demo-pax' not in text and 'demo-group' not in text and 'Id"' not in text
    assert all(set(item) == {'segment', 'passengers', 'reachedGatePassengers', 'missedBoardingPassengers', 'meanJourneyMinutes',
                             'maxJourneyMinutes', 'meanQueueMinutes', 'meanClosureDelayMinutes'} for item in result['segments'])
    assert analyze(flows()) == result


def test_blocking_a_stage_produces_a_measurable_delay():
    open_result = analyze(flows())
    value = flows()
    value['flow']['closures'] = [{'stage': 'bagDrop', 'startsMinute': 10, 'endsMinute': 25}, {'stage': 'bagDrop', 'startsMinute': 20, 'endsMinute': 30}]
    blocked = analyze(value)
    assert by_stage(blocked)['bagDrop']['blockedMinutes'] == 20
    assert by_stage(blocked)['bagDrop']['meanWaitMinutes'] > by_stage(open_result)['bagDrop']['meanWaitMinutes']
    assert by_segment(blocked)['holdBaggage']['meanClosureDelayMinutes'] > 0
    assert by_segment(blocked)['holdBaggage']['meanJourneyMinutes'] > by_segment(open_result)['holdBaggage']['meanJourneyMinutes']
    # Transfer passengers never visit bag drop or the checkpoint queues it feeds.
    assert by_segment(blocked)['transfer'] == by_segment(open_result)['transfer']
    assert all(item['meanClosureDelayMinutes'] == 0 for item in open_result['segments'])
    journeys = simulate_journeys(PassengerGenerationRequest.model_validate(value), generate(value)['passengers'])
    starts = [visit['start'] for journey in journeys for visit in journey['visits'] if visit['stage'] == 'bagDrop']
    assert starts and not any(10 <= start < 30 for start in starts)


def test_a_blocked_checkpoint_raises_wait_in_the_durable_run_results():
    value = json.loads((EXAMPLES / 'profiled-simulation-request.json').read_text(encoding='utf-8'))
    value['passengerGeneration']['flow'] = copy.deepcopy(NEUTRAL)
    open_run = simulate(SimulationRequest.model_validate(value))
    value['passengerGeneration']['flow']['closures'] = [{'stage': 'security', 'startsMinute': 20, 'endsMinute': 30}]
    frames = []
    blocked = simulate(SimulationRequest.model_validate(value), frames.append)
    assert blocked.status == 'completed' and 1 <= len(frames) <= 60
    for before, after in zip(open_run.metrics, blocked.metrics):
        assert after.mean_wait_minutes > before.mean_wait_minutes
    during = next(frame for frame in frames if frame['simulationMinute'] == 26)
    assert sum(zone['queuedPassengers'] for zone in during['zones']) > 0


def test_a_stage_without_staff_strands_its_passengers_and_reports_missed_boarding():
    value = flows()
    value['flow']['stages'][0]['staffCount'] = 0
    result = analyze(value)
    bag_drop = by_stage(result)['bagDrop']
    assert bag_drop['processedPassengers'] == 0 and bag_drop['queuedPassengers'] == bag_drop['arrivedPassengers'] > 0
    for kind in ('holdBaggage', 'reducedMobility'):
        segment = by_segment(result)[kind]
        assert segment['reachedGatePassengers'] == 0 and segment['missedBoardingPassengers'] == segment['passengers']


def test_flow_rules_and_http_surface():
    value = flows()
    assert validate('PassengerGenerationRequest', value) == []
    value['flow']['stages'] = value['flow']['stages'][1:]
    value['flow']['closures'] = [{'stage': 'bagDrop', 'startsMinute': 5, 'endsMinute': 120}]
    value['flow']['segments'][0]['share'] = 0.5
    fields = {(item['field'], item['code']) for item in validate('PassengerGenerationRequest', value)}
    assert fields == {('/flow/segments', 'invalid_value'), ('/flow/segments/1/holdBaggage', 'invalid_value'),
                      ('/flow/segments/2/holdBaggage', 'invalid_value'), ('/flow/closures/0', 'invalid_value'),
                      ('/flow/closures/0/stage', 'invalid_value')}
    with TestClient(app) as client:
        reply = client.post('/passengers/v1/flows', json=flows())
        assert reply.status_code == 200 and reply.json() == analyze(flows())
        assert client.post('/passengers/v1/flows', json=value).status_code == 422
        assert client.post('/passengers/v1/flows', content='{}', headers={'content-type': 'text/plain'}).status_code == 415


@pytest.mark.parametrize('engine',['simpy-1.0','simpy-kpi-1.0'])
def test_captured_simpy_runs_with_a_flow_use_the_stage_engine(engine):
    value = json.loads((CONTRACTS / 'processes/v1/examples/simpy-request.json').read_text(encoding='utf-8'))
    value['engine']=engine
    if engine=='simpy-kpi-1.0': value['snapshotIntervalMinutes']=3
    value['passengerGeneration']['flow'] = copy.deepcopy(NEUTRAL)
    assert validate('KpiSimulationRequest' if engine=='simpy-kpi-1.0' else 'SimPySimulationRequest', value) == []
    result = simulate(SimulationRequest.model_validate(value))
    assert result.status == 'completed' and result.process_result is None and result.navigation_result is None and result.kpi_result is None
    assert [str(metric.zone_id) for metric in result.metrics] == [checkpoint['checkpointId'] for checkpoint in value['checkpoints']]

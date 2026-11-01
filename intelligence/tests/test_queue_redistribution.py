"""Queue transfers conserve passengers and act only after a recorded decision."""
import copy
import json

import pytest

from app.contracts.models import SimulationRequest
from app.contracts.validation import CONTRACTS, validate
from app.simulation.observation import observe
from tests.test_events import A, B, FLIGHT, base, event, people, run


def congested(navigation=True):
    value = base(navigation=navigation) | {'events': []}
    batch = value['passengerGeneration']['batches'][0]
    batch['arrival'] = {'kind': 'constant', 'minimum': 0, 'maximum': 0, 'mean': 0, 'stdDev': 0, 'mode': 0}
    return value


def transfer(minute=5, count=5, walking=3):
    return event('queue_redistribution', minute, 100, checkpointId=A,
                 targetCheckpointId=B, passengerCount=count, windowMinutes=walking)


def moved(response):
    return [person for person in people(response).values() if person.get('assignedCheckpointId') == A]


def test_redistribution_moves_only_waiting_people_without_changing_earlier_history():
    value = congested()
    baseline = run(value)
    changed = run(value | {'events': [transfer()]})
    old, new = people(baseline), people(changed)
    transferred = moved(changed)

    assert len(transferred) == 5
    assert set(old) == set(new)
    assert changed['processResult']['populationCount'] == baseline['processResult']['populationCount'] == 40
    for person in transferred:
        assert old[person['id']]['processingStartedMinute'] >= 5
        assert person['checkpointId'] == B
        assert person['arrivalMinute'] == old[person['id']]['arrivalMinute']
        assert person['processingStartedMinute'] >= 8
        assert person['queueWaitMinutes'] == person['processingStartedMinute'] - person['arrivalMinute']
    for identifier, person in old.items():
        if person['processingStartedMinute'] < 5:
            assert new[identifier] == person
    early = lambda response: [row for row in response['kpiResult']['snapshots'] if row['minute'] < 5]
    assert early(changed) == early(baseline)
    assert sum(cp['processedCount'] for cp in changed['processResult']['checkpoints']) == 40
    assert sum(person['queueWaitMinutes'] for person in new.values()) < sum(person['queueWaitMinutes'] for person in old.values())
    assert validate('KpiSimulationResponse', changed) == []


def test_a_newly_staffed_control_accepts_the_queue_only_after_opening_and_transfer():
    value = congested(navigation=False)
    value['staffSchedules'] = [{'checkpointId': B, 'steps': [
        {'startMinute': 0, 'staffCount': 0}, {'startMinute': 10, 'staffCount': 1}]}]
    value['events'] = [transfer(minute=10, count=8, walking=2)]

    response = run(value)
    transferred = moved(response)
    assert len(transferred) == 8
    assert all(person['checkpointId'] == B and person['processingStartedMinute'] >= 12 for person in transferred)
    for row in response['kpiResult']['snapshots']:
        if row['zoneId'] == B and row['minute'] < 10:
            assert row['effectiveCapacity'] == row['processedPassengers'] == row['processingPassengers'] == 0
    assert validate('KpiSimulationResponse', response) == []


@pytest.mark.parametrize('restriction', ['no-staff', 'unavailable', 'staff-reduction', 'unreachable'])
def test_redistribution_keeps_people_at_source_when_destination_cannot_serve_them(restriction):
    value = congested()
    if restriction == 'no-staff':
        for checkpoint in value['checkpoints'] + value['passengerGeneration']['checkpoints']:
            if checkpoint['checkpointId'] == B:
                checkpoint['staffCount'] = 0
        other_events = []
    elif restriction == 'staff-reduction':
        other_events = [event('staff_reduction', 4, 101, checkpointId=B, staffCount=0)]
    elif restriction == 'unavailable':
        other_events = [event('checkpoint_unavailable', 4, 101, checkpointId=B)]
    else:
        value['navigation']['graph']['edges'] = [edge for edge in value['navigation']['graph']['edges'] if edge['fromZoneId'] != B]
        other_events = []
    response = run(value | {'events': [*other_events, transfer()]})

    assert moved(response) == []
    assert all(person['checkpointId'] == A for person in people(response).values())
    assert sum(cp['processedCount'] for cp in response['processResult']['checkpoints']) == 40
    assert validate('KpiSimulationResponse', response) == []


def test_redistributed_people_walk_from_the_control_that_actually_served_them():
    value = congested()
    # B's only exit takes five minutes, then the shared corridor takes one minute.
    next(edge for edge in value['navigation']['graph']['edges'] if edge['fromZoneId'] == B)['travelSeconds'] = 300
    response = run(value | {'events': [transfer()]})

    assert moved(response)
    for person in moved(response):
        assert person['walkingMinutes'] == 6
        assert person['gateArrivalMinute'] == person['securityClearedMinute'] + 6
    assert validate('KpiSimulationResponse', response) == []


def test_redistribution_is_reproducible_and_observing_before_it_preserves_the_prefix():
    value = congested()
    changed = value | {'events': [transfer()]}
    assert observe(SimulationRequest.model_validate(value), 4) == observe(SimulationRequest.model_validate(changed), 4)
    assert run(changed) == run(changed)


def test_redistribution_never_moves_more_people_than_are_still_waiting():
    value = congested(navigation=False)
    baseline = people(run(value))
    waiting = [person for person in baseline.values() if person['processingStartedMinute'] >= 5]
    response = run(value | {'events': [transfer(count=100)]})

    assert len(moved(response)) == len(waiting)
    assert response['processResult']['populationCount'] == len(people(response)) == 40
    assert validate('KpiSimulationResponse', response) == []


@pytest.mark.parametrize('mutation,field,code', [
    ('missing-target', '/events/0/targetCheckpointId', 'required'),
    ('same-target', '/events/0/targetCheckpointId', 'invalid_value'),
    ('unknown-target', '/events/0/targetCheckpointId', 'invalid_value'),
    ('target-on-outage', '/events/0/targetCheckpointId', 'invalid_value'),
])
def test_redistribution_validates_target_and_rejects_target_on_other_events(mutation, field, code):
    item = transfer()
    if mutation == 'missing-target':
        item.pop('targetCheckpointId')
    elif mutation == 'same-target':
        item['targetCheckpointId'] = A
    elif mutation == 'unknown-target':
        item['targetCheckpointId'] = '60000000-0000-0000-0000-000000000999'
    else:
        item = event('checkpoint_unavailable', 5, 100, checkpointId=A, targetCheckpointId=B)
    errors = validate('KpiSimulationRequest', congested() | {'events': [item]})
    assert (field, code) in {(error['field'], error['code']) for error in errors}


def test_agent_observation_accepts_staff_and_transfer_limits_from_the_dotnet_client():
    from fastapi.testclient import TestClient
    from app.main import app
    response = TestClient(app).post('/simulation/v1/observe', json={
        'request': congested(), 'minute': 5, 'gateEventId': None,
        'limits': {'reserve': 1, 'transferMinutes': 2, 'frozen': []}})
    assert response.status_code == 200, response.text
    assert response.json()['minute'] == 5


C = '60000000-0000-0000-0000-000000000103'


def three_controls():
    """The airport's three controls: A takes the whole flight at minute 0, B and C are open and empty."""
    value = congested()
    third = copy.deepcopy(value['checkpoints'][0]) | {'checkpointId': C}
    value['checkpoints'].append(third)
    value['passengerGeneration']['checkpoints'].append(copy.deepcopy(third))
    nav = value['navigation']
    nav['graph']['nodes'].append(copy.deepcopy(next(node for node in nav['graph']['nodes'] if node['id'] == A)) | {'id': C, 'name': 'Control C'})
    nav['graph']['edges'].append(copy.deepcopy(nav['graph']['edges'][0]) | {'id': '60000000-0000-0000-0000-000000000806', 'fromZoneId': C})
    nav['checkpointBindings'].append({'resourceId': C, 'zoneId': C})
    return value


@pytest.mark.parametrize('closed', [[A], [A, B]])
def test_a_closed_control_sends_its_whole_queue_to_the_open_ones(closed):
    value = three_controls()
    waiting = {key for key, person in people(run(value)).items() if person['processingStartedMinute'] >= 5}
    value['events'] = [event('checkpoint_unavailable', 5, 200 + index, checkpointId=key) for index, key in enumerate(closed)]
    if closed == [A]:
        value['events'].append(event('resume', 10, 210, checkpointId=A))  # Reopening moves nobody back.
    response = run(value)
    everyone = people(response)
    still_open = {A, B, C} - set(closed)

    moved = {key for key, person in everyone.items() if person.get('redistributedAtMinute') == 5}
    assert waiting and moved == waiting
    for key in moved:
        person = everyone[key]
        assert person['assignedCheckpointId'] == A and person['checkpointId'] in still_open
        assert person['processingStartedMinute'] >= 5 + person['transferMinutes']
    per_control = [sum(everyone[key]['checkpointId'] == target for key in moved) for target in sorted(still_open)]
    assert max(per_control) - min(per_control) <= 1  # Shortest queue first spreads them evenly.
    # Nobody is lost: everyone who arrived is processed or still in a queue, summed over the controls.
    totals = response['processResult']['checkpoints']
    assert sum(cp['arrivedCount'] for cp in totals) == len(everyone) == 40
    assert sum(cp['processedCount'] + cp['processingCount'] + cp['queuedCount'] for cp in totals) == 40
    assert all(person['stage'] == 'at_gate' for person in everyone.values())
    assert run(value) == response
    assert validate('KpiSimulationResponse', response) == []


def spread_run(with_events):
    value = three_controls()
    flight = value['passengerGeneration']['flights'][0]
    flight['checkpointRoutes'] = [{'checkpointId': key, 'routeId': flight['routeId'], 'routeTravelMinutes': flight['routeTravelMinutes']}
                                  for key in sorted([A, B, C])]
    value['passengerGeneration']['batches'][0]['checkpointMode'] = 'spread'
    if not with_events: del value['events']
    return value


@pytest.mark.parametrize('with_events', [True, False])
def test_arrivals_are_rerouted_to_the_least_loaded_controls_during_the_run(with_events):
    value = spread_run(with_events)
    response = run(value)
    result = people(response)
    assert validate('KpiSimulationResponse', response) == []
    assert len({person['checkpointId'] for person in result.values()}) > 1
    assert all(person['checkpointId'] != person.get('assignedCheckpointId') for person in result.values())
    assert response['processResult']['atGateCount'] == 40
    assert run(spread_run(with_events)) == response


def test_a_control_closed_on_the_map_gets_no_passengers_of_spread_flights():
    value = three_controls()
    flight = value['passengerGeneration']['flights'][0]
    flight['checkpointRoutes'] = [{'checkpointId': key, 'routeId': flight['routeId'], 'routeTravelMinutes': flight['routeTravelMinutes']}
                                  for key in sorted([A, B, C])]
    value['passengerGeneration']['batches'][0]['checkpointMode'] = 'spread'
    value['navigation']['closedZoneIds'] = [A]
    response = run(value)

    assert {person['checkpointId'] for person in people(response).values()} == {B, C}
    assert response['processResult']['atGateCount'] == 40 and response['processResult']['unreachableCount'] == 0
    assert validate('KpiSimulationResponse', response) == []
    # The route preview of "Trasee" plans the same passengers: nobody is left without a route.
    from fastapi.testclient import TestClient
    from app.main import app
    preview = TestClient(app).post('/navigation/v1/preview', json={key: value[key] for key in ('passengerGeneration', 'navigation')})
    assert preview.status_code == 200, preview.text
    assert preview.json()['routedCount'] == 40 and preview.json()['errorCount'] == 0
    assert {person['checkpointId'] for person in preview.json()['journeys']} == {B, C}


def staggered(second):
    """A closes at 5 with 37 waiting; B closes at `second`; arrivals at the closed A are redirected on arrival."""
    value = three_controls()
    value['events'] = [event('checkpoint_unavailable', 5, 300, checkpointId=A), event('checkpoint_unavailable', second, 301, checkpointId=B),
                       event('passenger_surge', 6, 302, checkpointId=A, flightId=FLIGHT, passengerCount=10, windowMinutes=10)]
    return value


@pytest.mark.parametrize('second', [20, 6])  # B closes with a queue, or while A's people are still walking to it.
def test_staggered_closures_send_everyone_to_the_last_open_control(second):
    value = staggered(second)
    response = run(value)
    everyone = people(response).values()

    for person in everyone:
        closed = {A: 5, B: second}.get(person['checkpointId'])
        assert closed is None or person['processingStartedMinute'] < closed  # Served at A or B only before they closed.
        assert person['stage'] == 'at_gate'
    left_b = [person for person in everyone if any(hop['fromCheckpointId'] == B for hop in person.get('transfers', []))]
    assert left_b and all(person['checkpointId'] == C and person['assignedCheckpointId'] == A for person in left_b)
    if second == 20:
        assert any(len(person['transfers']) == 2 for person in left_b)  # Moved off A's queue, then off B's.
        assert any(person['transfers'][0]['fromCheckpointId'] == B for person in left_b)  # Redirected to B on arrival, then moved.
    else:
        assert all(person['transfers'][1]['minute'] == 7 for person in left_b)  # Reached the closed B: moved on at once.
    totals = response['processResult']['checkpoints']
    assert len(everyone) == response['processResult']['populationCount'] == 50
    assert sum(cp['arrivedCount'] for cp in totals) == sum(cp['processedCount'] for cp in totals) == 50
    assert run(value) == response
    assert validate('KpiSimulationResponse', response) == []


def reopened():
    """As staggered(20), but A reopens at 10: B's queue can go back to A."""
    value = staggered(20)
    value['events'].append(event('resume', 10, 303, checkpointId=A))
    return value


def test_a_reopened_control_can_take_back_people_it_sent_away():
    response = run(reopened())
    back = [person for person in people(response).values() if person.get('transfers') and person['checkpointId'] == person['assignedCheckpointId']]
    assert back and all([hop['toCheckpointId'] for hop in person['transfers']] == [B, A] for person in back)
    assert validate('KpiSimulationResponse', response) == []


@pytest.mark.parametrize('field,value', [('fromCheckpointId', C), ('minute', 6)])
def test_a_broken_chain_of_moves_is_rejected(field, value):
    response = run(reopened())
    person = next(person for person in response['processResult']['passengers'] if len(person.get('transfers', [])) == 2)
    person['transfers'][1][field] = value  # Leaving a control they never reached, or before their walk there ended.
    if field == 'minute': person['redistributedAtMinute'] = value  # Keep the last-move mirror consistent.
    assert validate('KpiSimulationResponse', response)


@pytest.mark.parametrize('name', ['closure', 'closure-staggered'])
def test_the_closure_examples_are_what_the_engine_returns(name):
    """EventTests.cs checks these pairs against the .NET correlation; they must stay the engine's own output."""
    root = CONTRACTS / 'events/v1/examples'
    request = json.loads((root / f'{name}-request.json').read_text(encoding='utf-8'))
    assert run(request) == json.loads((root / f'{name}-response.json').read_text(encoding='utf-8'))

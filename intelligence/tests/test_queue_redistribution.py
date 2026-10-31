"""Queue transfers conserve passengers and act only after a recorded decision."""
import pytest

from app.contracts.models import SimulationRequest
from app.contracts.validation import validate
from app.simulation.observation import observe
from tests.test_events import A, B, base, event, people, run


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

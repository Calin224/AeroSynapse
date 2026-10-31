"""Scheduled events: prefix invariance, redirect rule, resumption, ordering and explicit conflicts."""
import copy
import itertools
import json

import pytest
from fastapi.testclient import TestClient

from app.contracts.models import SimulationRequest
from app.contracts.validation import CONTRACTS, validate
from app.main import app
from app.simulation.processes import simulate

A = '60000000-0000-0000-0000-000000000101'
B = '60000000-0000-0000-0000-000000000102'
FLIGHT = '70000000-0000-0000-0000-000000000001'
MAIN = '60000000-0000-0000-0000-000000000701'
ALTERNATIVE = '60000000-0000-0000-0000-000000000702'
GATE = '60000000-0000-0000-0000-000000000201'


def base(navigation=True):
    """Two controls, 40 passengers arriving over the first hour, two corridors to one gate."""
    value = json.loads((CONTRACTS / 'kpis/v1/examples/kpi-request.json').read_text(encoding='utf-8'))
    value['durationMinutes'] = 120
    second = copy.deepcopy(value['checkpoints'][0]) | {'checkpointId': B}
    value['checkpoints'].append(second)
    generation = value['passengerGeneration']
    generation['durationMinutes'] = 120
    generation['checkpoints'].append(copy.deepcopy(second))
    generation['flights'][0] |= {'boardingCloseMinute': 100, 'passengerCapacity': 40}
    batch = generation['batches'][0]
    batch |= {'modeledCount': 40, 'countMode': 'full'}
    batch['arrival'] = {'kind': 'uniform', 'minimum': 0, 'maximum': 60, 'mean': 30, 'stdDev': 10, 'mode': 30}
    batch['service'] = {'kind': 'constant', 'minimum': 2, 'maximum': 2, 'mean': 2, 'stdDev': 0, 'mode': 2}
    value['passengerCount'] = 40
    value['arrivalRatePerMinute'] = 40 / 120
    if navigation:
        nav = value['navigation']
        security = copy.deepcopy(next(node for node in nav['graph']['nodes'] if node['id'] == A)) | {'id': B, 'name': 'Control B'}
        nav['graph']['nodes'].append(security)
        nav['graph']['edges'].append(copy.deepcopy(nav['graph']['edges'][0]) | {'id': '60000000-0000-0000-0000-000000000805', 'fromZoneId': B})
        nav['checkpointBindings'].append({'resourceId': B, 'zoneId': B})
    else:
        value.pop('navigation')
    return value


def event(kind, minute, identifier, **fields):
    return {'eventId': f'90000000-0000-0000-0000-{identifier:012d}', 'type': kind, 'minute': minute} | fields


def run(value):
    return simulate(SimulationRequest.model_validate(value)).model_dump(mode='json', by_alias=True)


def people(response):
    return {person['id']: person for person in response['processResult']['passengers']}


def test_an_event_at_minute_t_leaves_every_earlier_interval_unchanged():
    before = run(base())
    value = base()
    value['events'] = [event('checkpoint_unavailable', 30, 1, checkpointId=A), event('staff_reduction', 30, 2, checkpointId=B, staffCount=0)]
    after = run(value)
    old, new = people(before), people(after)
    for identifier, person in old.items():
        for key in ('processingStartedMinute', 'securityClearedMinute', 'gateArrivalMinute'):
            if person[key] is not None and person[key] < 30:
                assert new[identifier][key] == person[key], (identifier, key)
    early = lambda response: [{key: value for key, value in row.items() if key != 'effectiveCapacity'}
                              for row in response['kpiResult']['snapshots'] if row['minute'] < 30]
    assert early(after) == early(before)
    assert after['processResult']['events'] == [
        {'eventId': value['events'][0]['eventId'], 'type': 'checkpoint_unavailable', 'minute': 30, 'sequence': 1},
        {'eventId': value['events'][1]['eventId'], 'type': 'staff_reduction', 'minute': 30, 'sequence': 2}]


def test_an_unavailable_control_starts_no_service_and_redirects_only_new_arrivals():
    value = base()
    value['events'] = [event('checkpoint_unavailable', 20, 1, checkpointId=A)]
    response = run(value)
    for person in people(response).values():
        if person['checkpointId'] == A and person['processingStartedMinute'] is not None:
            assert person['processingStartedMinute'] < 20
        if person.get('assignedCheckpointId') == A:
            assert person['checkpointId'] == B and person['arrivalMinute'] >= 20
        if person['checkpointId'] == A and person['arrivalMinute'] < 20 and person['processingStartedMinute'] is None:
            assert person['stage'] == 'queued'  # Active requests keep their place in the closed queue.
    assert any(person.get('assignedCheckpointId') == A for person in people(response).values())


def test_staff_reductions_never_preempt_service_in_progress():
    value = base(navigation=False)
    for checkpoint in value['checkpoints'] + value['passengerGeneration']['checkpoints']:
        checkpoint['staffCount'] = 3
    # Reduce staffing at a minute when two passengers are mid-service at control A.
    baseline = [person for person in people(run(value)).values() if person['checkpointId'] == A and person['processingStartedMinute'] is not None]
    minute = next(m for m in range(1, 120) if sum(p['processingStartedMinute'] < m < p['securityClearedMinute'] for p in baseline) >= 2)
    value['events'] = [event('staff_reduction', minute, 1, checkpointId=A, staffCount=1)]
    response = run(value)
    served = [person for person in people(response).values() if person['checkpointId'] == A and person['processingStartedMinute'] is not None]
    in_progress = [person for person in served if person['processingStartedMinute'] < minute < person['securityClearedMinute']]
    assert len(in_progress) >= 2 and all(person['securityClearedMinute'] == person['processingStartedMinute'] + 2 for person in in_progress)
    for start in [person['processingStartedMinute'] for person in served if person['processingStartedMinute'] >= minute]:
        # Every grant after the reduction finds fewer busy slots than the one operator still on duty.
        assert sum(person['processingStartedMinute'] < start < person['securityClearedMinute'] for person in served) == 0
    row = next(row for row in response['kpiResult']['snapshots'] if row['zoneId'] == A and row['minute'] == minute)
    assert row['capacity'] == 3 and row['effectiveCapacity'] == 1


def test_resumption_serves_the_waiting_queue_and_processes_every_passenger_once():
    value = base(navigation=False)
    value['events'] = [event('checkpoint_unavailable', 10, 1, checkpointId=A), event('checkpoint_unavailable', 10, 2, checkpointId=B),
                       event('resume', 70, 3, checkpointId=A)]
    response = run(value)
    everyone = list(people(response).values())
    assert len({person['id'] for person in everyone}) == len(everyone) == 40
    for person in everyone:
        if person['processingStartedMinute'] is not None and person['processingStartedMinute'] >= 10:
            assert person['checkpointId'] == A and person['processingStartedMinute'] >= 70
    processed = sum(cp['processedCount'] for cp in response['processResult']['checkpoints'])
    assert processed == sum(person['securityClearedMinute'] is not None for person in everyone)
    resumed = run(value | {'events': [event('checkpoint_unavailable', 10, 1, checkpointId=A), event('resume', 40, 3, checkpointId=A)]})
    assert all(person['checkpointId'] == A for person in people(resumed).values() if person['arrivalMinute'] >= 40)


def test_simultaneous_events_have_one_order_whatever_their_input_order():
    value = base()
    events = [event('passenger_surge', 15, 3, checkpointId=A, passengerCount=10, windowMinutes=5),
              event('checkpoint_unavailable', 15, 2, checkpointId=A), event('resume', 15, 1, checkpointId=A),
              event('flight_change', 15, 4, flightId=FLIGHT, delayMinutes=10)]
    outputs = []
    for permutation in itertools.permutations(events):
        outputs.append(run(value | {'events': list(permutation)}))
    assert all(output == outputs[0] for output in outputs)
    assert [item['type'] for item in outputs[0]['processResult']['events']] == [
        'flight_change', 'checkpoint_unavailable', 'resume', 'passenger_surge']


def test_surge_adds_demand_only_from_its_minute_and_flight_changes_move_the_deadline():
    value = base(navigation=False)
    value['events'] = [event('passenger_surge', 30, 1, checkpointId=B, passengerCount=12, windowMinutes=6, flightId=FLIGHT),
                       event('flight_change', 40, 2, flightId=FLIGHT, delayMinutes=-30)]
    response = run(value)
    surge = [person for person in people(response).values() if person['id'].startswith('demo-surge-')]
    assert len(surge) == 12 and all(30 <= person['arrivalMinute'] < 36 for person in surge)
    assert all(person['boardingCloseMinute'] == 70 for person in people(response).values() if person['flightId'] == FLIGHT)
    assert response['processResult']['populationCount'] == 52
    rows = [row for row in response['kpiResult']['snapshots'] if row['zoneId'] == B]
    assert all(row['populationCount'] == rows[0]['populationCount'] for row in rows if row['minute'] < 30)
    assert next(row for row in rows if row['minute'] == 30)['populationCount'] == rows[0]['populationCount'] + 12


def test_corridor_closure_reroutes_new_walks_and_holds_passengers_without_a_route():
    value = base()
    value['events'] = [event('corridor_closure', 20, 1, zoneId=MAIN)]
    rerouted = people(run(value))
    planned = people(run(base()))
    for identifier, person in rerouted.items():
        if person['securityClearedMinute'] is not None and person['securityClearedMinute'] >= 20:
            assert person['walkingMinutes'] > planned[identifier]['walkingMinutes']  # The longer alternative.
    value['events'] = [event('corridor_closure', 20, 1, zoneId=MAIN), event('corridor_closure', 20, 2, zoneId=ALTERNATIVE),
                       event('resume', 80, 3, zoneId=ALTERNATIVE)]
    held = [person for person in people(run(value)).values() if 'routeHoldMinutes' in person]
    assert held and all(person['securityClearedMinute'] + person['routeHoldMinutes'] == 80 for person in held)
    value['events'] = value['events'][:2]
    waiting = [person for person in people(run(value)).values() if person.get('awaitingRoute')]
    assert waiting and all(person['stage'] == 'walking' and person['gateArrivalMinute'] is None for person in waiting)


@pytest.mark.parametrize('events,field', [
    ([event('resume', 10, 1, checkpointId=A)], '/events/0/checkpointId'),
    ([event('checkpoint_unavailable', 10, 1, checkpointId=A), event('checkpoint_unavailable', 20, 2, checkpointId=A)], '/events/1/checkpointId'),
    ([event('staff_reduction', 10, 1, checkpointId=A, staffCount=1)], '/events/0/staffCount'),
    ([event('resume', 10, 1, zoneId=MAIN)], '/events/0/zoneId'),
    ([event('corridor_closure', 10, 1, zoneId=MAIN), event('corridor_closure', 10, 2, zoneId=MAIN)], '/events/1/zoneId'),
    ([event('flight_change', 110, 1, flightId=FLIGHT, delayMinutes=5)], '/events/0/delayMinutes'),
    ([event('flight_change', 50, 1, flightId=FLIGHT, delayMinutes=-60)], '/events/0/delayMinutes'),
])
def test_contradictory_timelines_are_rejected_with_an_explicit_conflict(events, field):
    assert validate('KpiSimulationRequest', base() | {'events': events}) == [
        {'field': field, 'code': 'event_conflict', 'message': 'The event contradicts the scheduled timeline.'}]


@pytest.mark.parametrize('events,field,code', [
    ([event('staff_reduction', 10, 1, checkpointId=A)], '/events/0/staffCount', 'required'),
    ([event('checkpoint_unavailable', 10, 1, checkpointId=A, zoneId=MAIN)], '/events/0/zoneId', 'invalid_value'),
    ([event('resume', 10, 1)], '/events/0', 'invalid_value'),
    ([event('checkpoint_unavailable', 120, 1, checkpointId=A)], '/events/0/minute', 'out_of_range'),
    ([event('passenger_surge', 118, 1, checkpointId=A, passengerCount=5, windowMinutes=5)], '/events/0/windowMinutes', 'out_of_range'),
    ([event('checkpoint_unavailable', 10, 1, checkpointId='60000000-0000-0000-0000-000000000999')], '/events/0/checkpointId', 'invalid_value'),
    ([event('flight_change', 10, 1, flightId=FLIGHT, delayMinutes=0)], '/events/0/delayMinutes', 'out_of_range'),
])
def test_impossible_events_return_field_errors(events, field, code):
    assert {(item['field'], item['code']) for item in validate('KpiSimulationRequest', base() | {'events': events})} == {(field, code)}


def test_events_require_flight_profiles_and_the_kpi_engine():
    value = base()
    value.pop('navigation')
    value.pop('passengerGeneration')
    value['events'] = [event('checkpoint_unavailable', 10, 1, checkpointId=A)]
    assert validate('KpiSimulationRequest', value) == [
        {'field': '/passengerGeneration', 'code': 'required', 'message': 'Field is required.'}]
    corridor = base(navigation=False) | {'events': [event('corridor_closure', 10, 1, zoneId=MAIN)]}
    assert ('/events/0/zoneId', 'invalid_value') in {(item['field'], item['code']) for item in validate('KpiSimulationRequest', corridor)}


def test_event_runs_are_reproducible_and_valid_over_http():
    value = base()
    value['events'] = [event('passenger_surge', 5, 1, checkpointId=A, passengerCount=20, windowMinutes=10),
                       event('checkpoint_unavailable', 12, 2, checkpointId=A), event('corridor_closure', 25, 3, zoneId=MAIN),
                       event('resume', 50, 4, checkpointId=A), event('resume', 60, 5, zoneId=MAIN)]
    first, second = run(value), run(value)
    assert first == second
    assert validate('KpiSimulationResponse', first) == []
    with TestClient(app) as client:
        response = client.post('/simulation/kpi-run', json=value)
    assert response.status_code == 200 and response.json() == first


def test_empty_schedule_preserves_the_baseline_and_has_a_valid_report():
    baseline = run(base())
    response = run(base() | {'events': []})
    assert response['processResult'].pop('events') == []
    assert response['kpiResult'].pop('eventCount') == 0
    for row in response['kpiResult']['snapshots']:
        assert row.pop('effectiveCapacity') == row['capacity']
    assert response == baseline


def test_closure_at_a_completion_boundary_keeps_service_and_holds_the_next_grant():
    value = base(navigation=False)
    value['passengerGeneration']['batches'][0]['arrival'] = {
        'kind': 'constant', 'minimum': 0, 'maximum': 0, 'mean': 0, 'stdDev': 0, 'mode': 0}
    value['events'] = [event('checkpoint_unavailable', 2, 1, checkpointId=A),
                       event('resume', 5, 2, checkpointId=A)]
    everyone = list(people(run(value)).values())
    assert everyone[0]['processingStartedMinute'] == 0
    assert everyone[0]['securityClearedMinute'] == 2
    assert everyone[1]['processingStartedMinute'] == 5
    assert all(person['processingStartedMinute'] != 2 for person in everyone)


@pytest.mark.parametrize('minute', [0, 20])
def test_closure_precedes_arrivals_at_the_same_minute(minute):
    value = base(navigation=False)
    value['passengerGeneration']['batches'][0]['arrival'] = {
        'kind': 'constant', 'minimum': minute, 'maximum': minute, 'mean': minute, 'stdDev': 0, 'mode': minute}
    value['events'] = [event('checkpoint_unavailable', minute, 1, checkpointId=A)]
    assert all(person['checkpointId'] == B and person['assignedCheckpointId'] == A
               for person in people(run(value)).values())


def test_flight_surge_obeys_corridor_closures_and_resumes_its_walk_once():
    value = base()
    graph = value['navigation']['graph']
    alternative = next(edge for edge in graph['edges'] if edge['fromZoneId'] == A and edge['toZoneId'] == ALTERNATIVE)
    graph['edges'].append(alternative | {'id': '60000000-0000-0000-0000-000000000806', 'fromZoneId': B})
    value['events'] = [event('passenger_surge', 30, 1, checkpointId=B, flightId=FLIGHT,
                             passengerCount=3, windowMinutes=1),
                       event('corridor_closure', 20, 2, zoneId=MAIN),
                       event('corridor_closure', 20, 3, zoneId=ALTERNATIVE),
                       event('resume', 80, 4, zoneId=ALTERNATIVE)]
    response = run(value)
    extra = [person for person in people(response).values() if person['id'].startswith('demo-surge-')]
    assert len(extra) == 3
    assert all(person['securityClearedMinute'] + person['routeHoldMinutes'] == pytest.approx(80) for person in extra)
    assert all(person['gateArrivalMinute'] == 80 + person['walkingMinutes'] for person in extra)
    value['events'] = value['events'][:-1]
    extra = [person for person in people(run(value)).values() if person['id'].startswith('demo-surge-')]
    assert all(person['awaitingRoute'] and person['gateArrivalMinute'] is None for person in extra)


def test_duplicate_event_identifiers_are_rejected_before_the_scheduler():
    events = [event('passenger_surge', minute, 1, checkpointId=A, passengerCount=3, windowMinutes=1)
              for minute in (10, 20)]
    assert {(item['field'], item['code']) for item in validate('KpiSimulationRequest', base() | {'events': events})} == {
        ('/events/1/eventId', 'duplicate_id')}

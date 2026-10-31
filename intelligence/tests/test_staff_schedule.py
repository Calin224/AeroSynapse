"""Staff schedules: people on duty change over the day, without preempting service or moving earlier minutes."""
import pytest
from fastapi.testclient import TestClient

from app.contracts.validation import validate
from app.main import app
from tests.test_events import A, B, base, event, people, run


def scheduled(steps, checkpoint=A, navigation=False):
    """Two controls; `checkpoint` follows `steps`, and its staffCount is the schedule's maximum."""
    value = base(navigation=navigation) | {'events': []}
    for item in value['checkpoints'] + value['passengerGeneration']['checkpoints']:
        if item['checkpointId'] == checkpoint:
            item['staffCount'] = max(count for _, count in steps)
    value['staffSchedules'] = [{'checkpointId': checkpoint,
                                'steps': [{'startMinute': minute, 'staffCount': count} for minute, count in steps]}]
    return value


def at(response, zone, minute):
    return next(row for row in response['kpiResult']['snapshots'] if row['zoneId'] == zone and row['minute'] == minute)


def test_a_constant_schedule_runs_exactly_like_fixed_staffing():
    fixed = base(navigation=False) | {'events': []}
    for item in fixed['checkpoints'] + fixed['passengerGeneration']['checkpoints']:
        if item['checkpointId'] == A:
            item['staffCount'] = 2
    assert run(scheduled([(0, 2)])) == run(fixed)


def test_more_people_later_serve_faster_only_from_their_minute():
    lean = run(scheduled([(0, 1)]))
    reinforced = run(scheduled([(0, 1), (30, 3)]))
    before = lambda response: {key: person for key, person in people(response).items()
                               if person['processingStartedMinute'] is not None and person['processingStartedMinute'] < 30}
    # Every interval before the step is unchanged.
    assert before(reinforced) == before(lean)
    wait = lambda response: sum(person['queueWaitMinutes'] or 0 for person in people(response).values() if person['checkpointId'] == A)
    assert wait(reinforced) < wait(lean)
    assert at(reinforced, A, 20)['effectiveCapacity'] == 1 and at(reinforced, A, 40)['effectiveCapacity'] == 3
    assert at(reinforced, A, 40)['capacity'] == 3


def test_a_smaller_step_never_preempts_service_in_progress():
    value = scheduled([(0, 3), (20, 1)])
    response = run(value)
    served = [person for person in people(response).values() if person['checkpointId'] == A and person['processingStartedMinute'] is not None]
    for person in served:
        assert person['securityClearedMinute'] == person['processingStartedMinute'] + 2
    for start in [person['processingStartedMinute'] for person in served if person['processingStartedMinute'] >= 20]:
        # After the step, a grant finds nobody else in service at the single remaining operator.
        assert sum(person['processingStartedMinute'] < start < person['securityClearedMinute'] for person in served) == 0
    assert validate('KpiSimulationResponse', response) == []


def test_an_outage_ends_on_the_staffing_scheduled_at_that_minute():
    value = scheduled([(0, 1), (30, 3)])
    value['events'] = [event('checkpoint_unavailable', 10, 1, checkpointId=A), event('resume', 50, 2, checkpointId=A)]
    response = run(value)
    assert at(response, A, 40)['effectiveCapacity'] == 0 and at(response, A, 60)['effectiveCapacity'] == 3


@pytest.mark.parametrize('events,field', [
    ([event('staff_reduction', 40, 1, checkpointId=A, staffCount=1)], '/events/0/checkpointId'),
    ([event('resume', 40, 1, checkpointId=A)], '/events/0/checkpointId'),
])
def test_staff_events_conflict_with_a_schedule(events, field):
    assert validate('KpiSimulationRequest', scheduled([(0, 1), (30, 3)]) | {'events': events}) == [
        {'field': field, 'code': 'event_conflict', 'message': 'The event contradicts the scheduled timeline.'}]


@pytest.mark.parametrize('steps,staff,code', [
    ([(5, 2)], 2, 'invalid_value'),
    ([(0, 2), (0, 3)], 3, 'invalid_value'),
    ([(0, 2), (120, 3)], 3, 'invalid_value'),
    ([(0, 2), (30, 3)], 2, 'comparison_mismatch'),
])
def test_malformed_schedules_return_field_errors(steps, staff, code):
    value = scheduled(steps)
    value['checkpoints'][0]['staffCount'] = value['passengerGeneration']['checkpoints'][0]['staffCount'] = staff
    assert ('/staffSchedules/0/steps', code) in {(item['field'], item['code']) for item in validate('KpiSimulationRequest', value)}


def test_schedules_name_each_known_checkpoint_once():
    value = scheduled([(0, 1), (30, 3)])
    value['staffSchedules'].append(dict(value['staffSchedules'][0]))
    value['staffSchedules'].append({'checkpointId': '60000000-0000-0000-0000-000000000999', 'steps': [{'startMinute': 0, 'staffCount': 1}]})
    assert {(item['field'], item['code']) for item in validate('KpiSimulationRequest', value)} == {
        ('/staffSchedules/1/checkpointId', 'duplicate_id'), ('/staffSchedules/2/checkpointId', 'invalid_value')}


def test_schedules_need_the_kpi_engine_and_an_events_list():
    value = scheduled([(0, 1), (30, 3)])
    value.pop('events')
    assert ('/events', 'required') in {(item['field'], item['code']) for item in validate('KpiSimulationRequest', value)}


def test_scheduled_runs_are_reproducible_and_valid_over_http():
    value = scheduled([(0, 1), (30, 3), (90, 2)], checkpoint=B, navigation=True)
    first, second = run(value), run(value)
    assert first == second and validate('KpiSimulationResponse', first) == []
    with TestClient(app) as client:
        response = client.post('/simulation/kpi-run', json=value)
    assert response.status_code == 200 and response.json() == first


def test_queue_forecasts_follow_the_staff_in_effect_each_minute():
    from tests.test_queue_forecast import request
    from app.simulation.forecast import forecast
    value = request()
    simulation = value['simulation']
    checkpoint = simulation['checkpoints'][0]['checkpointId']
    for item in simulation['checkpoints'] + simulation['passengerGeneration']['checkpoints']:
        item['staffCount'] = 3
    simulation |= {'events': [], 'staffSchedules': [{'checkpointId': checkpoint, 'steps': [
        {'startMinute': 0, 'staffCount': 1}, {'startMinute': 6, 'staffCount': 3}]}]}
    assert not validate('QueueForecastRequest', value)
    result = forecast(value)
    assert not validate('QueueForecastResult', result)
    assert [point['availableStaff'] for point in result['checkpoints'][0]['points']][:8] == [1, 1, 1, 1, 1, 1, 3, 3]

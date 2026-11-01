"""An agent observes the run paused at a minute, decides, and its decisions only act after that minute."""
import json

import pytest
from fastapi.testclient import TestClient

from app.contracts.models import SimulationRequest
from app.contracts.validation import CONTRACTS
from app.simulation.engine import simulate as engine
from app.main import app
from app.simulation.observation import observe
from app.simulation.processes import simulate
from tests import test_check_in, test_journeys
from tests.test_staff_schedule import A, at, run, scheduled


def simulate_any(value):
    return engine(SimulationRequest.model_validate(value)).model_dump(mode='json', by_alias=True)


def observed(value, minute, gate=None):
    return observe(SimulationRequest.model_validate(value), minute, gate)


def test_observation_matches_the_full_run_at_that_minute():
    value = scheduled([(0, 1)])
    state, full = observed(value, 30), run(value)
    for control in state['checkpoints']:
        row = at(full, control['checkpointId'], 30)
        assert control['queued'] == row['queuedPassengers'] and control['processed'] == row['processedPassengers']
    assert next(c for c in state['checkpoints'] if c['checkpointId'] == A)['staffOnDuty'] == 1


def test_a_decision_after_the_minute_leaves_the_observation_unchanged():
    lean, reinforced = scheduled([(0, 1)]), scheduled([(0, 1), (31, 3)])
    # The step at 31 cannot change minute 30.
    assert observed(lean, 30) == observed(reinforced, 30)
    assert next(c for c in observed(reinforced, 40)['checkpoints'] if c['checkpointId'] == A)['staffOnDuty'] == 3


def test_desks_opened_later_serve_from_their_minute_and_never_before():
    steady = test_check_in.base(passengers=120, check_in={'staffCount': 1})
    opened = test_check_in.base(passengers=120, check_in={'staffCount': 3, 'deskSteps': [
        {'startMinute': 0, 'staffCount': 1}, {'startMinute': 20, 'staffCount': 3}]})
    a, b = test_check_in.run(steady), test_check_in.run(opened)
    early = lambda response: [r for r in response['checkInResult']['passengers'] if r['startedMinute'] is not None and r['startedMinute'] < 20]
    assert early(a) == early(b)
    assert max(r['startedMinute'] for r in b['checkInResult']['passengers'] if r['startedMinute'] is not None) <= \
        max(r['startedMinute'] for r in a['checkInResult']['passengers'] if r['startedMinute'] is not None)
    rows = {row['minute']: row for row in b['checkInResult']['snapshots']}
    assert rows[10]['capacity'] == 1 and rows[30]['capacity'] == 3
    assert observed(opened, 25)['checkIn']['desksOpen'] == 3 and observed(opened, 15)['checkIn']['desksOpen'] == 1


@pytest.mark.parametrize('steps', [[{'startMinute': 5, 'staffCount': 3}], [{'startMinute': 0, 'staffCount': 1}, {'startMinute': 20, 'staffCount': 2}],
                                   [{'startMinute': 0, 'staffCount': 3}, {'startMinute': 20, 'staffCount': 3}, {'startMinute': 20, 'staffCount': 1}]])
def test_desk_steps_start_at_zero_peak_at_the_staffing_and_increase_in_time(steps):
    with pytest.raises(ValueError):
        SimulationRequest.model_validate(test_check_in.base(check_in={'staffCount': 3, 'deskSteps': steps}))


def test_desk_steps_may_close_desks():
    steps = [{'startMinute': 0, 'staffCount': 3}, {'startMinute': 20, 'staffCount': 1}]
    SimulationRequest.model_validate(test_check_in.base(check_in={'staffCount': 3, 'deskSteps': steps}))


def test_keeping_gates_on_a_delay_moves_no_flight_when_a_plan_exists():
    value = test_journeys.example()
    value['events'] = [test_journeys.delay(1, 90, 120), test_journeys.delay(2, 130, 90, 31)]
    options = observed(value, 90, test_journeys.identifier(30))['gate']
    assert options['optimal']['moves'] and options['keepGates'] is not None and not options['keepGates']['moves']
    value['journey']['gateChoices'] = [{'eventId': test_journeys.identifier(30), 'mode': 'keep_gates'}]
    kept = test_journeys.run(value)['journeyResult']
    before, after = ({f['flightId']: f['gateId'] for f in plan['flights']} for plan in kept['plans'][:2])
    assert before == after
    assert test_journeys.run(value) == test_journeys.run(value)


def test_gate_choices_must_name_the_runs_delays_once():
    value = test_journeys.example()
    value['events'] = [test_journeys.delay(1, 90, 120)]
    value['journey']['gateChoices'] = [{'eventId': test_journeys.identifier(99), 'mode': 'keep_gates'}]
    with pytest.raises(ValueError):
        SimulationRequest.model_validate(value)


def test_http_observe_validates_the_minute():
    client, value = TestClient(app), scheduled([(0, 1)])
    assert client.post('/simulation/v1/observe', json={'request': value, 'minute': 30}).json()['minute'] == 30
    for minute in (0, 120, 12.5):
        assert client.post('/simulation/v1/observe', json={'request': value, 'minute': minute}).status_code == 422
    assert client.post('/simulation/v1/observe', json={'request': value, 'minute': 30, 'extra': 1}).status_code == 422
    for minute in ('abc', [1], {'a': 1}, True, None):
        assert client.post('/simulation/v1/observe', json={'request': value, 'minute': minute}).status_code == 422


def test_http_observe_accepts_the_limits_the_api_sends():
    client, value = TestClient(app), scheduled([(0, 1)])
    limits = {'deskLimit': 10, 'reserve': 10, 'transferMinutes': 5, 'frozen': []}
    assert client.post('/simulation/v1/observe', json={'request': value, 'minute': 30, 'limits': limits}).status_code == 200
    for bad in ({**limits, 'reserve': -1}, {**limits, 'reserve': 2.5}, {**limits, 'reserve': True}, {**limits, 'extra': 1}):
        assert client.post('/simulation/v1/observe', json={'request': value, 'minute': 30, 'limits': bad}).status_code == 422
    # A huge exponent is refused at once instead of stalling the event loop in int().
    huge = '{"request": %s, "minute": 30, "limits": {"reserve": 1e999999}}' % json.dumps(value)
    response = client.post('/simulation/v1/observe', content=huge, headers={'content-type': 'application/json'})
    assert response.status_code == 422
    response = client.post('/simulation/v1/observe', content=huge.replace('"minute": 30', '"minute": 1e999999').replace('1e999999}}', '1}}'),
                           headers={'content-type': 'application/json'})
    assert response.status_code == 422


@pytest.mark.parametrize('name', ['kpis/v1/examples/check-in-desks', 'journeys/v1/examples/gate-choice'])
def test_shared_examples_are_the_engine_output(name):
    load = lambda suffix: json.loads((CONTRACTS / f'{name}-{suffix}.json').read_text(encoding='utf-8'))
    assert simulate_any(load('request')) == load('response')

"""Shift plans: exact on small instances, every rule re-checked, forged plans rejected."""
import copy
import itertools
import json
import random

import pytest
from fastapi.testclient import TestClient

from app.contracts.validation import CONTRACTS, validate
from app.main import app
from app.optimization.shifts import evaluate, solve

TERMINAL = '10000000-0000-0000-0000-000000000001'
A, B = '20000000-0000-0000-0000-000000000001', '20000000-0000-0000-0000-000000000002'
P1, P2 = '30000000-0000-0000-0000-000000000001', '30000000-0000-0000-0000-000000000002'


def example(name):
    return json.loads((CONTRACTS / 'staffing/v1/examples' / (name + '.json')).read_text(encoding='utf-8'))


def tiny(seed, slots=5):
    rng = random.Random(seed)
    checkpoint = lambda key, limit: {'checkpointId': key, 'terminalId': TERMINAL, 'minimumStaff': 0, 'maximumStaff': 2,
                                     'physicalLimit': limit, 'requiredOpen': False, 'closed': False,
                                     'capacityMilliPerMinute': 400, 'arrivalsPerSlot': [rng.randint(0, 9) for _ in range(slots)]}
    return {'contractVersion': '1.0', 'scenarioId': '40000000-0000-0000-0000-000000000001', 'scenarioRevision': 1,
            'slotMinutes': 15, 'slotCount': slots, 'timeLimitSeconds': 10,
            'rules': {'minShiftSlots': 2, 'maxShiftSlots': 4, 'breakAfterSlots': 2, 'transferMinutes': 5,
                      'maxMovesPerHour': 1, 'queueWeight': 1, 'staffSlotCost': 300, 'moveCost': 700,
                      'targetUtilizationPercent': 100},
            'staff': [{'staffMemberId': P1, 'terminalId': TERMINAL}, {'staffMemberId': P2, 'terminalId': TERMINAL}],
            'checkpoints': [checkpoint(A, 2), checkpoint(B, 1)]}


def shift_of(person, pattern):
    """The shift a per-slot pattern (checkpoint or None) describes, or None for a day off."""
    worked = [t for t, place in enumerate(pattern) if place]
    if not worked:
        return None
    start, end, segments = worked[0], worked[-1] + 1, []
    for t in range(start, end):
        if pattern[t] is None:
            continue
        if segments and segments[-1]['checkpointId'] == pattern[t] and segments[-1]['endSlot'] == t:
            segments[-1]['endSlot'] = t + 1
        else:
            segments.append({'checkpointId': pattern[t], 'startSlot': t, 'endSlot': t + 1})
    return {'staffMemberId': person, 'startSlot': start, 'endSlot': end,
            'breaks': [t for t in range(start, end) if pattern[t] is None], 'segments': segments}


def brute_force(request):
    """Best score over every per-slot assignment that satisfies the rules, by enumeration."""
    rules, slots = request['rules'], request['slotCount']
    limits = {item['checkpointId']: min(item['maximumStaff'], item['physicalLimit']) for item in request['checkpoints']}
    best = None
    patterns = list(itertools.product([None, A, B], repeat=slots))
    for first, second in itertools.product(patterns, repeat=2):
        shifts = [shift for shift in (shift_of(P1, first), shift_of(P2, second)) if shift]
        if any(not rules['minShiftSlots'] <= shift['endSlot'] - shift['startSlot'] <= rules['maxShiftSlots'] for shift in shifts):
            continue
        if any(all(pattern[k] for k in range(t, t + rules['breakAfterSlots'] + 1))
               for pattern in (first, second) for t in range(slots - rules['breakAfterSlots'])):
            continue
        plans, _, _, score = evaluate(request, shifts)
        if any(count > limits[plan['checkpointId']] for plan in plans for count in plan['staff']):
            continue
        if any(sum(plan['arrivingMovers'][k] for plan in plans for k in range(t, min(slots, t + 4))) > rules['maxMovesPerHour']
               for t in range(slots)):
            continue
        best = score if best is None else min(best, score)
    return best


@pytest.mark.parametrize('seed', range(6))
def test_small_plans_are_optimal_against_exhaustive_enumeration(seed):
    request = tiny(seed)
    result = solve(request)
    assert result['status'] == 'optimal' and result['score'] == result['bestBound'] == brute_force(request)
    assert validate('ShiftPlanResult', json.loads(json.dumps(result))) == []


def test_a_long_day_follows_the_waves_within_every_rule():
    request = example('shift-plan-request')
    result = solve(request)
    assert result['status'] in ('optimal', 'feasible') and validate('ShiftPlanResult', json.loads(json.dumps(result))) == []
    rules = request['rules']
    for shift in result['shifts']:
        assert rules['minShiftSlots'] <= shift['endSlot'] - shift['startSlot'] <= rules['maxShiftSlots']
        assert shift['segments'][0]['startSlot'] == shift['startSlot'] and shift['segments'][-1]['endSlot'] == shift['endSlot']
    # More people stand where the passengers are: the busiest slot is staffed above the quietest one.
    for plan, checkpoint in zip(result['checkpoints'], sorted(request['checkpoints'], key=lambda item: item['checkpointId'])):
        arrivals = checkpoint['arrivalsPerSlot']
        assert plan['staff'][arrivals.index(max(arrivals))] > plan['staff'][arrivals.index(min(arrivals))]


def forged(mutate):
    result = json.loads(json.dumps(solve(tiny(3))))
    mutate(result)
    return validate('ShiftPlanResult', result)


@pytest.mark.parametrize('mutate', [
    lambda value: value['checkpoints'][0]['staff'].__setitem__(0, value['checkpoints'][0]['staff'][0] + 1),
    lambda value: value.__setitem__('score', value['score'] - 1),
    lambda value: value.__setitem__('moves', value['moves'] + 1),
    lambda value: value['shifts'][0].__setitem__('endSlot', value['shifts'][0]['endSlot'] + 1),
    lambda value: value['shifts'][0].__setitem__('breaks', [99]),
    lambda value: value['shifts'].append(copy.deepcopy(value['shifts'][0])),
    lambda value: value['request']['rules'].__setitem__('maxShiftSlots', 1),
])
def test_forged_plans_are_rejected(mutate):
    assert {item['code'] for item in forged(mutate)} & {'invalid_result', 'out_of_range'}


def test_a_shift_without_a_break_is_rejected():
    request = tiny(1, slots=4)
    request['rules'] |= {'breakAfterSlots': 2, 'maxShiftSlots': 4}
    result = {'status': 'feasible', 'solverStatus': 'FEASIBLE', 'timedOut': True, 'request': request,
              'objective': solve(request)['objective'], 'wallTimeSeconds': 0}
    shift = {'staffMemberId': P1, 'startSlot': 0, 'endSlot': 3, 'breaks': [],
             'segments': [{'checkpointId': A, 'startSlot': 0, 'endSlot': 3}]}
    plans, duty, moves, score = evaluate(request, [shift])
    result |= {'shifts': [shift], 'checkpoints': plans, 'dutySlots': duty, 'moves': moves, 'score': score, 'bestBound': 0}
    assert validate('ShiftPlanResult', result) == [{'field': '/shifts/0', 'code': 'invalid_result',
                                                    'message': 'Result fields contradict the run status or comparison.'}]


def test_a_required_checkpoint_is_staffed_while_passengers_arrive_not_while_empty():
    request = tiny(0, slots=4)
    request['rules'] |= {'minShiftSlots': 1, 'maxShiftSlots': 4, 'breakAfterSlots': 4}
    request['checkpoints'][0] |= {'requiredOpen': True, 'arrivalsPerSlot': [0, 3, 3, 0]}
    request['checkpoints'][1] |= {'requiredOpen': True, 'arrivalsPerSlot': [0, 0, 0, 0]}
    plans = {plan['checkpointId']: plan['staff'] for plan in solve(request)['checkpoints']}
    assert plans[A] == [0, 1, 1, 0] and plans[B] == [0, 0, 0, 0]


def test_headroom_staffs_a_steady_load_that_one_operator_would_just_cover():
    # 14 passengers per slot against 15 per operator-slot: a fluid plan sees no queue with one operator.
    request = tiny(0, slots=4)
    request['rules'] |= {'minShiftSlots': 4, 'maxShiftSlots': 4, 'breakAfterSlots': 4, 'staffSlotCost': 1}
    request['checkpoints'][0] |= {'capacityMilliPerMinute': 1000, 'arrivalsPerSlot': [14] * 4}
    request['checkpoints'][1]['arrivalsPerSlot'] = [0] * 4
    staffed = lambda percent: solve(request | {'rules': request['rules'] | {'targetUtilizationPercent': percent}})['checkpoints']
    at = lambda plans: next(plan for plan in plans if plan['checkpointId'] == A)['staff']
    assert at(staffed(100)) == [1, 1, 1, 1]
    assert at(staffed(75)) == [2, 2, 2, 2]


def test_a_zero_budget_reports_a_timeout_without_a_plan():
    request = example('shift-plan-request') | {'timeLimitSeconds': 0}
    result = solve(request)
    assert result['status'] == 'timeout' and result['shifts'] == [] and result['score'] is None
    assert validate('ShiftPlanResult', json.loads(json.dumps(result))) == []


@pytest.mark.parametrize('mutate,field,code', [
    (lambda value: value['checkpoints'][0]['arrivalsPerSlot'].pop(), '/checkpoints/0/arrivalsPerSlot', 'comparison_mismatch'),
    (lambda value: value['rules'].__setitem__('maxShiftSlots', 1), '/rules/maxShiftSlots', 'out_of_range'),
    (lambda value: value['checkpoints'][0].update(requiredOpen=True, closed=True), '/checkpoints/0/requiredOpen', 'invalid_value'),
    (lambda value: value['staff'].append(dict(value['staff'][0])), '/staff/2/staffMemberId', 'duplicate_id'),
])
def test_contradictory_requests_return_field_errors(mutate, field, code):
    request = tiny(0)
    mutate(request)
    assert (field, code) in {(item['field'], item['code']) for item in validate('ShiftPlanRequest', request)}


def test_the_route_validates_and_answers_with_a_checked_plan():
    with TestClient(app) as client:
        bad = tiny(0)
        bad['checkpoints'][0]['arrivalsPerSlot'].pop()
        assert client.post('/optimization/staffing/v1/shifts', json=bad).status_code == 422
        reply = client.post('/optimization/staffing/v1/shifts', json=tiny(2))
        assert reply.status_code == 200 and reply.json()['status'] == 'optimal'
        assert validate('ShiftPlanResult', reply.json()) == []
        assert client.post('/contracts/v1/staffing/shifts/result/validate', json=example('shift-plan-response')).status_code == 200

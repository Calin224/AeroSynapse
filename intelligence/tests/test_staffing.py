import copy
import itertools
import json
import random

import pytest
from fastapi.testclient import TestClient

from app.contracts.validation import CONTRACTS, validate
from app.main import app
from app.optimization.staffing import solve, generate_candidates

ROOT = CONTRACTS / 'staffing/v1/examples'


def request():
    return json.loads((ROOT / 'staffing-request.json').read_text())


def feasible(value, allocation):
    if sum(allocation) > value['availableStaff']: return False
    for item, count in zip(value['checkpoints'], allocation):
        if count > min(item['maximumStaff'], item['physicalLimit']): return False
        if item['closed'] and count: return False
        if item['requiredOpen'] and count < max(1, item['minimumStaff']): return False
        if count and count < item['minimumStaff']: return False
    return all(sum(count for item, count in zip(value['checkpoints'], allocation) if item['terminalId'] == pool['terminalId'])
               <= pool['availableStaff'] for pool in value['staffPools'])


def objective(value, allocation):
    return sum(max(0, item['demandMilli'] - count * item['capacityMilli'])
               + item['changeCost'] * abs(count - item['currentStaff'])
               for item, count in zip(value['checkpoints'], allocation))


def test_small_optimum_redistributes_two_people_and_reports_reconciled_capacity_and_score():
    value = request(); result = solve(value)
    assert result['status'] == 'optimal' and not result['timedOut']
    assert [item['staffCount'] for item in result['assignments']] == [2, 1]
    assert result['score'] == result['bestBound'] == 40
    assert result['usedStaff'] == 3 and result['unassignedStaff'] == 0
    assert result['constraints'] == value
    assert [item['estimatedCapacityMilli'] for item in result['assignments']] == [6000, 1000]
    assert validate('StaffingSolveResult', result) == []


def test_change_cost_is_part_of_the_optimization_not_just_a_reported_label():
    value = request()
    for item in value['checkpoints']: item['changeCost'] = 10000
    result = solve(value)
    assert [item['staffCount'] for item in result['assignments']] == [0, 3]
    assert result['score'] == 6000


def test_closed_controls_get_zero_and_physical_capacity_is_a_hard_limit():
    value = request()
    value['checkpoints'][0]['physicalLimit'] = 1
    value['checkpoints'][1]['closed'] = True
    result = solve(value)
    assert result['status'] == 'optimal'
    assert [item['staffCount'] for item in result['assignments']] == [1, 0]
    assert result['unassignedStaff'] == 2


@pytest.mark.parametrize('kind', ['short_staff', 'closed_required', 'physical_minimum'])
def test_infeasible_constraints_return_no_configuration_or_fabricated_score(kind):
    value = request()
    item = value['checkpoints'][0]
    item['requiredOpen'] = True; item['minimumStaff'] = 2
    if kind == 'short_staff': value['availableStaff'] = 1
    elif kind == 'closed_required': item['closed'] = True
    else: item['physicalLimit'] = 1
    result = solve(value)
    assert result['status'] == 'infeasible' and result['solverStatus'] == 'INFEASIBLE'
    assert result['assignments'] == [] and result['score'] is result['usedStaff'] is result['bestBound'] is None


def test_a_real_zero_solver_budget_reports_timeout_without_reading_unassigned_variables():
    result = solve(request() | {'timeLimitSeconds': 0})
    assert result['status'] == 'timeout' and result['solverStatus'] == 'UNKNOWN' and result['timedOut']
    assert result['assignments'] == [] and result['score'] is result['usedStaff'] is result['bestBound'] is None


def test_maximum_profile_capacity_and_staffing_use_integer_arithmetic_without_overflow():
    value = request()
    value['availableStaff'] = value['staffPools'][0]['availableStaff'] = 10000
    value['checkpoints'] = [value['checkpoints'][0] | {
        'currentStaff': 10000, 'minimumStaff': 10000, 'maximumStaff': 10000,
        'physicalLimit': 10000, 'requiredOpen': True, 'capacityMilli': 10**13}]
    assert validate('StaffingSolveRequest', value) == []
    result = solve(value)
    assert result['status'] == 'optimal'
    assert result['assignments'][0]['estimatedCapacityMilli'] == 10**17
    assert result['usedStaff'] == 10000 and result['score'] == result['bestBound'] == 0
    assert validate('StaffingSolveResult', result) == []


def test_optional_controls_open_with_the_minimum_or_stay_closed():
    value = request(); value['availableStaff'] = 1
    for item in value['checkpoints']: item['minimumStaff'] = 2
    result = solve(value)
    assert result['status'] == 'optimal' and result['usedStaff'] == 0


def test_multi_checkpoint_solutions_match_exhaustive_search_and_respect_terminal_pools():
    rng = random.Random(31)
    for _ in range(30):
        value = request()
        third = copy.deepcopy(value['checkpoints'][0])
        third['checkpointId'] = '60000000-0000-0000-0000-000000000103'
        third['terminalId'] = '60000000-0000-0000-0000-000000000003'
        value['checkpoints'].append(third)
        value['staffPools'] = [value['staffPools'][0] | {'availableStaff': 2},
                               {'terminalId': third['terminalId'], 'availableStaff': 1}]
        for item in value['checkpoints']:
            item.update(minimumStaff=rng.randrange(3), maximumStaff=rng.randrange(4), physicalLimit=rng.randrange(4),
                        requiredOpen=rng.choice([False, True]), currentStaff=rng.randrange(4),
                        demandMilli=rng.randrange(8) * 1000, capacityMilli=rng.randrange(4) * 1000,
                        changeCost=rng.randrange(40))
        candidates = [allocation for allocation in itertools.product(range(4), repeat=3) if feasible(value, allocation)]
        result = solve(value)
        if not candidates:
            assert result['status'] == 'infeasible'
        else:
            assert result['status'] == 'optimal'
            allocation = tuple(item['staffCount'] for item in result['assignments'])
            assert feasible(value, allocation)
            assert result['score'] == min(objective(value, allocation) for allocation in candidates)


def test_http_normalizes_mathematical_integers_and_returns_explicit_validation_errors():
    with TestClient(app) as client:
        value = request(); value['availableStaff'] = 3.0
        response = client.post('/optimization/staffing/v1/solve', json=value)
        assert response.status_code == 200 and response.json()['score'] == 40
        value['checkpoints'].append(copy.deepcopy(value['checkpoints'][0]))
        response = client.post('/optimization/staffing/v1/solve', json=value)
        assert response.status_code == 422
        assert any(item['code'] == 'duplicate_id' for item in response.json()['errors'])
        value = request(); value['checkpoints'][0]['physicalLimit'] = -1
        response = client.post('/optimization/staffing/v1/solve', json=value)
        assert response.status_code == 422
        assert response.json()['errors'][0]['field'] == '/checkpoints/0/physicalLimit'


def test_candidate_search_produces_distinct_feasible_non_baseline_allocations_with_actual_scores():
    value = {'constraints': request(), 'candidateCount': 4}
    result = generate_candidates(value)
    assert result['status'] == 'complete' and len(result['candidates']) == 4
    baseline = tuple(item['currentStaff'] for item in value['constraints']['checkpoints'])
    seen = {baseline}
    scores = []
    for item in result['candidates']:
        counts = tuple(assignment['staffCount'] for assignment in item['assignments'])
        assert counts not in seen and feasible(value['constraints'], counts)
        assert item['score'] == objective(value['constraints'], counts)
        seen.add(counts); scores.append(item['score'])
    assert scores == sorted(scores)
    assert validate('StaffingCandidateSearchResult', result) == []


def test_candidate_search_reports_exhaustion_without_padding_the_list_with_duplicates():
    constraints = request(); constraints['availableStaff'] = 1
    for item in constraints['checkpoints']:
        item.update(currentStaff=0, maximumStaff=1, physicalLimit=1)
    result = generate_candidates({'constraints': constraints, 'candidateCount': 4})
    assert result['status'] == 'exhausted' and len(result['candidates']) == 2
    assert validate('StaffingCandidateSearchResult', result) == []


@pytest.mark.parametrize('timeout', [False, True])
def test_candidate_search_never_invents_a_configuration_for_infeasibility_or_timeout(timeout):
    constraints = request()
    if timeout: constraints['timeLimitSeconds'] = 0
    else: constraints['checkpoints'][0].update(requiredOpen=True, closed=True)
    result = generate_candidates({'constraints': constraints, 'candidateCount': 4})
    assert result['status'] == ('timeout' if timeout else 'exhausted')
    assert result['candidates'] == []
    assert validate('StaffingCandidateSearchResult', result) == []


def test_all_candidate_solves_share_one_decreasing_search_budget(monkeypatch):
    import app.optimization.staffing as staffing
    configured = staffing.configured_solver
    budgets = []
    def record(seconds):
        budgets.append(seconds)
        return configured(seconds)
    monkeypatch.setattr(staffing, 'configured_solver', record)
    result = generate_candidates({'constraints': request() | {'timeLimitSeconds': 1}, 'candidateCount': 4})
    assert result['status'] == 'complete' and len(budgets) == 4
    assert 0 < budgets[-1] < budgets[0] <= 1


@pytest.mark.parametrize('corruption', ['duplicate', 'baseline', 'score', 'status'])
def test_candidate_results_reject_duplicate_baseline_and_forged_configurations(corruption):
    result = generate_candidates({'constraints': request(), 'candidateCount': 4})
    if corruption == 'duplicate': result['candidates'][1] = copy.deepcopy(result['candidates'][0])
    if corruption == 'baseline':
        for item, constraint in zip(result['candidates'][0]['assignments'], result['request']['constraints']['checkpoints']):
            item['staffCount'] = constraint['currentStaff']
    if corruption == 'score': result['candidates'][0]['score'] += 1
    if corruption == 'status': result['status'] = 'timeout'
    assert validate('StaffingCandidateSearchResult', result)


def test_candidate_search_http_accepts_mathematical_integers_and_rejects_invalid_counts():
    with TestClient(app) as client:
        response = client.post('/optimization/staffing/v1/candidates', json={'constraints': request(), 'candidateCount': 4.0})
        assert response.status_code == 200 and len(response.json()['candidates']) == 4
        response = client.post('/optimization/staffing/v1/candidates', json={'constraints': request(), 'candidateCount': 5})
        assert response.status_code == 422

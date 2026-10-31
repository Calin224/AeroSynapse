import copy
import itertools
import json

from fastapi.testclient import TestClient
from app.main import app
from app.contracts.validation import CONTRACTS, validate
from app.optimization.pareto import search, values, FORMULA
from test_staffing import feasible


def request():
    constraints = json.loads((CONTRACTS / 'staffing/v1/examples/staffing-request.json').read_text())
    return {'constraints': constraints, 'candidateCount': 4, 'durationMinutes': 60,
            'options': {'weights': {'time': 40, 'cost': 40, 'risk': 20},
                'normalization': {'timeMinutes': 10, 'costPoints': 1000, 'riskFraction': .1},
                'operationalBudget': 10000, 'staffMinuteCost': 1,
                'maxMeanWaitMinutes': 10080, 'maxP95WaitMinutes': 10080, 'maxBoardingRisk': 1}}


def score(request, counts, weights):
    return sum(values(request, cp, count, weights)[3] for cp, count in zip(sorted(request['constraints']['checkpoints'], key=lambda cp: cp['checkpointId']), counts))


def test_weighted_and_anchor_solves_match_exhaustive_optima_with_real_different_assignments():
    value = request(); result = search(value)
    assert validate('ParetoSearchRequest', value) == []
    assert validate('ParetoSearchResult', result) == []
    assert result['formula'] == FORMULA and len(result['candidates']) >= 2
    baseline = tuple(cp['currentStaff'] for cp in value['constraints']['checkpoints'])
    allocations = [counts for counts in itertools.product(range(4), repeat=2)
                   if feasible(value['constraints'], counts) and counts != baseline
                   and sum(values(value, cp, count, value['options']['weights'])[2]
                           for cp, count in zip(value['constraints']['checkpoints'], counts)) <= value['options']['operationalBudget']]
    for solve in result['solves'][:4]:
        assert solve['attempted'] and solve['solverStatus'] == 'OPTIMAL'
        assert solve['scaledObjective'] == solve['bestBound'] == min(score(value, counts, solve['weights']) for counts in allocations)
    assert result['solves'][1]['assignments'] != result['solves'][2]['assignments']
    assert result['solves'][1]['operationalCost'] < result['solves'][2]['operationalCost']
    assert result['solves'][1]['proxyTimeMinutes'] > result['solves'][2]['proxyTimeMinutes']


def test_changed_weights_rebuild_the_selected_objective_and_change_the_actual_solver_solution():
    value = request(); value['options']['weights'] = {'time': 100, 'cost': 0, 'risk': 0}
    fast = search(value)
    value = copy.deepcopy(value); value['options']['weights'] = {'time': 0, 'cost': 100, 'risk': 0}
    cheap = search(value)
    a, b = fast['solves'][0], cheap['solves'][0]
    assert a['assignments'] != b['assignments']
    assert a['scaledObjective'] == score(fast['request'], tuple(cp['staffCount'] for cp in a['assignments']), a['weights'])
    assert b['scaledObjective'] == score(cheap['request'], tuple(cp['staffCount'] for cp in b['assignments']), b['weights'])
    assert a['proxyTimeMinutes'] < b['proxyTimeMinutes'] and a['operationalCost'] > b['operationalCost']


def test_hard_operating_budget_excludes_expensive_candidates_and_infeasibility_is_explicit():
    value = request(); value['options']['operationalBudget'] = 0
    result = search(value)
    assert not result['candidates']
    assert all(row['solverStatus'] == 'INFEASIBLE' for row in result['solves'])
    assert all(row['scaledObjective'] is None and row['assignments'] == [] for row in result['solves'])
    assert result['status'] == 'exhausted'


def test_zero_search_budget_and_discrete_plateaus_never_fabricate_solutions():
    value = request(); value['constraints']['timeLimitSeconds'] = 0
    result = search(value)
    assert result['status'] == 'timeout' and result['candidates'] == []
    assert len(result['solves']) >= 4 and not any(row['attempted'] for row in result['solves'])


def test_http_validation_enforces_weights_normalization_and_bounded_integer_coefficients():
    with TestClient(app) as client:
        value = request()
        result = client.post('/optimization/staffing/v1/pareto', json=value)
        assert result.status_code == 200
        assert result.json()['request'] == value
        bad = request(); bad['options']['weights'] = {'time': 0, 'cost': 0, 'risk': 0}
        assert client.post('/optimization/staffing/v1/pareto', json=bad).status_code == 422
        bad = request(); bad['options']['normalization']['timeMinutes'] = 0
        assert client.post('/optimization/staffing/v1/pareto', json=bad).status_code == 422
        bad = request(); bad['options']['normalization']['costPoints'] = 1; bad['options']['operationalBudget'] = 10**14
        assert client.post('/optimization/staffing/v1/pareto', json=bad).status_code == 422

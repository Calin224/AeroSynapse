import copy
import itertools
import json
import random

import pytest
from fastapi.testclient import TestClient

from app.contracts.validation import CONTRACTS, validate
from app.main import app
from app.optimization.resources import covers, solve

ROOT = CONTRACTS / 'resources/v1/examples'


def request():
    return json.loads((ROOT / 'resource-request.json').read_text())


def test_real_solver_avoids_unavailable_gate_and_minimizes_changes_without_mutating_inputs():
    value = request(); before = copy.deepcopy(value)
    result = solve(value)
    assert result['status'] == 'optimal'
    assert result['score'] == result['bestBound'] == 1
    assert result['assignments'][0]['gateId'] == value['resources']['gates'][0]['gateId']
    assert result['advisoryOnly'] and not result['rules']
    assert value == before and result['constraints'] == value
    assert validate('ResourceSolveResult', result) == []


def test_adjacent_windows_merge_but_a_gap_remains_unavailable():
    w = lambda a, b: {'startMinute': a, 'endMinute': b}
    assert covers([w(10, 20), w(0, 10), w(18, 30)], 5, 30)
    assert not covers([w(0, 10), w(10.0001, 30)], 5, 30)
    assert not covers([], 0, 10)


@pytest.mark.parametrize('kind,code', [('availability','gate_availability'), ('skills','team_skills'),
    ('shift','team_shift'), ('size','team_size'), ('compatibility','gate_compatibility'), ('team-gate','team_gate_compatibility')])
def test_infeasible_explanations_name_the_flight_resource_and_concrete_rule(kind, code):
    value = request(); config = value['resources']
    if kind == 'availability':
        for gate in config['gates']: gate['availability'] = []
    if kind == 'skills':
        for worker in config['workers']: worker['skills'] = []
    if kind == 'shift':
        for worker in config['workers']: worker['shifts'] = [{'startMinute':0,'endMinute':20}]
    if kind == 'size':
        for requirement in config['requirements']: requirement['minimumTeamSize'] = 2
    if kind == 'compatibility':
        for requirement in config['requirements']: requirement['compatibleGateIds'] = []
    if kind == 'team-gate':
        for team in config['teams']: team['gateIds'] = []
    assert validate('ResourceSolveRequest', value) == []  # Impossible constraints are valid planning problems.
    result = solve(value)
    assert result['status'] == 'infeasible' and not result['assignments']
    assert result['score'] is None and result['bestBound'] is None
    assert any(r['code'] == code and r['flightId'] and r['resourceId'] and r['message'] for r in result['rules'])
    assert validate('ResourceSolveResult', result) == []


def overlapping(value):
    for flight in value['flights']:
        flight.update(startMinute=10,endMinute=40)
    for gate in value['resources']['gates']:
        gate['availability'] = [{'startMinute':0,'endMinute':90}]


@pytest.mark.parametrize('kind,code', [('gate','gate_capacity'), ('team','team_capacity'), ('person','staff_capacity')])
def test_overlapping_resources_and_people_shared_across_different_teams_cannot_be_double_booked(kind, code):
    value = request(); overlapping(value); config = value['resources']
    if kind == 'gate':
        for requirement in config['requirements']:
            requirement['compatibleGateIds'] = [config['gates'][0]['gateId']]
    elif kind == 'team':
        config['teams'] = [config['teams'][0]]
    else:
        config['teams'][1]['memberIds'] = config['teams'][0]['memberIds'][:]
    result = solve(value)
    assert result['status'] == 'infeasible'
    assert any(r['code'] in ({'team_capacity', 'staff_capacity'} if kind == 'team' else {code}) for r in result['rules'])
    assert validate('ResourceSolveResult', result) == []


def test_half_open_end_allows_the_same_gate_and_team_to_be_reused():
    value = request(); config = value['resources']
    config['teams'] = [config['teams'][0]]
    for requirement in config['requirements']:
        requirement['compatibleGateIds'] = [config['gates'][0]['gateId']]
    result = solve(value)
    assert result['status'] == 'optimal'
    assert len({a['gateId'] for a in result['assignments']}) == len({a['teamId'] for a in result['assignments']}) == 1


def test_expired_budget_has_no_fabricated_solution_or_infeasibility_claim():
    value = request(); value['timeLimitSeconds'] = 0
    result = solve(value)
    assert result['status'] == 'timeout' and result['timedOut']
    assert result['assignments'] == result['rules'] == [] and result['score'] is None
    assert validate('ResourceSolveResult', result) == []


@pytest.mark.parametrize('kind', ['unknown-gate','unknown-worker','duplicate-worker','duplicate-member','bad-window','horizon','missing-flight'])
def test_contract_rejects_invalid_graphs_and_windows_before_solving(kind):
    value = request(); config = value['resources']
    if kind == 'unknown-gate': config['requirements'][0]['compatibleGateIds'] = [value['scenarioId']]
    if kind == 'unknown-worker': config['teams'][0]['memberIds'] = [value['scenarioId']]
    if kind == 'duplicate-worker': config['workers'].append(copy.deepcopy(config['workers'][0]))
    if kind == 'duplicate-member': config['teams'][0]['memberIds'] *= 2
    if kind == 'bad-window': config['workers'][0]['shifts'][0]['endMinute'] = 0
    if kind == 'horizon': value['flights'][0]['endMinute'] = 91
    if kind == 'missing-flight': config['requirements'].pop()
    with TestClient(app) as client:
        response = client.post('/optimization/resources/v1/solve',json=value)
    assert response.status_code == 422


def brute_force(value):
    """Independent enumeration: all gate/team choices, interval coverage, skills, and per-person capacity."""
    c = value['resources']; workers = {w['staffMemberId']: w for w in c['workers']}
    feasible = []
    def available(windows, start, end):
        points = sorted({start, end} | {w[k] for w in windows for k in ('startMinute','endMinute') if start < w[k] < end})
        return all(any(w['startMinute'] <= (a+b)/2 < w['endMinute'] for w in windows) for a,b in zip(points, points[1:]))
    for allocation in itertools.product(list(itertools.product(c['gates'],c['teams'])), repeat=len(value['flights'])):
        used = []; score = 0
        for f, (g,t) in zip(value['flights'],allocation):
            r = next(r for r in c['requirements'] if r['flightId']==f['flightId'])
            members = [workers[m] for m in t['memberIds']]
            if (g['gateId'] not in r['compatibleGateIds'] or g['gateId'] not in t['gateIds']
                or len(members) < r['minimumTeamSize']
                or not set(r['requiredSkills']) <= {s for m in members for s in m['skills']}
                or not available(g['availability'],f['startMinute'],f['endMinute'])
                or not all(available(m['shifts'],f['startMinute'],f['endMinute']) for m in members)):
                break
            if any(f['startMinute'] < old['endMinute'] and old['startMinute'] < f['endMinute']
                and (g['gateId'] == og['gateId'] or t['teamId'] == ot['teamId'] or set(t['memberIds']) & set(ot['memberIds'])) for old,og,ot in used):
                break
            used.append((f,g,t)); score += g['gateId'] != f['currentGateId']
        else:
            feasible.append(score)
    return min(feasible) if feasible else None


def test_small_random_models_agree_with_exhaustive_enumeration():
    rng = random.Random(35)
    for _ in range(35):
        value = request(); c = value['resources']
        if rng.choice([True, False]): overlapping(value)
        for g in c['gates']:
            g['availability'] = [{'startMinute':rng.choice([0,15,40]),'endMinute':90}]
        for w in c['workers']:
            w['skills'] = rng.choice([[],['boarding'],['boarding','ramp']])
            w['shifts'] = [{'startMinute':0,'endMinute':rng.choice([30,60,90])}]
        for t in c['teams']:
            t['gateIds'] = [g['gateId'] for g in c['gates'] if rng.choice([True,False])]
            t['memberIds'] = rng.choice([[c['workers'][0]['staffMemberId']], [c['workers'][1]['staffMemberId']], [w['staffMemberId'] for w in c['workers']]])
        expected = brute_force(value); result = solve(value)
        assert result['status'] == ('infeasible' if expected is None else 'optimal')
        assert result['score'] == expected
        assert validate('ResourceSolveResult',result) == []


def test_http_boundary_normalizes_fractional_minutes_and_rejects_duplicates_and_extra_fields():
    value = request(); value['flights'][0]['startMinute'] = 10.5
    with TestClient(app) as client:
        response = client.post('/optimization/resources/v1/solve',json=value)
        assert response.status_code == 200
        assert response.json()['constraints']['flights'][0]['startMinute'] == 10.5
        assert validate('ResourceSolveResult',response.json()) == []
        duplicate = json.dumps(value).replace('"scenarioRevision": 1', '"scenarioRevision": 1, "scenarioRevision": 2')
        assert client.post('/optimization/resources/v1/solve',content=duplicate,headers={'Content-Type':'application/json'}).status_code == 422
        assert client.post('/optimization/resources/v1/solve',json=value | {'apply':True}).status_code == 422
        assert client.post('/optimization/resources/v1/solve',content='{}',headers={'Content-Type':'text/plain'}).status_code == 415


@pytest.mark.parametrize('mutation', ['gate','score','duplicate','team','timeout','advisory'])
def test_forged_solver_results_fail_independent_validation(mutation):
    result = solve(request())
    if mutation == 'gate': result['assignments'][0]['gateId'] = result['constraints']['resources']['gates'][1]['gateId']
    if mutation == 'score': result['score'] = 0
    if mutation == 'duplicate': result['assignments'][1] = copy.deepcopy(result['assignments'][0])
    if mutation == 'team': result['assignments'][0]['teamId'] = result['constraints']['scenarioId']
    if mutation == 'timeout': result.update(status='timeout', solverStatus='UNKNOWN', timedOut=True)
    if mutation == 'advisory': result['advisoryOnly'] = False
    assert validate('ResourceSolveResult', result)


@pytest.mark.parametrize('kind', ['skills','shifts'])
def test_large_infeasible_explanations_remain_within_the_shared_result_contract(kind):
    from uuid import UUID
    value = request(); config = value['resources']
    if kind == 'skills':
        for requirement in config['requirements']:
            requirement['requiredSkills'] = [('skill_' + str(i)).ljust(40, 'x') for i in range(30)]
    else:
        config['workers'] = [{'staffMemberId': str(UUID(int=1000+i)), 'skills':['boarding'], 'shifts':[]} for i in range(100)]
        for team in config['teams']:
            team['memberIds'] = [w['staffMemberId'] for w in config['workers']]
    assert validate('ResourceSolveRequest', value) == []
    result = solve(value)
    assert result['status'] == 'infeasible'
    assert all(len(r['message']) <= 1000 for r in result['rules'])
    assert validate('ResourceSolveResult', result) == []


def test_intervals_collapsed_by_binary64_are_rejected_before_the_solver_runs():
    from app.contracts.validation import parse_json
    wire = json.dumps(request()).replace('"startMinute": 10', '"startMinute": 10.0000000000000000000001', 1)
    wire = wire.replace('"endMinute": 40', '"endMinute": 10.0000000000000000000002', 1)
    value, errors = parse_json(wire)
    assert not errors
    assert validate('ResourceSolveRequest', value)
    with TestClient(app) as client:
        response = client.post('/optimization/resources/v1/solve', content=wire, headers={'Content-Type':'application/json'})
    assert response.status_code == 422
    assert 'assignments' not in response.json()


def test_nullable_configuration_reports_the_concrete_nested_field_instead_of_a_union_type_error():
    value = {'revision':1, 'resources':request()['resources']}
    assert validate('ResourceConfigWriteRequest',value) == []
    assert validate('ResourceConfigWriteRequest',{'revision':1,'resources':None}) == []
    value['resources']['requirements'][0]['minimumTeamSize'] = 0
    assert [(e['field'],e['code']) for e in validate('ResourceConfigWriteRequest',value)] == [('/resources/requirements/0/minimumTeamSize','out_of_range')]
    value['resources']['requirements'][0]['minimumTeamSize'] = 1
    value['resources']['workers'][0]['unknown'] = True
    assert [(e['field'],e['code']) for e in validate('ResourceConfigWriteRequest',value)] == [('/resources/workers/0/unknown','unknown_field')]


def test_shift_conflict_does_not_blame_an_unavailable_alternative_gate_when_another_gate_is_available():
    value = request()
    value['flights'] = value['flights'][:1]
    value['resources']['requirements'] = value['resources']['requirements'][:1]
    for worker in value['resources']['workers']:
        worker['shifts'] = [{'startMinute':0,'endMinute':20}]
    result = solve(value)
    assert result['status'] == 'infeasible'
    assert any(r['code'] == 'team_shift' for r in result['rules'])
    assert not any(r['code'].startswith('gate_') for r in result['rules'])
    assert validate('ResourceSolveResult',result) == []

"""Independent invariants plus one reviewed corpus consumed by xUnit as well."""
import copy
import itertools
import json
import os
from pathlib import Path
from urllib.error import HTTPError
from urllib.request import Request, urlopen

import pytest

from app.contracts.models import SimulationRequest
from app.contracts.validation import CONTRACTS, validate
from app.optimization.staffing import solve
from app.simulation.engine import simulate
from app.simulation.provenance import fingerprint

CASES = json.loads((CONTRACTS / 'critical/v1/cases.json').read_text(encoding='utf-8'))


def payload(case):
    value = json.loads((CONTRACTS / case['fixture']).read_text(encoding='utf-8'))
    for mutation in case['mutations']:
        parts = mutation['path'].split('/')[1:]
        parent = value
        for key in parts[:-1]:
            parent = parent[int(key)] if isinstance(parent, list) else parent[key]
        parent[int(parts[-1]) if isinstance(parent, list) else parts[-1]] = copy.deepcopy(mutation['value'])
    return value


def post(base, path, value):
    request = Request(base + path, data=json.dumps(value).encode(), headers={'Content-Type': 'application/json'})
    try:
        response = urlopen(request, timeout=10)
    except HTTPError as error:
        response = error
    with response:
        return response.status, json.load(response)


@pytest.mark.parametrize('case', CASES, ids=lambda case: case['name'])
def test_reviewed_cases_agree_in_validator_and_both_real_HTTP_services(case):
    value = payload(case)
    assert bool(validate(case['definition'], value)) == (case['status'] != 200)
    # Missing endpoints fail instead of silently skipping cross-runtime coverage.
    responses = [post(os.environ[name].rstrip('/'), case['endpoint'], value)
                 for name in ('CONTRACT_API_URL', 'CONTRACT_PYTHON_URL')]
    assert responses[0] == responses[1]
    assert responses[0][0] == case['status']
    if case['status'] == 200:
        assert responses[0][1] == value
    else:
        assert responses[0][1]['errors']


@pytest.mark.parametrize('case', [c for c in CASES if c['definition'] == 'KpiSimulationRequest' and c['status'] == 200], ids=lambda c: c['name'])
@pytest.mark.parametrize('seed', [0, 42, 2147483647])
def test_seeded_simulation_conserves_every_passenger_at_every_snapshot(case, seed):
    wire = payload(case)
    wire['seed'] = seed
    if wire.get('passengerGeneration') is not None:
        wire['passengerGeneration']['seed'] = seed
    original = copy.deepcopy(wire)
    first = simulate(SimulationRequest.model_validate(wire)).model_dump(mode='json', by_alias=True)
    frames = []
    second = simulate(SimulationRequest.model_validate(wire), on_progress=frames.append).model_dump(mode='json', by_alias=True)
    assert first == second
    assert wire == original
    assert fingerprint(first, 'result') == fingerprint(second, 'result')
    report = first['processResult']
    assert first['kpiResult']['processedCount'] == case['expected']['processed']
    assert report['queuedCount'] == case['expected']['queued']
    for row in first['kpiResult']['snapshots']:
        counts = [row[key] for key in ('notArrivedPassengers', 'queuedPassengers', 'processingPassengers', 'walkingPassengers', 'atGatePassengers', 'unreachablePassengers')]
        assert min(counts) >= 0
        assert sum(counts) == row['populationCount']
        assert row['processedPassengers'] <= row['arrivedPassengers'] <= row['populationCount']
    assert first['kpiResult']['populationCount'] == first['kpiResult']['processedCount'] + first['kpiResult']['unprocessedCount']
    assert not validate('KpiSimulationResponse', first)


@pytest.mark.parametrize('case', [c for c in CASES if c['definition'] == 'StaffingSolveRequest'], ids=lambda c: c['name'])
def test_solver_matches_an_independent_exhaustive_oracle_including_infeasible_cases(case):
    wire = payload(case)
    original = copy.deepcopy(wire)
    def feasible(allocation):
        if sum(allocation) > wire['availableStaff']:
            return False
        for checkpoint, count in zip(wire['checkpoints'], allocation):
            if count > min(checkpoint['maximumStaff'], checkpoint['physicalLimit']): return False
            if checkpoint['closed'] and count: return False
            if checkpoint['requiredOpen'] and count < max(1, checkpoint['minimumStaff']): return False
            if count and count < checkpoint['minimumStaff']: return False
        return all(sum(count for checkpoint, count in zip(wire['checkpoints'], allocation) if checkpoint['terminalId'] == pool['terminalId']) <= pool['availableStaff'] for pool in wire['staffPools'])
    def score(allocation):
        return sum(max(0, checkpoint['demandMilli'] - count * checkpoint['capacityMilli']) + checkpoint['changeCost'] * abs(count - checkpoint['currentStaff']) for checkpoint, count in zip(wire['checkpoints'], allocation))
    candidates = [a for a in itertools.product(*(range(min(c['maximumStaff'], c['physicalLimit']) + 1) for c in wire['checkpoints'])) if feasible(a)]
    result = solve(wire)
    assert wire == original
    assert result['status'] == case['expected']['solverStatus']
    assert not validate('StaffingSolveResult', result)
    if not candidates:
        assert result['assignments'] == [] and result['score'] is None
    else:
        allocation = tuple(c['staffCount'] for c in result['assignments'])
        assert feasible(allocation)
        assert result['score'] == min(map(score, candidates)) == case['expected']['score']
        assert list(allocation) == case['expected']['allocation']

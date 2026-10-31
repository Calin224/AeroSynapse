"""The staffing solver contract and its safety checks agree across Python and .NET."""
import copy
import json
import os
import urllib.error
import urllib.request
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from app.contracts.validation import validate
from app.main import app
from app.optimization.staffing import solve

ROOT = Path(__file__).resolve().parents[1] / 'staffing/v1/examples'


@pytest.mark.parametrize('kind', ['request', 'result', 'feasible', 'large_capacity', 'duplicate', 'budget', 'capacity', 'physical', 'timeout'])
def test_staffing_contract_and_result_invariants_agree_across_services(kind):
    is_request = kind in ('request', 'duplicate', 'budget')
    value = json.loads((ROOT / ('staffing-request.json' if is_request else 'staffing-response.json')).read_text())
    if kind == 'duplicate': value['checkpoints'].append(copy.deepcopy(value['checkpoints'][0]))
    if kind == 'budget': value['availableStaff'] = 4
    if kind == 'capacity': value['assignments'][0]['estimatedCapacityMilli'] = 999999
    if kind == 'physical': value['constraints']['checkpoints'][0]['physicalLimit'] = 1
    if kind == 'timeout': value.update(status='timeout', solverStatus='UNKNOWN', timedOut=True)
    if kind == 'feasible': value.update(status='feasible', solverStatus='FEASIBLE', timedOut=True, bestBound=0)
    if kind == 'large_capacity':
        request = value['constraints']
        request['availableStaff'] = request['staffPools'][0]['availableStaff'] = 10000
        request['checkpoints'] = [request['checkpoints'][0] | {
            'currentStaff': 10000, 'minimumStaff': 10000, 'maximumStaff': 10000,
            'physicalLimit': 10000, 'requiredOpen': True, 'capacityMilli': 10**13}]
        value = solve(request)
    definition = 'StaffingSolveRequest' if is_request else 'StaffingSolveResult'
    errors = validate(definition, value)
    assert bool(errors) == (kind not in ('request', 'result', 'feasible', 'large_capacity'))
    path = '/contracts/v1/staffing/' + ('validate' if is_request else 'result/validate')
    with TestClient(app) as client:
        reply = client.post(path, json=value)
        assert reply.status_code == (422 if errors else 200)
        for base in (os.getenv('CONTRACT_API_URL'), os.getenv('CONTRACT_PYTHON_URL')):
            if not base: continue
            request = urllib.request.Request(base + path, data=json.dumps(value).encode(), headers={'Content-Type': 'application/json'})
            try: response = urllib.request.urlopen(request, timeout=10)
            except urllib.error.HTTPError as failure: response = failure
            with response:
                assert response.status == reply.status_code
                assert json.load(response) == reply.json()


@pytest.mark.parametrize('kind', ['request', 'result', 'duplicate', 'baseline', 'score', 'status', 'pool'])
def test_distinct_candidate_contracts_and_failures_agree_across_services(kind):
    is_request = kind == 'request'
    value = json.loads((ROOT / ('staffing-candidates-request.json' if is_request else 'staffing-candidates-response.json')).read_text())
    if kind == 'duplicate': value['candidates'][1] = copy.deepcopy(value['candidates'][0])
    if kind == 'baseline':
        for assignment, item in zip(value['candidates'][0]['assignments'], value['request']['constraints']['checkpoints']):
            assignment['staffCount'] = item['currentStaff']
    if kind == 'score': value['candidates'][0]['score'] += 1
    if kind == 'status': value['status'] = 'timeout'
    if kind == 'pool': value['request']['constraints']['staffPools'][0]['availableStaff'] = 0
    definition = 'StaffingCandidateSearchRequest' if is_request else 'StaffingCandidateSearchResult'
    errors = validate(definition, value)
    assert bool(errors) == (kind not in ('request', 'result'))
    path = '/contracts/v1/staffing/candidates/' + ('validate' if is_request else 'result/validate')
    with TestClient(app) as client:
        reply = client.post(path, json=value)
        assert reply.status_code == (422 if errors else 200)
        for base in (os.getenv('CONTRACT_API_URL'), os.getenv('CONTRACT_PYTHON_URL')):
            if not base: continue
            request = urllib.request.Request(base + path, data=json.dumps(value).encode(), headers={'Content-Type': 'application/json'})
            try: response = urllib.request.urlopen(request, timeout=10)
            except urllib.error.HTTPError as failure: response = failure
            with response:
                assert response.status == reply.status_code
                assert json.load(response) == reply.json()

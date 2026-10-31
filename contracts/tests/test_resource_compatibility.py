"""Shared resource constraints and solver-output validation agree across services."""
import copy
import json
import os
import urllib.error
import urllib.request

import pytest
from fastapi.testclient import TestClient

from app.contracts.validation import CONTRACTS, validate
from app.main import app
from app.optimization.resources import solve

ROOT = CONTRACTS / 'resources/v1/examples'


@pytest.mark.parametrize('kind', ['request','result','feasible','timeout','infeasible','unknown-gate','duplicate',
    'window','horizon','skills','forged-gate','forged-score','shared-person','forged-rule','advisory'])
def test_integrated_resource_contracts_agree(kind):
    is_request = kind in ('request','unknown-gate','duplicate','window','horizon')
    value = json.loads((ROOT / ('resource-request.json' if is_request else 'resource-response.json')).read_text())
    if kind == 'unknown-gate': value['resources']['requirements'][0]['compatibleGateIds'] = [value['scenarioId']]
    if kind == 'duplicate': value['resources']['workers'].append(copy.deepcopy(value['resources']['workers'][0]))
    if kind == 'window': value['resources']['workers'][0]['shifts'][0]['endMinute'] = 0
    if kind == 'horizon': value['flights'][0]['endMinute'] = 91
    if kind == 'feasible': value.update(status='feasible',solverStatus='FEASIBLE',timedOut=True,bestBound=0)
    if kind in ('timeout', 'infeasible', 'forged-rule'):
        request = value['constraints']
        if kind == 'timeout': request['timeLimitSeconds'] = 0
        else:
            for gate in request['resources']['gates']: gate['availability'] = []
        value = solve(request)
        if kind == 'forged-rule': value['rules'][0]['flightId'] = request['scenarioId']
    if kind == 'skills':
        for worker in value['constraints']['resources']['workers']: worker['skills'] = []
    if kind == 'forged-gate': value['assignments'][0]['gateId'] = value['constraints']['resources']['gates'][1]['gateId']
    if kind == 'forged-score': value['score'] = 0
    if kind == 'advisory': value['advisoryOnly'] = False
    if kind == 'shared-person':
        for flight in value['constraints']['flights']: flight.update(startMinute=10,endMinute=40)
        for gate in value['constraints']['resources']['gates']: gate['availability'] = [{'startMinute':0,'endMinute':90}]
        for team in value['constraints']['resources']['teams']: team['memberIds'] = value['constraints']['resources']['teams'][0]['memberIds'][:]
        for i, assignment in enumerate(value['assignments']):
            assignment['gateId'] = value['constraints']['resources']['gates'][i]['gateId']
            assignment['teamId'] = value['constraints']['resources']['teams'][i]['teamId']
    definition = 'ResourceSolveRequest' if is_request else 'ResourceSolveResult'
    errors = validate(definition,value)
    assert bool(errors) == (kind not in ('request','result','feasible','timeout','infeasible'))
    path = '/contracts/v1/resources/' + ('validate' if is_request else 'result/validate')
    with TestClient(app) as client:
        reply = client.post(path,json=value)
        assert reply.status_code == (422 if errors else 200)
        for base in (os.getenv('CONTRACT_API_URL'),os.getenv('CONTRACT_PYTHON_URL')):
            if not base: continue
            request = urllib.request.Request(base + path,data=json.dumps(value).encode(),headers={'Content-Type':'application/json'})
            try: response = urllib.request.urlopen(request,timeout=10)
            except urllib.error.HTTPError as failure: response = failure
            with response:
                assert response.status == reply.status_code
                assert json.load(response) == reply.json()

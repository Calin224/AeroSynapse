"""Shared navigation cases exercise both runtimes and the differential HTTP contracts."""
import copy
import json
import os
import urllib.error
import urllib.request
from pathlib import Path

import pytest
from fastapi.testclient import TestClient
from app.contracts.validation import schema, validate
from app.main import app

ROOT = Path(__file__).resolve().parents[1] / 'navigation/v1'
CASES = json.loads((ROOT / 'validation-cases.json').read_text(encoding='utf-8'))
PATHS = {'NavigationPreviewRequest':'navigation/validate','RoutedSimulationRequest':'navigation/simulation/validate',
         'NavigationResult':'navigation/result/validate','ProfiledSimulationRequest':'passengers/simulation/validate'}

@pytest.fixture(scope='module')
def client():
    with TestClient(app) as client: yield client

@pytest.mark.parametrize('case',CASES,ids=lambda case:case['name'])
def test_navigation_validation_is_shared(case,client):
    payload = json.loads((ROOT / 'examples' / (case['example'] + '.json')).read_text(encoding='utf-8'))
    for mutation in case['mutations']:
        parts = mutation['path'].split('/')[1:]; parent = payload
        for part in parts[:-1]: parent = parent[int(part)] if isinstance(parent,list) else parent[part]
        parent[int(parts[-1]) if isinstance(parent,list) else parts[-1]] = copy.deepcopy(mutation['value'])
    errors = validate(case['definition'],payload)
    assert sorted((item['field'],item['code']) for item in errors) == sorted((item['field'],item['code']) for item in case['errors'])
    if case['definition'] not in PATHS: return
    path = '/contracts/v1/' + PATHS[case['definition']]
    result = client.post(path,json=payload)
    assert result.status_code == (422 if errors else 200)
    for base in (os.getenv('CONTRACT_API_URL'),os.getenv('CONTRACT_PYTHON_URL')):
        if not base: continue
        request = urllib.request.Request(base+path,data=json.dumps(payload).encode(),headers={'Content-Type':'application/json'})
        try: reply = urllib.request.urlopen(request,timeout=10)
        except urllib.error.HTTPError as error: reply = error
        with reply:
            assert reply.status == result.status_code
            assert json.load(reply) == result.json()

def test_navigation_schema_exports_match(client):
    for definition in ('NavigationConfig','NavigationPreviewRequest','NavigationResult','RoutedSimulationRequest','RoutedSimulationResponse'):
        path = '/contracts/v1/schemas/' + definition
        expected = schema(definition)
        assert client.get(path).json() == expected
        for base in (os.getenv('CONTRACT_API_URL'),os.getenv('CONTRACT_PYTHON_URL')):
            if base:
                with urllib.request.urlopen(base+path,timeout=10) as reply: assert json.load(reply) == expected

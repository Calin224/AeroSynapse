"""Passenger generation uses one corpus for the Python and .NET validators and HTTP surfaces."""
import copy
import json
import os
import urllib.error
import urllib.request
from pathlib import Path

import pytest
from fastapi.testclient import TestClient
from jsonschema import Draft202012Validator

from app.contracts.validation import schema, validate
from app.main import app

ROOT = Path(__file__).resolve().parents[1] / 'passengers/v1'
CASES = json.loads((ROOT / 'validation-cases.json').read_text(encoding='utf-8'))
PATHS = {'PassengerGenerationRequest': 'passengers/validate', 'ProfiledSimulationRequest': 'passengers/simulation/validate'}


@pytest.fixture(scope='module')
def client():
    with TestClient(app) as client:
        yield client


@pytest.mark.parametrize('case', CASES, ids=lambda case: case['name'])
def test_shared_passenger_contract_case(case, client):
    payload = json.loads((ROOT / 'examples' / (case['example'] + '.json')).read_text(encoding='utf-8'))
    for mutation in case['mutations']:
        parts = mutation['path'].split('/')[1:]
        parent = payload
        for part in parts[:-1]:
            parent = parent[int(part)] if isinstance(parent, list) else parent[part]
        parent[int(parts[-1]) if isinstance(parent, list) else parts[-1]] = copy.deepcopy(mutation['value'])
    errors = validate(case['definition'], payload)
    assert sorted((error['field'], error['code']) for error in errors) == sorted((error['field'], error['code']) for error in case['errors'])
    expected_status = 422 if errors else 200
    path = '/contracts/v1/' + PATHS[case['definition']]
    result = client.post(path, json=payload)
    assert result.status_code == expected_status
    for base in (os.getenv('CONTRACT_API_URL'), os.getenv('CONTRACT_PYTHON_URL')):
        if not base:
            continue
        request = urllib.request.Request(base + path, data=json.dumps(payload).encode(), headers={'Content-Type': 'application/json'})
        try:
            reply = urllib.request.urlopen(request, timeout=10)
        except urllib.error.HTTPError as error:
            reply = error
        with reply:
            assert reply.status == expected_status
            assert json.load(reply) == result.json()


def test_passenger_schema_is_standalone_and_exports_the_same_definitions(client):
    document = json.loads((ROOT / 'contract.schema.json').read_text(encoding='utf-8'))
    Draft202012Validator.check_schema(document)
    example = json.loads((ROOT / 'examples/generation-request.json').read_text(encoding='utf-8'))
    assert not list(Draft202012Validator(document | {'$ref': '#/$defs/PassengerGenerationRequest'}).iter_errors(example))
    for definition in ('PassengerGenerationRequest', 'PassengerGenerationConfig', 'ProfiledSimulationRequest', 'ProfiledScenarioSnapshot'):
        path = '/contracts/v1/schemas/' + definition
        expected = schema(definition)
        assert client.get(path).json() == expected
        for base in (os.getenv('CONTRACT_API_URL'), os.getenv('CONTRACT_PYTHON_URL')):
            if base:
                with urllib.request.urlopen(base + path, timeout=10) as reply:
                    assert json.load(reply) == expected

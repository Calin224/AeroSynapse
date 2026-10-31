"""Real packaged .NET -> FastAPI -> PostgreSQL; no in-process engine replacement."""
import concurrent.futures
import copy
import http.cookiejar
import json
import os
import time
import urllib.error
import urllib.request
from pathlib import Path
from uuid import uuid4

import psycopg
import pytest


class Api:
    def __init__(self):
        self.base = os.environ['CRITICAL_API_URL'].rstrip('/')
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
        self.csrf = None
        self.csrf = self.call('/auth/csrf')[1]['requestToken']
        status, _, _ = self.call('/auth/login', 'POST', {'username': 'operator.demo', 'password': os.environ['CRITICAL_OPERATOR_PASSWORD']})
        assert status == 200, 'The isolated operator account must authenticate.'
        self.csrf = self.call('/auth/csrf')[1]['requestToken']

    def call(self, path, method='GET', value=None, headers=None):
        body = json.dumps(value).encode() if value is not None else None
        request = urllib.request.Request(self.base + path, data=body, method=method,
            headers={**({'Content-Type': 'application/json'} if body is not None else {}), **({'X-CSRF-TOKEN': self.csrf} if self.csrf else {}), **(headers or {})})
        try:
            response = self.opener.open(request, timeout=15)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            return response.status, json.load(response) if response.status != 204 else None, response.headers


@pytest.fixture
def scenario():
    api = Api()
    status, layout, _ = api.call('/scenes/60000000-0000-0000-0000-000000000001/layout-versions', 'POST')
    assert status == 201, layout
    value = json.loads(Path('backend/examples/scenario-request.json').read_text(encoding='utf-8'))
    value.update(code='critical-' + uuid4().hex, name='Critical deterministic synthetic fixture', layoutVersionId=layout['version']['id'])
    status, saved, _ = api.call('/scenarios', 'POST', value)
    assert status == 201, saved
    return api, saved, value


def wait(api, key):
    deadline = time.monotonic() + 45
    while time.monotonic() < deadline:
        status, receipt, _ = api.call('/simulations/' + key)
        assert status == 200
        if receipt['status'] in ('completed', 'failed'):
            assert receipt['status'] == 'completed', f"Run failed: {receipt.get('errorCode')}"
            return receipt
        time.sleep(0.15)
    pytest.fail('The clean-stack simulation did not finish within 45 seconds.')


def test_authenticated_idempotent_smoke_persists_real_engine_evidence_in_the_database(scenario):
    api, saved, _ = scenario
    scenario_id = saved['scenario']['id']
    key, trace = str(uuid4()), 'critical-' + uuid4().hex
    def launch():
        return api.call(f'/scenarios/{scenario_id}/simulations', 'POST', {'scenarioRevision': 1}, {'Idempotency-Key': key, 'X-Correlation-ID': trace})
    with concurrent.futures.ThreadPoolExecutor(max_workers=3) as pool:
        responses = list(pool.map(lambda _: launch(), range(3)))
    assert all(status == 202 for status, _, _ in responses)
    assert len({reply['runId'] for _, reply, _ in responses}) == 1
    completed = wait(api, key)
    run_id = completed['runId']
    with urllib.request.urlopen(os.environ['CRITICAL_ENGINE_URL'] + '/simulation/runs/' + run_id, timeout=10) as response:
        engine = json.load(response)
    assert engine['status'] == engine['result']['status'] == 'completed'
    assert engine['result']['runId'] == run_id
    assert completed['correlationId'] == trace
    status, stored, _ = api.call('/runs/' + run_id)
    assert status == 200 and stored['resultsValid'] is True
    with psycopg.connect(autocommit=True) as database:
        row = database.execute('SELECT "Status", "InputHash", "ResultHash", "EngineBuild", "InputSnapshotJson" FROM twin.runs WHERE "Id" = %s', (run_id,)).fetchone()
        assert row is not None and row[0] == 'completed' and row[4] is not None
        assert row[1] == engine['diagnostics']['inputHash']
        assert row[2] == engine['diagnostics']['resultHash']
        assert row[3] == engine['diagnostics']['engineBuild']
        assert database.execute('SELECT count(*) FROM twin.simulation_commands WHERE "ScenarioId" = %s', (scenario_id,)).fetchone()[0] == 1
        assert database.execute('SELECT count(*) FROM twin.run_metrics WHERE "RunId" = %s', (run_id,)).fetchone()[0] > 0
        assert database.execute('SELECT count(*) FROM twin.run_snapshots WHERE "RunId" = %s', (run_id,)).fetchone()[0] > 0
    metrics = api.call('/runs/' + run_id + '/metrics')[1]
    snapshots = api.call('/runs/' + run_id + '/snapshots')[1]
    assert metrics['isValid'] and metrics['items']
    assert snapshots['isValid'] and snapshots['items']
    status, replay, _ = api.call('/runs/' + run_id + '/replay')
    assert status == 200 and replay['runId'] == run_id
    assert launch()[1]['runId'] == run_id
    second_key = str(uuid4())
    status, _, _ = api.call(f'/scenarios/{scenario_id}/simulations', 'POST', {'scenarioRevision': 1}, {'Idempotency-Key': second_key})
    assert status == 202
    second_id = wait(api, second_key)['runId']
    second = api.call('/runs/' + second_id)[1]
    assert second_id != run_id
    assert {key: second['provenance'][key] for key in ('inputHash', 'resultHash', 'engineBuild')} == {
        key: stored['provenance'][key] for key in ('inputHash', 'resultHash', 'engineBuild')}


def test_invalid_scenario_and_unknown_command_JSON_are_rejected_without_new_rows(scenario):
    api, saved, value = scenario
    scenario_id = saved['scenario']['id']
    invalid = copy.deepcopy(value)
    invalid.update(code='critical-invalid-' + uuid4().hex)
    invalid['inputs']['staffAllocations'].append(copy.deepcopy(invalid['inputs']['staffAllocations'][0]))
    status, error, _ = api.call('/scenarios', 'POST', invalid)
    assert status in (400, 422) and error['errors']
    with psycopg.connect(autocommit=True) as database:
        assert database.execute('SELECT count(*) FROM twin.scenarios WHERE "Code" = %s', (invalid['code'],)).fetchone()[0] == 0
    key = str(uuid4())
    status, _, _ = api.call(f'/scenarios/{scenario_id}/simulations', 'POST', {'scenarioRevision': 1, 'unexpectedCommand': True}, {'Idempotency-Key': key})
    assert status == 400
    with psycopg.connect(autocommit=True) as database:
        assert database.execute('SELECT count(*) FROM twin.simulation_commands WHERE "Id" = %s', (key,)).fetchone()[0] == 0
    assert api.call(f'/scenarios/{scenario_id}/runs')[1] == []

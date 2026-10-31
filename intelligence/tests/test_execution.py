import json
import os
import threading
import time
from concurrent.futures import ThreadPoolExecutor
from uuid import uuid4

import psycopg
import pytest
from fastapi.testclient import TestClient
from psycopg import sql

from app.contracts.correlation import simulation
from app.contracts.models import SimulationRequest, SimulationResponse
from app.contracts.validation import CONTRACTS
from app.main import app
from app.simulation import executions
from app.simulation.engine import simulate


def payload():
    return json.loads((CONTRACTS / "v1/examples/simulation-request.json").read_text(encoding="utf-8")) | {"runId": str(uuid4())}


def test_seeded_engine_is_reproducible_and_preserves_passenger_and_time_bounds():
    request = SimulationRequest.model_validate(payload())
    first = simulate(request).model_dump(mode="json", by_alias=True)
    assert first == simulate(request).model_dump(mode="json", by_alias=True)
    response = SimulationResponse.model_validate(first)
    assert not simulation(request, response)
    assert sum(item.processed_passengers + item.queued_passengers for item in response.snapshots) <= request.passenger_count
    assert first["status"] == "completed"
    assert first["metrics"]


def test_zero_staff_and_very_low_arrival_rate_are_supported():
    wire = payload()
    wire["checkpoints"][0]["staffCount"] = 0
    result = simulate(SimulationRequest.model_validate(wire)).model_dump(mode="json", by_alias=True)
    assert result["metrics"][0]["processedPassengers"] == 0
    wire["arrivalRatePerMinute"] = 0.000001
    result = simulate(SimulationRequest.model_validate(wire)).model_dump(mode="json", by_alias=True)
    assert result["snapshots"][0]["queuedPassengers"] == 0


def test_correlation_header_is_propagated_and_invalid_identifiers_are_replaced(caplog):
    with TestClient(app) as client, caplog.at_level("INFO"):
        response = client.post("/simulation/validate", json={"duration_minutes": 10, "passenger_count": 20}, headers={"X-Correlation-ID": "us011-trace"})
        assert response.headers["X-Correlation-ID"] == "us011-trace"
        requests = [record for record in caplog.records if record.name == "aerosynapse.intelligence"
                    and "/simulation/validate" in record.getMessage()]
        assert requests and all(record.correlation_id == "us011-trace" for record in requests)
        assert client.get("/health", headers={"X-Correlation-ID": "bad identifier"}).headers["X-Correlation-ID"] != "bad identifier"


@pytest.fixture
def execution_client(monkeypatch):
    host = os.getenv("SIMULATION_TEST_POSTGRES_HOST")
    if not host:
        pytest.skip("Set SIMULATION_TEST_POSTGRES_HOST to run isolated PostgreSQL execution tests.")
    database = "us011_python_" + uuid4().hex
    settings = {"host": host, "port": int(os.getenv("SIMULATION_TEST_POSTGRES_PORT", "5432")),
                "user": os.getenv("SIMULATION_TEST_POSTGRES_USER", "aerosynapse"),
                "password": os.getenv("SIMULATION_TEST_POSTGRES_PASSWORD", "local-demo-only")}
    with psycopg.connect(dbname="postgres", autocommit=True, **settings) as admin:
        admin.execute(sql.SQL("CREATE DATABASE {}").format(sql.Identifier(database)))
        try:
            for key, value in settings.items():
                env_key = {"host": "POSTGRES_HOST", "port": "POSTGRES_PORT", "user": "POSTGRES_USER", "password": "POSTGRES_PASSWORD"}[key]
                monkeypatch.setenv(env_key, str(value))
            monkeypatch.setenv("POSTGRES_DB", database)
            with TestClient(app) as client:
                yield client
        finally:
            admin.execute(sql.SQL("DROP DATABASE {} WITH (FORCE)").format(sql.Identifier(database)))


def test_duplicate_command_and_restart_return_persisted_result_without_executing_again(execution_client, monkeypatch):
    wire = payload()
    original = execution_client.post("/simulation/runs", json=wire, headers={"X-Correlation-ID": "receipt-trace"})
    assert original.status_code == 202
    assert original.json()["status"] == "completed"
    assert original.json()['diagnostics']['inputHash']
    assert original.json()['diagnostics']['resultHash']
    assert original.json()['diagnostics']['engineMilliseconds'] >= 0
    monkeypatch.setattr(executions, "simulate", lambda _: pytest.fail("A persisted receipt must not be recomputed"))
    assert execution_client.post("/simulation/runs", json=wire).json() == original.json()
    with TestClient(app) as restarted:
        assert restarted.get("/simulation/runs/" + wire["runId"]).json() == original.json()
        assert restarted.post("/simulation/runs", json=wire).json() == original.json()
    changed = wire | {"seed": wire["seed"] + 1}
    assert execution_client.post("/simulation/runs", json=changed).status_code == 409


def test_concurrent_duplicate_and_lookup_during_lost_response_do_not_reexecute_or_block_health(execution_client, monkeypatch):
    wire = payload()
    entered, release = threading.Event(), threading.Event()
    calls = []

    def delayed(request, on_progress=None):
        calls.append(request.run_id)
        entered.set()
        assert release.wait(5)
        return simulate(request, on_progress=on_progress)

    monkeypatch.setattr(executions, "simulate", delayed)
    with ThreadPoolExecutor(max_workers=2) as pool:
        original = pool.submit(execution_client.post, "/simulation/runs", json=wire)
        try:
            assert entered.wait(5)
            duplicate = execution_client.post("/simulation/runs", json=wire)
            assert duplicate.json()["status"] == "running"
            assert execution_client.get("/simulation/runs/" + wire["runId"]).json()["status"] == "running"
            assert execution_client.get("/health").status_code == 200
        finally:
            release.set()
        assert original.result(timeout=5).json()["status"] == "completed"
    assert len(calls) == 1
    assert execution_client.get("/simulation/runs/" + wire["runId"]).json()["status"] == "completed"


def test_engine_failure_is_durable_and_terminal(execution_client, monkeypatch):
    wire = payload()

    def fail(_, on_progress=None):
        raise RuntimeError("private engine detail")

    monkeypatch.setattr(executions, "simulate", fail)
    reply = execution_client.post("/simulation/runs", json=wire).json()
    assert reply["status"] == "failed"
    assert reply["result"]["failure"]["code"] == "simulation_failed"
    assert "private engine detail" not in json.dumps(reply)
    assert reply['diagnostics']['resultHash'] is None
    assert reply['diagnostics']['engineMilliseconds'] >= 0
    monkeypatch.setattr(executions, "simulate", lambda _: pytest.fail("A failed receipt is terminal"))
    assert execution_client.post("/simulation/runs", json=wire).json() == reply


def test_progress_is_readable_before_the_POST_returns_and_survives_completion(execution_client, monkeypatch):
    wire = payload()
    entered, release = threading.Event(), threading.Event()

    def waiting(request, on_progress=None):
        def frame(value):
            on_progress(value)
            if value["simulationMinute"] == 1:
                entered.set()
                assert release.wait(5)
        return simulate(request, on_progress=frame)

    monkeypatch.setattr(executions, "simulate", waiting)
    with ThreadPoolExecutor(max_workers=1) as pool:
        pending = pool.submit(execution_client.post, "/simulation/runs", json=wire)
        try:
            assert entered.wait(5)
            progress = execution_client.get("/simulation/runs/" + wire["runId"]).json()
            assert progress["status"] == "running"
            assert progress["progress"]["simulationMinute"] == 1
            assert set(progress["progress"]["zones"][0]) == {"zoneId", "queuedPassengers", "processedPassengers"}
        finally:
            release.set()
        completed = pending.result(timeout=5).json()
    assert completed["status"] == "completed"
    assert completed["progress"]["simulationMinute"] == wire["durationMinutes"]


def test_live_forecast_uses_real_inflight_progress_before_simulation_completes(execution_client, monkeypatch):
    wire = json.loads((CONTRACTS / 'forecast/v1/examples/forecast-request.json').read_text())['simulation']
    wire['runId'] = str(uuid4())
    entered, release = threading.Event(), threading.Event()

    def waiting(request, on_progress=None):
        def frame(value):
            on_progress(value)
            if value['simulationMinute'] == 1:
                entered.set()
                assert release.wait(10)
        return simulate(request, on_progress=frame)

    monkeypatch.setattr(executions, 'simulate', waiting)
    with ThreadPoolExecutor(max_workers=1) as pool:
        pending = pool.submit(execution_client.post, '/simulation/runs', json=wire)
        try:
            assert entered.wait(5)
            observed = execution_client.get('/simulation/runs/' + wire['runId']).json()
            assert observed['status'] == 'running' and observed['result'] is None
            progress = observed['progress']
            assert progress['zones'][0]['notArrivedPassengers'] == wire['passengerCount']
            forecast = execution_client.post('/simulation/queue-forecast', json={
                'simulation': wire, 'observationSource': 'live_simulation',
                'options': {'fromMinute': progress['simulationMinute'], 'horizonMinutes': 15, 'queueThresholdPassengers': None},
                'backlogs': [{'checkpointId': zone['zoneId'], 'queuedPassengers': zone['queuedPassengers'],
                              'notArrivedPassengers': zone['notArrivedPassengers']} for zone in progress['zones']]})
            assert forecast.status_code == 200, forecast.text
            assert forecast.json()['observationSource'] == 'live_simulation' and forecast.json()['fromMinute'] == 1
            assert not pending.done()
        finally:
            release.set()
        assert pending.result(timeout=5).json()['status'] == 'completed'


def test_unreachable_execution_storage_maps_to_503(execution_client, monkeypatch):
    monkeypatch.setattr(executions, "connect", lambda: (_ for _ in ()).throw(psycopg.OperationalError("private connection detail")))
    response = execution_client.post("/simulation/runs", json=payload())
    assert response.status_code == 503
    assert "private connection detail" not in response.text


@pytest.mark.parametrize('failure_at', ['update', 'commit'])
def test_terminal_storage_retry_preserves_the_result_without_recomputing(execution_client, monkeypatch, failure_at):
    wire = payload()
    real_connect = executions.connect
    calls = []
    failures = []

    class Connection:
        def __enter__(self):
            self.inner = real_connect()
            self.inner.__enter__()
            self.terminal = False
            return self

        def execute(self, query, parameters=None):
            self.terminal = 'SET status = %s, result = %s' in query
            if self.terminal and failure_at == 'update' and len(failures) < 2:
                failures.append(failure_at)
                raise psycopg.OperationalError('temporary final-update failure')
            return self.inner.execute(query, parameters)

        def __exit__(self, *error):
            value = self.inner.__exit__(*error)
            if self.terminal and failure_at == 'commit' and not failures:
                failures.append(failure_at)
                raise psycopg.OperationalError('lost commit acknowledgement')
            return value

    def counted(request, on_progress=None):
        calls.append(request.run_id)
        return simulate(request, on_progress=on_progress)

    monkeypatch.setattr(executions, 'connect', Connection)
    monkeypatch.setattr(executions, 'simulate', counted)
    response = execution_client.post('/simulation/runs', json=wire)
    assert response.status_code == 202
    assert response.json()['status'] == 'completed'
    assert failures == [failure_at] * (2 if failure_at == 'update' else 1)
    assert len(calls) == 1
    assert execution_client.post('/simulation/runs', json=wire).json() == response.json()
    assert execution_client.get('/simulation/runs/' + wire['runId']).json() == response.json()
    assert len(calls) == 1


@pytest.mark.parametrize('lookup', ['get', 'post'])
def test_abandoned_uncontrolled_receipt_becomes_terminal_without_recomputation(execution_client, monkeypatch, lookup):
    wire = executions.EngineSimulationRequest.model_validate(payload()).model_dump(mode='json', by_alias=True)
    with executions.connect() as connection:
        connection.execute("""INSERT INTO intelligence.simulation_executions
            (run_id, request, correlation_id, status, created_at)
            VALUES (%s, %s, 'abandoned-test', 'running', now() - interval '2 hours')""",
            (wire['runId'], psycopg.types.json.Jsonb(wire)))
    monkeypatch.setattr(executions, 'simulate', lambda *args, **kwargs: pytest.fail('Receipt must not be recomputed'))
    response = execution_client.get('/simulation/runs/' + wire['runId']) if lookup == 'get' else execution_client.post('/simulation/runs', json=wire)
    assert response.status_code == (200 if lookup == 'get' else 202)
    assert response.json()['status'] == 'failed'
    assert response.json()['result']['failure']['code'] == 'simulation_result_unavailable'
    assert execution_client.post('/simulation/runs', json=wire).json() == response.json()


def test_controlled_process_reproduces_uncontrolled_math_and_durable_timeout(execution_client):
    wire = payload()
    ordinary = execution_client.post('/simulation/runs', json=wire).json()
    controlled_wire = wire | {'runId': str(uuid4())}
    controlled = execution_client.post('/simulation/runs', json=controlled_wire, headers={'X-Execution-Timeout-Ms': '10000'}).json()
    assert controlled['status'] == 'completed'
    assert ordinary['diagnostics']['inputHash'] == controlled['diagnostics']['inputHash']
    assert ordinary['diagnostics']['resultHash'] == controlled['diagnostics']['resultHash']
    expired_wire = wire | {'runId': str(uuid4())}
    started = time.monotonic()
    expired = execution_client.post('/simulation/runs', json=expired_wire, headers={'X-Execution-Timeout-Ms': '1'}).json()
    assert time.monotonic() - started < 4
    assert expired['status'] == 'failed' and expired['result']['failure']['code'] == 'simulation_timeout'
    assert not expired['result']['metrics'] and expired['diagnostics']['resultHash'] is None
    assert execution_client.post('/simulation/runs', json=expired_wire, headers={'X-Execution-Timeout-Ms': '10000'}).json() == expired


def test_cancellation_tombstone_prevents_a_later_controlled_launch(execution_client):
    wire = payload()
    assert execution_client.post('/simulation/runs/' + wire['runId'] + '/cancel').status_code == 200
    reply = execution_client.post('/simulation/runs', json=wire, headers={'X-Execution-Timeout-Ms': '10000'}).json()
    assert reply['status'] == 'failed' and reply['result']['failure']['code'] == 'simulation_cancelled'
    assert execution_client.get('/simulation/runs/' + wire['runId']).json() == reply


def test_active_controlled_computation_stops_after_durable_cancellation(execution_client):
    wire = payload() | {'engine': 'simpy-kpi-1.0', 'snapshotIntervalMinutes': 1, 'passengerCount': 100000,
                        'durationMinutes': 90, 'arrivalRatePerMinute': 100000 / 90}
    with ThreadPoolExecutor(max_workers=1) as pool:
        computing = pool.submit(execution_client.post, '/simulation/runs', json=wire, headers={'X-Execution-Timeout-Ms': '10000'})
        deadline = time.monotonic() + 5
        while time.monotonic() < deadline:
            if execution_client.get('/simulation/runs/' + wire['runId']).status_code == 200:
                break
            time.sleep(.02)
        else:
            pytest.fail('Controlled receipt was not created.')
        assert execution_client.post('/simulation/runs/' + wire['runId'] + '/cancel').status_code == 200
        reply = computing.result(timeout=5).json()
    assert reply['status'] == 'failed' and reply['result']['failure']['code'] == 'simulation_cancelled'
    assert reply['result']['metrics'] == []
    assert execution_client.get('/simulation/runs/' + wire['runId']).json() == reply


@pytest.mark.parametrize('limit', ['0', '-1', '3600001', 'not-a-number'])
def test_invalid_execution_control_is_rejected_before_computation(execution_client, limit):
    assert execution_client.post('/simulation/runs', json=payload(), headers={'X-Execution-Timeout-Ms': limit}).status_code == 422

"""PostgreSQL receipts ensure a run ID is executed at most once, across processes."""
import logging
import os
import time
from datetime import datetime, timedelta, timezone
from typing import Annotated
from uuid import UUID

import psycopg
from fastapi import APIRouter, Header, Request
from fastapi.responses import JSONResponse
from psycopg.rows import dict_row
from psycopg.types.json import Jsonb

from app.contracts.models import SimulationRequest
from app.simulation.engine import simulate
from app.simulation.provenance import engine_build, engine_version, fingerprint
from app.simulation.supervisor import failed, run_bounded

router = APIRouter(prefix="/simulation/runs", tags=["Simulation execution"])
logger = logging.getLogger("aerosynapse.intelligence.executions")
TERMINAL_RETRY_DELAYS = (0, 0.1, 0.3, 0.9)
# Legacy calls have no computation deadline. Bound recovery of an unconfirmed receipt
# by the same one-hour maximum supported by controlled execution requests.
UNCONTROLLED_RECOVERY_LIMIT = timedelta(hours=1)


class EngineSimulationRequest(SimulationRequest):
    """Distinct OpenAPI name preserves the existing legacy validation schema."""


def connect():
    if not os.getenv("POSTGRES_HOST"):
        raise psycopg.OperationalError("PostgreSQL execution storage is not configured.")
    return psycopg.connect(
        host=os.environ["POSTGRES_HOST"], port=int(os.getenv("POSTGRES_PORT", "5432")),
        dbname=os.getenv("POSTGRES_DB", "aerosynapse"), user=os.getenv("POSTGRES_USER", "aerosynapse"),
        password=os.getenv("POSTGRES_PASSWORD", ""), connect_timeout=3,
        options="-c statement_timeout=3000 -c lock_timeout=3000", row_factory=dict_row,
    )


def initialize_storage():
    with connect() as connection:
        # Concurrent service starts serialize DDL without locking execution requests.
        connection.execute("SELECT pg_advisory_xact_lock(11003011)")
        connection.execute("CREATE SCHEMA IF NOT EXISTS intelligence")
        connection.execute("""
            CREATE TABLE IF NOT EXISTS intelligence.simulation_executions (
                run_id uuid PRIMARY KEY, request jsonb NOT NULL, correlation_id varchar(64) NOT NULL,
                status varchar(16) NOT NULL CHECK (status IN ('running', 'completed', 'failed')),
                result jsonb, created_at timestamptz NOT NULL DEFAULT now(), finished_at timestamptz,
                CHECK ((status = 'running' AND result IS NULL AND finished_at IS NULL)
                    OR (status IN ('completed', 'failed') AND result IS NOT NULL AND finished_at IS NOT NULL))
            )
        """)
        connection.execute("ALTER TABLE intelligence.simulation_executions ADD COLUMN IF NOT EXISTS progress jsonb")
        connection.execute("ALTER TABLE intelligence.simulation_executions ADD COLUMN IF NOT EXISTS diagnostics jsonb")
        connection.execute("ALTER TABLE intelligence.simulation_executions ADD COLUMN IF NOT EXISTS deadline_at timestamptz")
        connection.execute("CREATE TABLE IF NOT EXISTS intelligence.simulation_cancellations (run_id uuid PRIMARY KEY, requested_at timestamptz NOT NULL DEFAULT now())")


def envelope(row):
    return {"runId": str(row["run_id"]), "status": row["status"], "result": row["result"], "progress": row["progress"], "diagnostics": row["diagnostics"]}


def reconcile_execution(connection, row):
    if row is not None and row['status'] == 'running':
        run_id = row['run_id']
        deadline = row['deadline_at'] or row['created_at'] + UNCONTROLLED_RECOVERY_LIMIT
        if deadline <= datetime.now(timezone.utc):
            code, message = ('simulation_timeout', 'Execution exceeded its wall-clock time limit.') if row['deadline_at'] else (
                'simulation_result_unavailable', 'Execution did not publish a durable result within the recovery limit.')
            updated = connection.execute("""UPDATE intelligence.simulation_executions SET status = 'failed', result = %s,
                finished_at = now() WHERE run_id = %s AND status = 'running' RETURNING *""",
                (Jsonb(failed(run_id, code, message)), run_id)).fetchone()
            row = updated or connection.execute('SELECT * FROM intelligence.simulation_executions WHERE run_id = %s', (run_id,)).fetchone()
    return row


def save_terminal(run_id, result, diagnostics, correlation_id):
    # Retry only persistence, never the computation. An unknown commit outcome is
    # safe: a terminal receipt wins, including cancellation or an expired deadline.
    for attempt, delay in enumerate(TERMINAL_RETRY_DELAYS):
        if delay:
            time.sleep(delay)
        try:
            with connect() as connection:
                row = connection.execute("""
                    UPDATE intelligence.simulation_executions SET status = %s, result = %s, diagnostics = %s, finished_at = now()
                    WHERE run_id = %s AND status = 'running' RETURNING *
                """, (result['status'], Jsonb(result), Jsonb(diagnostics), run_id)).fetchone()
                if row is None:
                    row = connection.execute('SELECT * FROM intelligence.simulation_executions WHERE run_id = %s', (run_id,)).fetchone()
            return row
        except psycopg.Error:
            if attempt == len(TERMINAL_RETRY_DELAYS) - 1:
                raise
            logger.warning('Final result storage will retry run=%s attempt=%s correlationId=%s', run_id, attempt + 1, correlation_id)


@router.post("", status_code=202)
def execute_simulation(payload: EngineSimulationRequest, request: Request,
                       timeout_ms: Annotated[int | None, Header(alias='X-Execution-Timeout-Ms', ge=1, le=3600000)] = None):
    correlation_id = request.state.correlation_id
    wire = payload.model_dump(mode="json", by_alias=True)
    input_hash = fingerprint(wire, 'input')
    try:
        with connect() as connection:
            inserted = connection.execute("""
                INSERT INTO intelligence.simulation_executions (run_id, request, correlation_id, status, deadline_at)
                VALUES (%s, %s, %s, 'running', %s) ON CONFLICT (run_id) DO NOTHING RETURNING run_id
            """, (payload.run_id, Jsonb(wire), correlation_id,
                  datetime.now(timezone.utc) + timedelta(milliseconds=timeout_ms) if timeout_ms else None)).fetchone()
            row = connection.execute("SELECT * FROM intelligence.simulation_executions WHERE run_id = %s", (payload.run_id,)).fetchone()
            if row["request"] != wire:
                return JSONResponse(status_code=409, content={"code": "engine_conflict", "message": "Run ID already identifies different inputs."})
            if not inserted:
                row = reconcile_execution(connection, row)
        if not inserted:
            logger.info("Simulation deduplicated run=%s correlationId=%s", payload.run_id, correlation_id)
            return envelope(row)
        # The receipt is committed before computation. A lost response or process restart
        # can never authorize recomputation of a running or terminal receipt.
        logger.info("Simulation running run=%s input_hash=%s engine=%s correlationId=%s", payload.run_id, input_hash, engine_version(payload), correlation_id)
        last_update = -float("inf")

        def save_progress(frame):
            nonlocal last_update
            now = time.monotonic()
            if now - last_update < 0.5 and frame["simulationMinute"] != payload.duration_minutes:
                return
            # Keep only aggregate queue counts; no passenger/staff identifiers or input data.
            safe = {"simulationMinute": frame["simulationMinute"], "zones": [
                {"zoneId": zone["zoneId"], "queuedPassengers": zone["queuedPassengers"], "processedPassengers": zone["processedPassengers"],
                 **({'notArrivedPassengers': zone['notArrivedPassengers']} if 'notArrivedPassengers' in zone else {})}
                for zone in frame["zones"]]}
            try:
                with connect() as connection:
                    connection.execute("UPDATE intelligence.simulation_executions SET progress = %s WHERE run_id = %s AND status = 'running'", (Jsonb(safe), payload.run_id))
                last_update = now
            except psycopg.Error:
                logger.warning("Progress storage unavailable run=%s correlationId=%s", payload.run_id, correlation_id)
        started = time.perf_counter()
        try:
            def cancelled():
                with connect() as connection:
                    return connection.execute('SELECT 1 FROM intelligence.simulation_cancellations WHERE run_id = %s', (payload.run_id,)).fetchone() is not None
            if timeout_ms is not None:
                remaining_ms = max(1, int((row['deadline_at'] - datetime.now(timezone.utc)).total_seconds() * 1000))
                result = run_bounded(wire, remaining_ms, cancelled, save_progress, correlation_id)
            elif cancelled():
                result = failed(payload.run_id, 'simulation_cancelled', 'Execution was cancelled before computation.')
            else:
                result = simulate(payload, on_progress=save_progress).model_dump(mode="json", by_alias=True)
        except Exception:
            logger.exception("Simulation failed run=%s correlationId=%s", payload.run_id, correlation_id)
            result = {"contractVersion": "1.0", "runId": str(payload.run_id), "status": "failed",
                      "simulatedMinutes": 0, "metrics": [], "snapshots": [],
                      "failure": {"code": "simulation_failed", "message": "The simulation engine could not complete this run."}}
        elapsed_ms = round((time.perf_counter() - started) * 1000, 3)
        diagnostics = {'inputHash': input_hash, 'resultHash': fingerprint(result, 'result') if result['status'] == 'completed' else None,
                       'engineVersion': engine_version(payload), 'engineBuild': engine_build(),
                       'engineMilliseconds': elapsed_ms}
        row = save_terminal(payload.run_id, result, diagnostics, correlation_id)
        logger.info("Simulation %s run=%s input_hash=%s result_hash=%s engine_ms=%s correlationId=%s",
                    row["status"], payload.run_id, input_hash, (row['diagnostics'] or {}).get('resultHash'), elapsed_ms, correlation_id)
        return envelope(row)
    except psycopg.Error:
        logger.exception("Execution storage unavailable run=%s correlationId=%s", payload.run_id, correlation_id)
        return JSONResponse(status_code=503, content={"code": "engine_storage_unavailable", "message": "Execution storage is unavailable."})


@router.get("/{run_id}")
def get_execution(run_id: UUID, request: Request):
    try:
        with connect() as connection:
            row = connection.execute("SELECT * FROM intelligence.simulation_executions WHERE run_id = %s", (run_id,)).fetchone()
            row = reconcile_execution(connection, row)
        logger.info("Simulation lookup run=%s correlationId=%s", run_id, request.state.correlation_id)
        if row is None:
            return JSONResponse(status_code=404, content={"code": "run_not_found"})
        return envelope(row)
    except psycopg.Error:
        return JSONResponse(status_code=503, content={"code": "engine_storage_unavailable", "message": "Execution storage is unavailable."})


@router.post('/{run_id}/cancel')
def cancel_execution(run_id: UUID, request: Request):
    try:
        with connect() as connection:
            row = connection.execute('SELECT * FROM intelligence.simulation_executions WHERE run_id = %s FOR UPDATE', (run_id,)).fetchone()
            if row is not None and row['status'] == 'running' and row['deadline_at'] is None:
                return JSONResponse(status_code=409, content={'code': 'execution_not_cancellable', 'message': 'Only controlled executions support cancellation.'})
            # A tombstone also handles cancellation arriving before the original POST receipt.
            connection.execute('INSERT INTO intelligence.simulation_cancellations (run_id) VALUES (%s) ON CONFLICT DO NOTHING', (run_id,))
            if row is not None and row['status'] == 'running':
                connection.execute("""UPDATE intelligence.simulation_executions SET status = 'failed', result = %s,
                    finished_at = now() WHERE run_id = %s AND status = 'running'""",
                    (Jsonb(failed(run_id, 'simulation_cancelled', 'Execution was cancelled.')), run_id))
        logger.info('Simulation cancellation requested run=%s correlationId=%s', run_id, request.state.correlation_id)
        return {'runId': str(run_id), 'cancellationRequested': True}
    except psycopg.Error:
        return JSONResponse(status_code=503, content={'code': 'engine_storage_unavailable', 'message': 'Execution storage is unavailable.'})

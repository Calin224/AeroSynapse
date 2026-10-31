"""A controlled run computes in a child process that can actually be terminated."""
import json
import logging
import multiprocessing
import tempfile
import time
from pathlib import Path

from app.contracts.models import SimulationRequest
from app.simulation.engine import simulate


def failed(run_id, code, message):
    return {'contractVersion': '1.0', 'runId': str(run_id), 'status': 'failed', 'simulatedMinutes': 0,
            'metrics': [], 'snapshots': [], 'failure': {'code': code, 'message': message}}


def _compute(wire, result_path, sender, correlation_id):
    try:
        request = SimulationRequest.model_validate(wire)
        result = simulate(request, on_progress=lambda frame: sender.send(frame)).model_dump(mode='json', by_alias=True)
    except Exception:
        logging.getLogger('aerosynapse.intelligence.batch').exception(
            'Controlled simulation failed run=%s correlationId=%s', wire['runId'], correlation_id)
        result = failed(wire['runId'], 'simulation_failed', 'The simulation engine could not complete this run.')
    Path(result_path).write_text(json.dumps(result, separators=(',', ':'), allow_nan=False), encoding='utf-8')
    sender.close()


def run_bounded(wire, timeout_ms, cancelled, on_progress, correlation_id, _worker=_compute):
    # Spawn avoids inheriting database connections, RNG state and service threads.
    context = multiprocessing.get_context('spawn')
    receiver, sender = context.Pipe(duplex=False)
    deadline = time.monotonic() + timeout_ms / 1000
    with tempfile.TemporaryDirectory(prefix='aerosynapse-run-') as directory:
        result_path = str(Path(directory) / 'result.json')
        process = context.Process(target=_worker, args=(wire, result_path, sender, correlation_id), daemon=True)
        try:
            if cancelled():
                return failed(wire['runId'], 'simulation_cancelled', 'Execution was cancelled before computation.')
            process.start()
            sender.close()
            next_check = 0.0
            while True:
                now = time.monotonic()
                if now >= deadline:
                    return failed(wire['runId'], 'simulation_timeout', 'Execution exceeded its wall-clock time limit.')
                if now >= next_check:
                    if cancelled():
                        return failed(wire['runId'], 'simulation_cancelled', 'Execution was cancelled.')
                    next_check = now + .2
                if receiver.poll(min(.05, max(0, deadline - now))):
                    try:
                        frame = receiver.recv()
                    except EOFError:
                        frame = None
                    if frame is not None:
                        on_progress(frame)
                if not process.is_alive():
                    # Drain final progress before reading the result. Large results never block a pipe receive.
                    while receiver.poll():
                        try:
                            on_progress(receiver.recv())
                        except EOFError:
                            break
                    if time.monotonic() >= deadline:
                        return failed(wire['runId'], 'simulation_timeout', 'Execution exceeded its wall-clock time limit.')
                    if process.exitcode != 0 or not Path(result_path).exists():
                        return failed(wire['runId'], 'simulation_worker_exited', 'The simulation process exited without a result.')
                    return json.loads(Path(result_path).read_text(encoding='utf-8'))
        finally:
            if process.pid is not None:
                if process.is_alive():
                    process.terminate()
                process.join(timeout=1)
                if process.is_alive():
                    process.kill()
                    process.join(timeout=1)
                process.close()
            receiver.close()
            sender.close()

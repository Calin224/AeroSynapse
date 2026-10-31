"""Reproducible demo benchmark. The configurable budget is provisional, not an SLA."""
import argparse
import json
import os
import platform
import statistics
import time
from datetime import datetime, timezone
from uuid import UUID

from app.contracts.models import SimulationRequest
from app.simulation.engine import simulate
from app.simulation.provenance import VERSION, engine_build, engine_version, fingerprint


def demo_request():
    return SimulationRequest.model_validate({
        'contractVersion': '1.0', 'runId': str(UUID(int=25001)), 'scenarioId': str(UUID(int=25002)),
        'scenarioRevision': 1, 'layoutVersionId': str(UUID(int=25003)), 'startsAtUtc': '2026-10-15T10:00:00Z',
        'timeZone': 'UTC', 'durationMinutes': 90, 'seed': 42, 'passengerCount': 1000,
        'arrivalRatePerMinute': 1000 / 90, 'engine': 'simpy-kpi-1.0', 'snapshotIntervalMinutes': 1,
        'checkpoints': [{'checkpointId': str(UUID(int=25100 + index)), 'serviceRatePerMinute': 3, 'staffCount': 2}
                        for index in range(3)]})


def measure(repetitions=5, budget_ms=5000):
    request = demo_request()
    simulate(request)  # Warm runtime/dependencies; cold start is outside this engine budget.
    samples, hashes = [], []
    for _ in range(repetitions):
        started = time.perf_counter()
        result = simulate(request).model_dump(mode='json', by_alias=True)
        samples.append(round((time.perf_counter() - started) * 1000, 3))
        if result['status'] != 'completed':
            raise RuntimeError('The benchmark demo did not complete.')
        hashes.append(fingerprint(result, 'result'))
    stable = len(set(hashes)) == 1
    return {'isSynthetic': True, 'measuredAtUtc': datetime.now(timezone.utc).isoformat(),
            'workload': {'passengers': 1000, 'simulatedMinutes': 90, 'checkpoints': 3, 'seed': 42, 'snapshotIntervalMinutes': 1},
            'scope': 'warm engine and result serialization; excludes HTTP, PostgreSQL and rendering',
            'budgetStatus': 'provisional; no team threshold supplied', 'budgetMilliseconds': budget_ms,
            'samplesMilliseconds': samples, 'medianMilliseconds': statistics.median(samples), 'maximumMilliseconds': max(samples),
            'withinBudget': max(samples) <= budget_ms, 'stableResults': stable, 'hashVersion': VERSION,
            'inputHash': fingerprint(request.model_dump(mode='json', by_alias=True), 'input'), 'resultHash': hashes[0],
            'engineVersion': engine_version(request), 'engineBuild': engine_build(), 'pythonVersion': platform.python_version(),
            'platform': platform.platform(), 'cpuCount': os.cpu_count()}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--repetitions', type=int, default=5)
    parser.add_argument('--budget-ms', type=float, default=float(os.getenv('SIMULATION_DEMO_BUDGET_MS', '5000')))
    args = parser.parse_args()
    if args.repetitions < 1 or args.repetitions > 100 or not 0 < args.budget_ms < float('inf'):
        parser.error('Use 1–100 repetitions and a finite positive budget.')
    report = measure(args.repetitions, args.budget_ms)
    print(json.dumps(report, indent=2, ensure_ascii=False))
    return 0 if report['withinBudget'] and report['stableResults'] else 1


if __name__ == '__main__':
    raise SystemExit(main())

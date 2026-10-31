import copy
import json
from concurrent.futures import ThreadPoolExecutor
from uuid import uuid4

from app.contracts.models import SimulationRequest
from app.contracts.validation import CONTRACTS
from app.simulation.benchmark import demo_request, measure
from app.simulation.engine import simulate
from app.simulation.provenance import engine_build, fingerprint


def test_shared_golden_vectors_match_dotnet_encoding():
    rows = json.loads((CONTRACTS / 'reproducibility/v1/fingerprints.json').read_text(encoding='utf-8'))
    for row in rows:
        assert fingerprint(row['value'], row['kind']) == row['expected'], row['name']
    assert rows[2]['expected'] == rows[3]['expected']


def test_distinct_runs_are_reproducible_concurrently_and_progress_does_not_change_math():
    wire = demo_request().model_dump(mode='json', by_alias=True)
    original = copy.deepcopy(wire)
    expected = fingerprint(simulate(SimulationRequest.model_validate(wire)).model_dump(mode='json', by_alias=True), 'result')

    def execute(_):
        frames = []
        request = SimulationRequest.model_validate(wire | {'runId': str(uuid4())})
        result = simulate(request, on_progress=frames.append).model_dump(mode='json', by_alias=True)
        assert frames[-1]['simulationMinute'] == 90
        assert fingerprint(request.model_dump(mode='json', by_alias=True), 'input') == fingerprint(wire, 'input')
        return fingerprint(result, 'result')

    with ThreadPoolExecutor(max_workers=4) as pool:
        assert list(pool.map(execute, range(4))) == [expected] * 4
    assert wire == original
    assert fingerprint(wire | {'seed': 43}, 'input') != fingerprint(wire, 'input')
    assert len(engine_build()) == 64


def test_benchmark_measures_the_declared_demo_and_reports_a_provisional_budget():
    report = measure(repetitions=2, budget_ms=30000)
    assert report['stableResults'] and report['withinBudget']
    assert report['workload']['passengers'] == 1000 and report['workload']['simulatedMinutes'] == 90
    assert len(report['samplesMilliseconds']) == 2
    assert all(value > 0 for value in report['samplesMilliseconds'])
    assert report['maximumMilliseconds'] == max(report['samplesMilliseconds'])
    assert 'provisional' in report['budgetStatus']

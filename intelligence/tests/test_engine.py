import json
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from app.main import app
from app.contracts import correlation
from app.contracts.models import SimulationRequest, SimulationResponse
from app.contracts.validation import validate
from app.contracts.validation import CONTRACTS
from app.simulation.engine import simulate

EXAMPLE = CONTRACTS / "v1/examples/simulation-request.json"


def payload():
    return json.loads(EXAMPLE.read_text(encoding="utf-8"))


def test_execution_repeats_exactly_and_correlates_with_the_captured_request():
    request = SimulationRequest.model_validate(payload())
    first = simulate(request)
    assert first == simulate(request)
    assert correlation.simulation(request, first) == []
    assert first.status == "completed"
    assert first.simulated_minutes == request.duration_minutes
    assert first.metrics[0].processed_passengers > 0
    assert validate("SimulationResponse", first.model_dump(mode="json", by_alias=True)) == []


def test_closed_checkpoint_queues_arrivals_and_processes_none():
    value = payload()
    value["checkpoints"][0]["staffCount"] = 0
    result = simulate(SimulationRequest.model_validate(value))
    assert result.metrics[0].processed_passengers == 0
    assert result.snapshots[0].queued_passengers > 0


def test_work_budget_returns_a_valid_failed_response():
    value = payload()
    value["passengerCount"] = 1_000_001
    result = simulate(SimulationRequest.model_validate(value))
    assert result.status == "failed"
    assert result.failure.code == "simulation_limit_exceeded"
    assert not result.metrics and not result.snapshots


def test_progress_frames_are_bounded_ordered_and_final_counts_match_the_result():
    frames = []
    request = SimulationRequest.model_validate(payload())
    result = simulate(request, on_progress=frames.append)
    assert 1 <= len(frames) <= 60
    assert [frame["simulationMinute"] for frame in frames] == sorted({frame["simulationMinute"] for frame in frames})
    assert frames[-1]["simulationMinute"] == request.duration_minutes
    for frame in frames:
        assert set(frame) == {"simulationMinute", "zones"}
        for zone in frame["zones"]:
            assert set(zone) == {"zoneId", "queuedPassengers", "processedPassengers"}
    assert frames[-1]["zones"][0]["processedPassengers"] == result.snapshots[0].processed_passengers


def test_actual_http_execution_uses_the_versioned_contract():
    with TestClient(app) as client:
        response = client.post("/simulation/v1/run", json=payload())
        assert response.status_code == 200
        result = SimulationResponse.model_validate(response.json())
        assert correlation.simulation(SimulationRequest.model_validate(payload()), result) == []


@pytest.mark.parametrize("body,media,status,code", [
    ('{}', 'application/json', 422, 'required'),
    ('{"seed":', 'application/json', 422, 'malformed_json'),
    ('{"seed":1,"seed":2}', 'application/json', 422, 'duplicate_field'),
    ('{}', 'text/plain', 415, 'unsupported_media_type'),
])
def test_execution_wire_errors_never_become_generic_500(body, media, status, code):
    with TestClient(app) as client:
        response = client.post("/simulation/v1/run", content=body, headers={"content-type": media})
        assert response.status_code == status
        assert any(item["code"] == code for item in response.json()["errors"])

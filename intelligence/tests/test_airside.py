import copy
import json
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

import pytest
from fastapi import FastAPI
from fastapi.testclient import TestClient
from jsonschema import Draft202012Validator
from pydantic import ValidationError

from app.airside.engine import simulate
from app.airside.models import AirsideRequest, AirsideResult, OptimizeResult
from app.airside.optimizer import optimize
from app.airside.routes import router


def payload(**changes):
    return {
        "contractVersion": "1.0", "synthetic": True, "seed": 53, "durationMinutes": 20,
        "serviceVariation": 0.0, "timeLimitSeconds": 3,
        "stands": [{"id": "S1", "available": [{"startMinute": 0, "endMinute": 240}]}],
        "teams": [{"id": "T1", "memberIds": ["M1"], "skills": ["handling", "pushback"], "standIds": ["S1"],
                   "available": [{"startMinute": 0, "endMinute": 240}]}],
        "vehicles": [{"id": "V1", "kind": "service", "standIds": ["S1"], "available": [{"startMinute": 0, "endMinute": 240}]},
                     {"id": "G1", "kind": "tug", "standIds": ["S1"], "available": [{"startMinute": 0, "endMinute": 240}]}],
        "turnarounds": [{"id": "F1", "standId": "S1", "arrivalMinute": 0, "scheduledPushbackMinute": 3,
                         "handlingMinutes": 3, "pushbackMinutes": 2},
                        {"id": "F2", "standId": "S1", "arrivalMinute": 0, "scheduledPushbackMinute": 6,
                         "handlingMinutes": 3, "pushbackMinutes": 2}],
        **changes,
    }


def run(body=None):
    result = simulate(AirsideRequest.model_validate(body or payload()))
    AirsideResult.model_validate(result)
    return result


def solve(body=None):
    result = optimize(AirsideRequest.model_validate(body or payload()))
    OptimizeResult.model_validate(result)
    return result


def assert_exclusive(result):
    bookings = {}
    for item in result["allocations"]:
        key = (item["resourceType"], item["resourceId"])
        for previous in bookings.get(key, []):
            assert item["endMinute"] <= previous["startMinute"] or previous["endMinute"] <= item["startMinute"]
        bookings.setdefault(key, []).append(item)


def two_stands(shared_member=False):
    body = payload()
    body["stands"].append({"id": "S2", "available": [{"startMinute": 0, "endMinute": 240}]})
    body["turnarounds"][1]["standId"] = "S2"
    body["teams"].append({**copy.deepcopy(body["teams"][0]), "id": "T2", "memberIds": ["M1" if shared_member else "M2"], "standIds": ["S2"]})
    body["teams"][0]["standIds"] = ["S1"]
    body["vehicles"][0]["standIds"] = ["S1", "S2"]
    body["vehicles"][1]["standIds"] = ["S1", "S2"]
    body["vehicles"].append({**copy.deepcopy(body["vehicles"][0]), "id": "V2"})
    return body


def test_fifo_analytical_end_to_end_and_kpis():
    result = run()
    first, second = result["turnarounds"]
    assert (first["handlingStartedMinute"], first["pushbackReadyMinute"], first["departedMinute"]) == (0, 3, 5)
    assert (second["standEnteredMinute"], second["pushbackReadyMinute"], second["departedMinute"]) == (5, 8, 10)
    assert result["kpis"] == {
        "turnaroundCount": 2, "departedCount": 2, "backlogCount": 0, "readyCount": 0,
        "meanTurnaroundMinutes": 7.5, "meanWaitingMinutes": 2.5,
        "meanReadinessDelayMinutes": 1.0, "lateCount": 1, "conflictCount": 1,
    }
    assert_exclusive(result)


def test_shared_member_prevents_incompatible_teams_from_overlapping():
    shared = run(two_stands(shared_member=True))
    independent = run(two_stands(shared_member=False))
    assert shared["kpis"]["meanTurnaroundMinutes"] > independent["kpis"]["meanTurnaroundMinutes"]
    assert shared["turnarounds"][1]["handlingStartedMinute"] == 5
    for result in (shared, independent, solve(two_stands(shared_member=True))["result"]):
        assert_exclusive(result)


def test_shared_tug_causes_readiness_wait_without_double_booking():
    result = run(two_stands())
    first, second = result["turnarounds"]
    assert first["handlingCompletedMinute"] == second["handlingCompletedMinute"] == 3
    assert (first["pushbackReadyMinute"], second["pushbackReadyMinute"]) == (3, 5)
    assert result["conflicts"][0]["stage"] == "pushback"
    assert_exclusive(result)


def test_team_availability_and_whole_service_windows_influence_results():
    body = payload()
    body["teams"][0]["available"] = [{"startMinute": 0, "endMinute": 2}, {"startMinute": 5, "endMinute": 20}]
    result = run(body)
    assert result["turnarounds"][0]["handlingStartedMinute"] == 5
    assert result["kpis"]["meanWaitingMinutes"] == 7.5
    assert_exclusive(result)


def test_complete_stand_occupancy_must_fit_window():
    body = payload()
    body["stands"][0]["available"] = [{"startMinute": 0, "endMinute": 4}, {"startMinute": 8, "endMinute": 20}]
    result = run(body)
    assert result["turnarounds"][0]["standEnteredMinute"] == 8
    stand_intervals = [item for item in result["allocations"] if item["resourceType"] == "stand"]
    assert all(8 <= item["startMinute"] < item["endMinute"] <= 20 for item in stand_intervals)
    assert_exclusive(result)


def test_horizon_completions_included_partial_waits_observed_no_future_claims():
    complete = run(payload(durationMinutes=10))
    assert complete["kpis"]["departedCount"] == complete["snapshots"][-1]["departedCount"] == 2
    partial = run(payload(durationMinutes=6))
    assert partial["kpis"]["departedCount"] == 1
    row = partial["turnarounds"][1]
    assert row["handlingStartedMinute"] == 5
    assert row["handlingCompletedMinute"] is row["departedMinute"] is None
    assert row["waitingMinutes"] == 5 and row["status"] == "handling"
    assert all(item["endMinute"] <= 6 for item in partial["allocations"])
    assert solve(payload(durationMinutes=6))["status"] == "infeasible"


def test_pending_unserviceable_flight_has_explicit_partial_backlog_diagnostic():
    body = payload(teams=[])
    result = run(body)
    assert result["kpis"]["backlogCount"] == 2
    assert all(row["waitingMinutes"] == 20 and row["status"] == "waiting" for row in result["turnarounds"])
    assert all(item["reason"] == "no_complete_available_path" for item in result["conflicts"])
    optimized = solve(body)
    assert optimized["status"] == "infeasible" and optimized["result"] is None


@pytest.mark.parametrize("category", ["stands", "teams", "vehicles"])
def test_empty_availability_means_unavailable(category):
    body = payload()
    body[category][0]["available"] = []
    assert run(body)["kpis"]["departedCount"] == 0
    assert solve(body)["status"] == "infeasible"


def test_optimizer_changes_sequence_to_reduce_readiness_delay():
    body = payload()
    body["turnarounds"][0].update(handlingMinutes=8, scheduledPushbackMinute=18)
    body["turnarounds"][1].update(handlingMinutes=2, scheduledPushbackMinute=2)
    baseline = run(body)
    optimized = solve(body)
    assert optimized["status"] == "optimal"
    assert optimized["result"]["kpis"]["meanReadinessDelayMinutes"] < baseline["kpis"]["meanReadinessDelayMinutes"]
    assert optimized["result"]["turnarounds"][1]["departedMinute"] == 4
    assert baseline["populationFingerprint"] == optimized["result"]["populationFingerprint"]
    assert_exclusive(optimized["result"])


def test_variation_is_reproducible_concurrent_and_does_not_resample_for_optimizer():
    body = payload(serviceVariation=0.5)
    baseline = run(body)
    with ThreadPoolExecutor(max_workers=4) as executor:
        assert all(value == baseline for value in executor.map(run, [body] * 4))
    optimized = solve(body)
    assert baseline["populationFingerprint"] == optimized["result"]["populationFingerprint"]
    assert [(row["handlingMinutes"], row["pushbackMinutes"]) for row in baseline["turnarounds"]] == [
        (row["handlingMinutes"], row["pushbackMinutes"]) for row in optimized["result"]["turnarounds"]]
    assert baseline["populationFingerprint"] != run({**body, "seed": 54})["populationFingerprint"]


def test_empty_population_has_zero_finite_metrics_and_optimal_empty_plan():
    result = run(payload(turnarounds=[]))
    assert all(value == 0 for value in result["kpis"].values())
    assert solve(payload(turnarounds=[]))["status"] == "optimal"


def test_maximum_duration_variation_is_valid_output_without_clipping():
    body = payload(seed=0, serviceVariation=0.5, durationMinutes=240)
    body["turnarounds"] = [{**body["turnarounds"][0], "handlingMinutes": 120, "pushbackMinutes": 60}]
    result = run(body)
    assert result["turnarounds"][0]["handlingMinutes"] == 161
    assert result["turnarounds"][0]["pushbackMinutes"] == 75
    assert solve(body)["status"] == "optimal"


@pytest.mark.parametrize("mutate", [
    lambda body: body.update(synthetic=False), lambda body: body.update(synthetic=1),
    lambda body: body.update(serviceVariation=float("nan")), lambda body: body.update(serviceVariation=float("inf")),
    lambda body: body.update(navigationCommand="go"), lambda body: body.update(durationMinutes=True),
    lambda body: body["stands"][0].update(id="real/airport"),
    lambda body: body["turnarounds"][0].update(standId="missing"),
    lambda body: body["teams"][0].update(memberIds=["M1", "M1"]),
    lambda body: body["teams"][0].update(standIds=["missing"]),
    lambda body: body["vehicles"][0].update(kind="operational"),
    lambda body: body["stands"][0].update(available=[{"startMinute": 5, "endMinute": 5}]),
    lambda body: body["turnarounds"][0].update(arrivalMinute=20),
    lambda body: body["turnarounds"].append(copy.deepcopy(body["turnarounds"][0])),
])
def test_strict_synthetic_bounded_input(mutate):
    body = payload()
    mutate(body)
    with pytest.raises(ValidationError):
        AirsideRequest.model_validate(body)


def test_versioned_schema_and_canonical_example_match_live_models():
    root = Path(__file__).resolve().parents[2] / "contracts/airside/v1"
    schema = json.loads((root / "contract.schema.json").read_text())
    for model in (AirsideRequest, AirsideResult, OptimizeResult):
        live = model.model_json_schema()
        assert schema["$defs"][model.__name__] == {key: value for key, value in live.items() if key != "$defs"}
        assert all(schema["$defs"][name] == value for name, value in live.get("$defs", {}).items())
    body = json.loads((root / "examples/request.json").read_text())
    result = run(body)
    assert result == json.loads((root / "examples/result.json").read_text())
    for name, value in (("AirsideRequest", body), ("AirsideResult", result), ("OptimizeResult", solve(body))):
        Draft202012Validator({**schema, "$ref": f"#/$defs/{name}"}).validate(value)


def test_http_feature_disabled_by_default_and_strict_synthetic_opt_in(monkeypatch):
    application = FastAPI()
    application.include_router(router)
    with TestClient(application) as client:
        monkeypatch.delenv("AIRSIDE_TWIN_ENABLED", raising=False)
        for route in ("run", "optimize"):
            assert client.post(f"/airside/v1/{route}", json=payload()).status_code == 404
        monkeypatch.setenv("AIRSIDE_TWIN_ENABLED", "true")
        for route in ("run", "optimize"):
            response = client.post(f"/airside/v1/{route}", json=payload())
            assert response.status_code == 200
            assert client.post(f"/airside/v1/{route}", json={**payload(), "synthetic": False}).status_code == 422
            assert client.post(f"/airside/v1/{route}", json={**payload(), "realPosition": [1, 2]}).status_code == 422
        varied = payload(seed=0, serviceVariation=0.5, durationMinutes=240)
        varied["turnarounds"] = [{**varied["turnarounds"][0], "handlingMinutes": 120, "pushbackMinutes": 60}]
        assert client.post("/airside/v1/run", json=varied).status_code == 200

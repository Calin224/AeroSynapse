import copy
import json
from pathlib import Path
from concurrent.futures import ThreadPoolExecutor

import pytest
from jsonschema import Draft202012Validator
from fastapi.testclient import TestClient
from pydantic import ValidationError

from app.baggage.engine import simulate
from app.baggage.models import BaggageRequest, BaggageResult
from app.main import app


def payload(**changes):
    return {
        "contractVersion": "1.0", "synthetic": True, "seed": 52,
        "durationMinutes": 10, "unitCount": 2, "arrivalWindowMinutes": 0.0,
        "loadingDeadlineMinute": 4.0, "serviceVariation": 0.0,
        "resources": {key: {"capacity": 1, "serviceMinutes": 1.0}
                      for key in ("checkIn", "beltA", "beltB", "sorting", "loading")},
        "incident": None, **changes,
    }


def run(body=None):
    return simulate(BaggageRequest.model_validate(body or payload()))


def test_fifo_end_to_end_matches_hand_calculated_two_bag_case():
    result = run()
    first, second = result["units"]
    assert [step["resourceId"] for step in first["stages"]] == ["checkIn", "beltA", "sorting", "loading"]
    assert [step["resourceId"] for step in second["stages"]] == ["checkIn", "beltB", "sorting", "loading"]
    assert (first["loadedMinute"], second["loadedMinute"]) == (4, 5)
    assert result["kpis"] == {
        "unitCount": 2, "loadedCount": 2, "backlogCount": 0,
        "throughputPerMinute": 0.2, "totalWaitingMinutes": 1,
        "meanWaitingMinutes": 0.5, "totalDelayMinutes": 1,
        "meanDelayMinutes": 0.5, "lateCount": 1, "divertedCount": 0,
    }
    assert result["resources"][0]["peakQueue"] == 1
    assert all(resource["peakBusy"] <= resource["capacity"] for resource in result["resources"])


def test_versioned_schema_and_analytical_examples_match_the_live_models():
    root = Path(__file__).resolve().parents[2] / "contracts/baggage/v1"
    schema = json.loads((root / "contract.schema.json").read_text())
    for model in (BaggageRequest, BaggageResult):
        live = model.model_json_schema()
        assert schema["$defs"][model.__name__] == {key: value for key, value in live.items() if key != "$defs"}
        assert all(schema["$defs"][name] == value for name, value in live["$defs"].items())
    body = json.loads((root / "examples/request.json").read_text())
    result = run(body)
    BaggageResult.model_validate(result)
    for name, value in (("BaggageRequest", body), ("BaggageResult", result)):
        Draft202012Validator({**schema, "$ref": f"#/$defs/{name}"}).validate(value)
    assert result == json.loads((root / "examples/result.json").read_text())


def test_completions_at_horizon_are_included_and_empty_population_is_finite():
    result = run(payload(durationMinutes=5))
    assert result["kpis"]["loadedCount"] == result["snapshots"][-1]["loadedCount"] == 2
    empty = run(payload(unitCount=0))
    assert all(value == 0 for value in empty["kpis"].values())
    assert all(resource["utilization"] == 0 for resource in empty["resources"])
    assert len(empty["snapshots"]) == 11


def test_partial_waits_and_delays_are_observed_without_predicting_completion():
    body = payload(durationMinutes=3, loadingDeadlineMinute=1.0)
    body["resources"]["checkIn"]["serviceMinutes"] = 10.0
    result = run(body)
    assert result["kpis"]["loadedCount"] == 0
    assert result["kpis"]["backlogCount"] == 2
    assert result["kpis"]["totalWaitingMinutes"] == 3
    assert result["kpis"]["totalDelayMinutes"] == 4
    assert all(unit["loadedMinute"] is None for unit in result["units"])
    assert result["resources"][0]["utilization"] == 1


def test_outage_pauses_in_service_work_and_preserves_it_on_recovery():
    body = payload(durationMinutes=15)
    body["incident"] = {"belt": "beltA", "startMinute": 1.5, "durationMinutes": 4.0, "divert": False}
    result = run(body)
    trace = result["units"][0]["stages"][1]
    assert trace["workSegments"] == [{"startMinute": 1, "endMinute": 1.5}, {"startMinute": 5.5, "endMinute": 6}]
    assert result["units"][0]["loadedMinute"] == 8
    assert result["snapshots"][2]["resources"][1]["stopped"] is True
    assert result["snapshots"][6]["resources"][1]["stopped"] is False
    assert result["resources"][1]["utilization"] == pytest.approx(1/15)


def test_outage_backlog_and_diversion_reconcile_every_bag():
    body = payload(unitCount=20, durationMinutes=10, loadingDeadlineMinute=8.0)
    body["resources"]["checkIn"] = {"capacity": 20, "serviceMinutes": 0.1}
    body["resources"]["sorting"] = body["resources"]["loading"] = {"capacity": 20, "serviceMinutes": 0.1}
    baseline = run(body)
    body["incident"] = {"belt": "beltA", "startMinute": 0.0, "durationMinutes": 20.0, "divert": False}
    stopped = run(body)
    body["incident"]["divert"] = True
    diverted = run(body)
    assert stopped["kpis"]["backlogCount"] > baseline["kpis"]["backlogCount"]
    assert diverted["kpis"]["divertedCount"] == 10
    assert diverted["resources"][1]["peakBusy"] == 0
    assert diverted["resources"][2]["peakQueue"] > stopped["resources"][2]["peakQueue"]
    assert baseline["populationFingerprint"] == stopped["populationFingerprint"] == diverted["populationFingerprint"]
    for result in (baseline, stopped, diverted):
        assert result["kpis"]["loadedCount"] + result["kpis"]["backlogCount"] == 20
        for frame in result["snapshots"]:
            arrived = sum(unit["arrivalMinute"] <= frame["minute"] for unit in result["units"])
            assert frame["loadedCount"] + sum(resource["queued"] + resource["inService"] for resource in frame["resources"]) == arrived
            assert all(resource["inService"] <= resource["capacity"] for resource in frame["resources"])


def test_extra_loading_capacity_improves_throughput_with_identical_population():
    body = payload(unitCount=40, durationMinutes=10, loadingDeadlineMinute=8.0)
    for key in ("checkIn", "beltA", "beltB", "sorting"):
        body["resources"][key] = {"capacity": 20, "serviceMinutes": 0.1}
    baseline = run(body)
    body["resources"]["loading"]["capacity"] = 8
    candidate = run(body)
    assert candidate["populationFingerprint"] == baseline["populationFingerprint"]
    assert candidate["kpis"]["throughputPerMinute"] > baseline["kpis"]["throughputPerMinute"]
    assert candidate["kpis"]["meanWaitingMinutes"] < baseline["kpis"]["meanWaitingMinutes"]
    assert candidate["kpis"]["meanDelayMinutes"] < baseline["kpis"]["meanDelayMinutes"]


def test_random_inputs_are_repeatable_under_parallel_runs_and_change_with_seed():
    body = payload(unitCount=100, arrivalWindowMinutes=5.0, serviceVariation=0.2)
    before = copy.deepcopy(body)
    expected = run(body)
    with ThreadPoolExecutor(max_workers=3) as pool:
        assert all(result == expected for result in pool.map(run, [body] * 3))
    assert body == before
    assert run({**body, "seed": 53})["populationFingerprint"] != expected["populationFingerprint"]


@pytest.mark.parametrize("change", [
    {"synthetic": False}, {"synthetic": "true"}, {"synthetic": 1}, {"unitCount": 5001}, {"unitCount": True},
    {"unitCount": -1}, {"seed": -1}, {"durationMinutes": 241}, {"durationMinutes": 1.2},
    {"arrivalWindowMinutes": 11.0}, {"loadingDeadlineMinute": 11.0},
    {"serviceVariation": float("nan")}, {"arrivalWindowMinutes": float("inf")},
    {"pnr": "REAL-123"}, {"units": [{"tag": "external-bag"}]},
    {"incident": {"belt": "unknown", "startMinute": 0.0, "durationMinutes": 1.0}},
    {"incident": {"belt": "beltA", "startMinute": 10.0, "durationMinutes": 1.0}},
])
def test_strict_synthetic_contract_rejects_invalid_inputs(change):
    with pytest.raises(ValidationError):
        BaggageRequest.model_validate(payload(**change))


@pytest.mark.parametrize("change", [{"capacity": 0}, {"capacity": 33}, {"serviceMinutes": 0.0}, {"serviceMinutes": float("inf")}])
def test_resources_have_finite_positive_bounded_capacity_and_duration(change):
    body = payload()
    body["resources"]["beltA"].update(change)
    with pytest.raises(ValidationError):
        BaggageRequest.model_validate(body)


def test_http_feature_is_off_by_default_and_security_contract_stays_available(monkeypatch):
    monkeypatch.delenv("BAGGAGE_TWIN_ENABLED", raising=False)
    with TestClient(app) as client:
        assert client.post("/baggage/v1/run", json=payload()).status_code == 404
        assert client.get("/health").status_code == 200
        assert client.post("/simulation/validate", json={"duration_minutes": 5, "passenger_count": 10}).status_code == 200
        monkeypatch.setenv("BAGGAGE_TWIN_ENABLED", "true")
        response = client.post("/baggage/v1/run", json=payload())
        assert response.status_code == 200
        assert response.json() == run()
        invalid = client.post("/baggage/v1/run", json=payload(synthetic=False))
        assert invalid.status_code == 422
        assert any(error["field"] == "synthetic" for error in invalid.json()["errors"])

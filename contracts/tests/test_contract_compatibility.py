"""One fixture corpus checks schema rules, DTOs, and both real HTTP services."""
import copy
import hashlib
import json
import os
import urllib.error
import urllib.request
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from app.main import app
from app.contracts import correlation
from app.contracts.models import (ScenarioSnapshot, ZoneSnapshot, SimulationRequest, SimulationResult,
                                  SimulationResponse, OptimizationRequest, OptimizationResult, OptimizationResponse,
                                  ValidationErrorResponse)
from app.contracts.validation import error, response, validate, schema

ROOT = Path(__file__).resolve().parents[1] / "v1"
CASES = json.loads((ROOT / "compatibility-cases.json").read_text(encoding="utf-8"))
CASES += json.loads((ROOT / "regression-cases.json").read_text(encoding="utf-8"))
CASES += json.loads((ROOT / "us009-cases.json").read_text(encoding="utf-8"))
PATHS = {"SimulationRequest": "simulation/validate", "OptimizationRequest": "optimization/validate",
         "ScenarioSnapshot": "scenario/validate", "ZoneSnapshot": "zone/validate",
         "SimulationResponse": "simulation/response/validate", "OptimizationResponse": "optimization/response/validate",
         "SimulationResult": "simulation/result/validate", "OptimizationResult": "optimization/result/validate"}
MODELS = {"SimulationRequest": SimulationRequest, "OptimizationRequest": OptimizationRequest,
          "ScenarioSnapshot": ScenarioSnapshot, "ZoneSnapshot": ZoneSnapshot,
          "SimulationResponse": SimulationResponse, "OptimizationResponse": OptimizationResponse,
          "SimulationResult": SimulationResult, "OptimizationResult": OptimizationResult}


def example(name):
    return json.loads((ROOT / "examples" / (name + ".json")).read_text(encoding="utf-8"))


def payload_for(case):
    payload = example(case["example"])
    for mutation in case["mutations"]:
        parts = mutation["path"].split("/")[1:]
        parent = payload
        for part in parts[:-1]:
            parent = parent[int(part)] if isinstance(parent, list) else parent[part]
        key = int(parts[-1]) if isinstance(parent, list) else parts[-1]
        if mutation["op"] == "remove":
            del parent[key]
        else:
            parent[key] = copy.deepcopy(mutation["value"])
    return payload


@pytest.mark.parametrize("case", CASES, ids=lambda case: case["name"])
def test_shared_compatibility_case(case):
    payload = payload_for(case)
    errors = validate(case["definition"], payload)
    if case["status"] == 200:
        assert errors == []
        model = MODELS[case["definition"]].model_validate(payload)
        serialized = model.model_dump(mode="json", by_alias=True)
        assert validate(case["definition"], serialized) == []
        assert MODELS[case["definition"]].model_validate(serialized) == model
    else:
        assert errors
        with pytest.raises(ValueError):
            MODELS[case["definition"]].model_validate(payload)
        for expected in case["errors"]:
            assert any(item["field"] == expected["field"] and item["code"] == expected["code"] for item in errors)
        assert validate("ValidationError", response(errors)) == []


@pytest.fixture(scope="module")
def client():
    with TestClient(app) as value:
        yield value


@pytest.mark.parametrize("case", CASES, ids=lambda case: case["name"])
def test_fastapi_HTTP_contract(case, client):
    payload = payload_for(case)
    result = client.post("/contracts/v1/" + PATHS[case["definition"]], json=payload)
    assert result.status_code == case["status"]
    expected = payload if result.status_code == 200 else response(validate(case["definition"], payload))
    assert result.json() == expected


def send(base, path, body, content_type="application/json"):
    request = urllib.request.Request(base.rstrip("/") + path, data=body.encode("utf-8") if isinstance(body, str) else body, headers={"Content-Type": content_type})
    try:
        with urllib.request.urlopen(request, timeout=10) as result:
            return result.status, json.load(result)
    except urllib.error.HTTPError as result:
        return result.code, json.load(result)


SERVICES = (os.getenv("CONTRACT_API_URL"), os.getenv("CONTRACT_PYTHON_URL"))


@pytest.mark.skipif(not all(SERVICES), reason="Set both CONTRACT_API_URL and CONTRACT_PYTHON_URL for differential HTTP tests.")
@pytest.mark.parametrize("case", CASES, ids=lambda case: case["name"])
def test_dotnet_and_fastapi_accept_identical_JSON_and_errors(case):
    payload = payload_for(case)
    path = "/contracts/v1/" + PATHS[case["definition"]]
    actual = [send(base, path, json.dumps(payload)) for base in SERVICES]
    assert actual[0] == actual[1]
    assert actual[0][0] == case["status"]


@pytest.mark.parametrize("body,content_type,status,code", [
    ('{"durationMinutes":', "application/json", 422, "malformed_json"),
    ('{"seed":1,"seed":2}', "application/json", 422, "duplicate_field"),
    ('{"checkpoints":[{"staffCount":1,"staffCount":2}]}', "application/json", 422, "duplicate_field"),
    ('{"seed":NaN}', "application/json", 422, "malformed_json"),
    ('{}', "text/plain", 415, "unsupported_media_type"),
    ('null', "application/json", 422, "invalid_type"),
    ('[]', "application/json", 422, "invalid_type"),
    ('', "application/json", 422, "malformed_json"),
])
def test_wire_errors_are_controlled_and_match_across_services(client, body, content_type, status, code):
    path = "/contracts/v1/simulation/validate"
    actual = client.post(path, content=body, headers={"Content-Type": content_type})
    assert actual.status_code == status
    assert any(item["code"] == code for item in actual.json()["errors"])
    assert "private-value" not in actual.text
    if all(SERVICES):
        assert send(SERVICES[0], path, body, content_type) == send(SERVICES[1], path, body, content_type) == (status, actual.json())


def test_v1_schema_is_frozen():
    text = (ROOT / "contract.schema.json").read_text(encoding="utf-8")
    assert hashlib.sha256(text.encode("utf-8")).hexdigest() == "b5a89bc975afec4756d362f4bd511ec38a58c97ac115dfd7934df9b3481f2099"


def test_existing_v1_definitions_are_unchanged_by_additive_contracts():
    text = (ROOT / "baseline.schema.json").read_text(encoding="utf-8")
    assert hashlib.sha256(text.encode("utf-8")).hexdigest() == "8ec8e50de70bf688a7b0af76319d249b507a0e88c68074a10e417d32d82f1f0c"
    for name, definition in json.loads(text)["$defs"].items():
        assert schema(name)["$defs"][name] == definition


def test_scenario_snapshot_builds_the_shared_request_without_live_data():
    from uuid import UUID
    snapshot = ScenarioSnapshot.model_validate(example("scenario-snapshot"))
    expected = example("simulation-request")
    request = snapshot.to_simulation_request(UUID(expected["runId"]))
    assert request.model_dump(mode="json", by_alias=True) == expected
    with pytest.raises(ValueError):
        snapshot.to_simulation_request(UUID(int=0))


def test_validation_error_example_round_trips():
    payload = example("validation-error")
    result = ValidationErrorResponse.model_validate(payload)
    assert result.model_dump(mode="json", by_alias=True) == payload


def test_openapi_exposes_required_fields_and_shared_error_schema(client):
    document = client.get("/openapi.json").json()
    for definition, path in PATHS.items():
        operation = document["paths"]["/contracts/v1/" + path]["post"]
        assert operation["requestBody"]["required"] is True
        assert operation["responses"]["422"]["content"]["application/json"]["schema"]["$ref"].endswith("/ContractValidationError")
        model = document["components"]["schemas"]["Contract" + definition]
        assert "contractVersion" in model["required"]
        assert model["additionalProperties"] is False
    assert document["components"]["schemas"]["SimulationRequest"]["required"] == ["duration_minutes", "passenger_count"]


def test_shared_openapi_is_current_and_identical_on_both_services(client):
    import importlib.util
    spec = importlib.util.spec_from_file_location("export_openapi", ROOT.parent / "export_openapi.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    expected = module.document()
    path = "/contracts/v1/openapi.json"
    assert client.get(path).json() == expected
    assert {"/contracts/v1/" + path for path in PATHS.values()} == set(expected["paths"])
    for definition, path_suffix in PATHS.items():
        operation = expected["paths"]["/contracts/v1/" + path_suffix]["post"]
        assert operation["requestBody"]["content"]["application/json"]["schema"]["$ref"] == "#/components/schemas/Contract" + definition
    if all(SERVICES):
        assert [json.load(urllib.request.urlopen(base + path, timeout=10)) for base in SERVICES] == [expected] * 2


@pytest.mark.parametrize("definition", [*PATHS, "ValidationError"])
def test_schema_export_matches_source_and_dotnet(definition, client):
    path = "/contracts/v1/schemas/" + definition
    assert client.get(path).json() == schema(definition)
    if all(SERVICES):
        documents = [json.load(urllib.request.urlopen(base + path, timeout=10)) for base in SERVICES]
        assert documents[0] == documents[1] == schema(definition)


def test_mathematical_integer_and_rate_boundaries_are_portable(client):
    payload = example("simulation-request")
    payload["durationMinutes"] = 60.0
    payload["seed"] = 42.0
    payload["arrivalRatePerMinute"] = 0.000001
    payload["checkpoints"][0]["staffCount"] = 0
    text = json.dumps(payload).replace('60.0', '6e1')
    path = "/contracts/v1/simulation/validate"
    assert client.post(path, content=text, headers={"Content-Type": "application/json"}).json() == payload
    assert SimulationRequest.model_validate(payload).duration_minutes == 60
    if all(SERVICES):
        assert [send(base, path, text) for base in SERVICES] == [(200, payload)] * 2


def test_correlation_checks_every_candidate_zone_and_time():
    request = SimulationRequest.model_validate(example("simulation-request"))
    result = SimulationResult.model_validate(example("simulation-result"))
    assert correlation.simulation(request, result) == []
    changed = result.model_copy(update={"run_id": request.scenario_id})
    assert correlation.simulation(request, changed) == [error("/runId", "invalid_result")]
    optimization = OptimizationRequest.model_validate(example("optimization-request"))
    comparison = OptimizationResult.model_validate(example("optimization-result"))
    assert correlation.optimization(optimization, comparison) == []
    assert correlation.optimization(optimization, comparison.model_copy(update={"candidates": []})) == [error("/candidates", "invalid_result")]
    response_model = SimulationResponse.model_validate(example("simulation-response"))
    assert correlation.simulation(request, response_model) == []
    assert error("/simulatedMinutes", "invalid_result") in correlation.simulation(request, response_model.model_copy(update={"simulated_minutes": 61}))
    assert error("/metrics/0/recordedAtUtc", "invalid_result") in correlation.simulation(request, response_model.model_copy(update={"simulated_minutes": 20}))
    assert error("/snapshots/0/elapsedMinutes", "invalid_result") in correlation.simulation(request, response_model.model_copy(update={"simulated_minutes": 20}))
    from datetime import datetime, timezone
    far_future = request.model_copy(update={"starts_at_utc": datetime(9999, 12, 31, 22, tzinfo=timezone.utc)})
    assert error("/simulatedMinutes", "invalid_result") in correlation.simulation(far_future, response_model.model_copy(update={"simulated_minutes": 10080}))
    assert correlation.optimization(optimization, OptimizationResponse.model_validate(example("optimization-response"))) == []

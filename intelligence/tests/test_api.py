import json
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from app.main import app

EXAMPLES = Path(__file__).resolve().parents[1] / "examples"


@pytest.fixture
def client():
    with TestClient(app) as test_client:
        yield test_client


def test_health(client):
    response = client.get("/health")

    assert response.status_code == 200
    assert response.json() == {"status": "Healthy"}


@pytest.mark.parametrize(("method", "path"), [
    ("POST", "/evacuation/v1/run"),
    ("POST", "/contracts/v1/evacuation/validate"),
    ("GET", "/contracts/v1/schemas/EvacuationRequest"),
])
def test_retired_exploration_endpoints_are_not_exposed(client, method, path):
    assert client.request(method, path).status_code == 404


def test_openapi_and_docs_are_available(client):
    response = client.get("/openapi.json")

    assert response.status_code == 200
    assert "/health" in response.json()["paths"]
    assert "/simulation/validate" in response.json()["paths"]
    assert "SimulationRequest" in response.json()["components"]["schemas"]
    operation = response.json()["paths"]["/simulation/validate"]["post"]
    error_schema = operation["responses"]["422"]["content"]["application/json"]["schema"]
    assert error_schema["$ref"].endswith("/ValidationErrorResponse")
    assert client.get("/docs").status_code == 200


def test_valid_request(client):
    payload = json.loads((EXAMPLES / "simulation-request.json").read_text())

    response = client.post("/simulation/validate", json=payload)

    assert response.status_code == 200
    assert response.json() == payload


def test_invalid_request_has_a_controlled_error(client):
    payload = json.loads((EXAMPLES / "simulation-request-invalid.json").read_text())
    expected = json.loads((EXAMPLES / "validation-error.json").read_text())

    response = client.post("/simulation/validate", json=payload)

    assert response.status_code == 422
    assert response.json() == expected


def test_validation_error_does_not_echo_invalid_input(client):
    response = client.post(
        "/simulation/validate",
        json={"duration_minutes": "private-value", "passenger_count": 120},
    )

    assert response.status_code == 422
    assert response.json()["errors"] == [
        {"field": "duration_minutes", "message": "Must be an integer."}
    ]
    assert "private-value" not in response.text


def test_malformed_json_has_a_controlled_error(client):
    response = client.post(
        "/simulation/validate",
        content='{"duration_minutes":',
        headers={"Content-Type": "application/json"},
    )

    assert response.status_code == 422
    assert response.json()["errors"] == [
        {"field": "body", "message": "Malformed JSON."}
    ]


@pytest.mark.parametrize("body", ["[]", "null"])
def test_body_must_be_a_json_object(client, body):
    response = client.post(
        "/simulation/validate",
        content=body,
        headers={"Content-Type": "application/json"},
    )

    assert response.status_code == 422
    assert response.json()["message"] == "Invalid request."

import json
from pathlib import Path

import pytest
from pydantic import ValidationError

from app.simulation.contracts import SimulationRequest

EXAMPLES = Path(__file__).resolve().parents[1] / "examples"


def test_valid_json_matches_simulation_request_schema():
    payload = json.loads((EXAMPLES / "simulation-request.json").read_text())

    request = SimulationRequest.model_validate(payload)

    assert request.model_dump() == payload


def test_seed_has_a_default():
    request = SimulationRequest(duration_minutes=1, passenger_count=1)

    assert request.seed == 42


@pytest.mark.parametrize(
    "changes",
    [
        {"duration_minutes": 0},
        {"passenger_count": 0},
        {"seed": -1},
        {"duration_minutes": "60"},
        {"passenger_count": True},
        {"duration_minutes": 60.5},
        {"unknown_field": "value"},
    ],
)
def test_invalid_input_is_rejected(changes):
    payload = {"duration_minutes": 60, "passenger_count": 120, "seed": 42}

    with pytest.raises(ValidationError):
        SimulationRequest.model_validate(payload | changes)


def test_required_fields_cannot_be_missing():
    with pytest.raises(ValidationError) as error:
        SimulationRequest.model_validate({})

    fields = {item["loc"][0] for item in error.value.errors()}
    assert fields == {"duration_minutes", "passenger_count"}


def test_json_schema_describes_the_contract():
    schema = SimulationRequest.model_json_schema()

    assert schema["required"] == ["duration_minutes", "passenger_count"]
    assert schema["additionalProperties"] is False
    assert schema["properties"]["duration_minutes"]["type"] == "integer"
    assert schema["properties"]["duration_minutes"]["exclusiveMinimum"] == 0
    assert schema["properties"]["passenger_count"]["exclusiveMinimum"] == 0
    assert schema["properties"]["seed"]["minimum"] == 0

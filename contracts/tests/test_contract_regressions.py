import json

import pytest
from fastapi.testclient import TestClient

from app.main import app
from app.contracts.validation import error, response
from test_contract_compatibility import SERVICES, example, send


@pytest.fixture(scope="module")
def client():
    with TestClient(app) as value:
        yield value


@pytest.mark.parametrize("body", [
    b'{"\\ud800":1}',
    b'{"text":"\\udc00"}',
    b'{"text":"\xff"}',
    '{"unknown":' + "[" * 64 + "]" * 64 + "}",
    json.dumps(example("simulation-request")).encode("utf-16"),
    b"\xef\xbb\xbf\xef\xbb\xbf{}",
])
def test_malformed_encoding_unicode_and_excess_nesting_have_identical_wire_errors(client, body):
    path = "/contracts/v1/simulation/validate"
    expected = response([error("", "malformed_json")])
    result = client.post(path, content=body, headers={"Content-Type": "application/json"})
    assert (result.status_code, result.json()) == (422, expected)
    if all(SERVICES):
        assert [send(base, path, body) for base in SERVICES] == [(422, expected)] * 2


@pytest.mark.parametrize("number", ["1e1000", "1e-1000", "-1e-1000", "9" * 5000])
def test_unrepresentable_numbers_are_rejected_consistently(client, number):
    body = json.dumps(example("simulation-request")).replace('"arrivalRatePerMinute": 2', '"arrivalRatePerMinute": ' + number)
    path = "/contracts/v1/simulation/validate"
    expected = response([error("/arrivalRatePerMinute", "out_of_range")])
    result = client.post(path, content=body, headers={"Content-Type": "application/json"})
    assert (result.status_code, result.json()) == (422, expected)
    if all(SERVICES):
        assert [send(base, path, body) for base in SERVICES] == [(422, expected)] * 2


def test_triplicate_fields_produce_one_error_in_each_service(client):
    path = "/contracts/v1/simulation/validate"
    body = '{"seed":1,"seed":2,"seed":3}'
    expected = response([error("/seed", "duplicate_field")])
    result = client.post(path, content=body, headers={"Content-Type": "application/json"})
    assert (result.status_code, result.json()) == (422, expected)
    if all(SERVICES):
        assert [send(base, path, body) for base in SERVICES] == [(422, expected)] * 2


def test_fractional_integer_tokens_cannot_be_rounded_into_valid_inputs(client):
    body = json.dumps(example("simulation-request")).replace('"seed": 42', '"seed": 42.00000000000000000000000000000001')
    path = "/contracts/v1/simulation/validate"
    expected = response([error("/seed", "invalid_type")])
    result = client.post(path, content=body, headers={"Content-Type": "application/json"})
    assert (result.status_code, result.json()) == (422, expected)
    if all(SERVICES):
        assert [send(base, path, body) for base in SERVICES] == [(422, expected)] * 2


def test_UTF8_BOM_and_exact_nesting_limit_are_supported(client):
    path = "/contracts/v1/simulation/validate"
    payload = example("simulation-request")
    body = b"\xef\xbb\xbf" + json.dumps(payload).encode()
    result = client.post(path, content=body, headers={"Content-Type": "application/json"})
    assert (result.status_code, result.json()) == (200, payload)
    if all(SERVICES):
        assert [send(base, path, body) for base in SERVICES] == [(200, payload)] * 2
    nested = '{"unknown":' + "[" * 63 + "]" * 63 + "}"
    result = client.post(path, content=nested, headers={"Content-Type": "application/json"})
    assert result.status_code == 422
    assert not any(item["code"] == "malformed_json" for item in result.json()["errors"])
    if all(SERVICES):
        assert [send(base, path, nested) for base in SERVICES] == [(422, result.json())] * 2

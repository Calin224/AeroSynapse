"""The forecast's recurrence and threshold contract agree at both service boundaries."""
import copy
import json
import os
import urllib.error
import urllib.request

import pytest
from fastapi.testclient import TestClient

from app.contracts.validation import CONTRACTS, validate
from app.main import app


@pytest.mark.parametrize('kind', ['request', 'response', 'equal-threshold', 'disabled', 'horizon', 'origin', 'duplicate',
                                  'unknown', 'negative', 'missing-threshold', 'queue', 'alert', 'period', 'units', 'date-shape', 'date-value', 'source', 'staff', 'mitigation'])
def test_forecast_contracts_agree(kind):
    is_request = kind in ('request', 'horizon', 'origin', 'duplicate', 'unknown', 'negative', 'missing-threshold')
    name = 'forecast-request' if is_request else 'forecast-response'
    value = json.loads((CONTRACTS / f'forecast/v1/examples/{name}.json').read_text())
    if kind == 'horizon': value['options']['horizonMinutes'] = 14
    if kind == 'origin': value['options']['fromMinute'] = 31
    if kind == 'duplicate': value['backlogs'].append(copy.deepcopy(value['backlogs'][0]))
    if kind == 'unknown': value['backlogs'][0]['checkpointId'] = value['simulation']['runId']
    if kind == 'negative': value['backlogs'][0]['queuedPassengers'] = -1
    if kind == 'missing-threshold': value['options'].pop('queueThresholdPassengers')
    if kind == 'queue': value['checkpoints'][0]['points'][4]['queuePassengers'] = 2
    if kind == 'alert': value['checkpoints'][0]['alert'] = None
    if kind == 'period': value['toMinute'] = 16
    if kind == 'units': value['queueUnit'] = 'minutes'
    if kind == 'date-shape': value['periodStartUtc'] = '2026-10-17'
    if kind == 'date-value': value['periodStartUtc'] = '2026-02-31T00:00:00Z'
    if kind == 'source': value['observationSource'] = 'actual_airport'
    if kind == 'staff': value['checkpoints'][0]['points'][0]['availableStaff'] += 1
    if kind == 'mitigation': value['checkpoints'][0]['mitigation']['extraStaff'] = 0
    if kind in ('equal-threshold', 'disabled'):
        value['queueThresholdPassengers'] = 1 if kind == 'equal-threshold' else None
        value['checkpoints'][0]['alert'] = None
        value['checkpoints'][0]['mitigation'] = None if kind == 'disabled' else {'extraStaff': 0, 'peakQueuePassengers': 1.0}
    definition = 'QueueForecastRequest' if is_request else 'QueueForecastResult'
    errors = validate(definition, value)
    assert bool(errors) == (kind not in ('request', 'response', 'equal-threshold', 'disabled'))
    path = '/contracts/v1/forecast/' + ('validate' if is_request else 'result/validate')
    with TestClient(app) as client:
        reply = client.post(path, json=value)
        assert reply.status_code == (422 if errors else 200)
        for base in (os.getenv('CONTRACT_API_URL'), os.getenv('CONTRACT_PYTHON_URL')):
            if not base: continue
            request = urllib.request.Request(base + path, data=json.dumps(value).encode(), headers={'Content-Type': 'application/json'})
            try: response = urllib.request.urlopen(request, timeout=10)
            except urllib.error.HTTPError as failure: response = failure
            with response:
                assert response.status == reply.status_code
                assert json.load(response) == reply.json()

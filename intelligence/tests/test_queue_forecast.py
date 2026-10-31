import copy
import json
from concurrent.futures import ThreadPoolExecutor

import pytest
from fastapi.testclient import TestClient

from app.contracts.validation import CONTRACTS, validate
from app.main import app
from app.simulation.forecast import forecast, mitigate, project_queue
from app.contracts.models import SimulationRequest
from app.simulation.engine import simulate


def request():
    simulation = json.loads((CONTRACTS / 'kpis/v1/examples/kpi-request.json').read_text())
    simulation['durationMinutes'] = simulation['passengerGeneration']['durationMinutes'] = 45
    simulation['passengerGeneration']['flights'][0]['boardingCloseMinute'] = 30
    simulation['passengerGeneration']['batches'][0]['arrival'].update(minimum=5, maximum=5, mean=5, mode=5)
    return {'simulation': simulation, 'observationSource': 'captured_snapshot', 'options': {'fromMinute': 0, 'horizonMinutes': 15, 'queueThresholdPassengers': 0},
            'backlogs': [{'checkpointId': cp['checkpointId'], 'queuedPassengers': 0, 'notArrivedPassengers': 2}
                         for cp in simulation['checkpoints']]}


def test_small_recurrence_is_calculable_by_hand_and_capacity_cannot_make_queues_negative():
    points, alert = project_queue(2, [3, 0, 1, 0], [1, 5, 0, 2], 7, 3)
    assert [point['queuePassengers'] for point in points] == [4, 0, 1, 0]
    assert [point['minute'] for point in points] == [8, 9, 10, 11]
    assert alert == {'minute': 8, 'queuePassengers': 4, 'thresholdPassengers': 3}


@pytest.mark.parametrize('threshold,expected', [(4, None), (5, None), (None, None), (3, 1), (0, 1)])
def test_only_strict_exceedance_of_an_enabled_threshold_generates_an_alert(threshold, expected):
    _, alert = project_queue(2, [3, 0], [1, 0], 0, threshold)
    assert (alert['minute'] if alert else None) == expected


def test_mitigation_is_the_smallest_extra_staffing_that_keeps_every_minute_within_the_threshold():
    # Q starts at 4; 6 arrivals/min against 1 operator × 2 pax/min grows the queue by 4 each minute.
    points = [{'arrivals': 6, 'availableStaff': 1}] * 3
    assert mitigate(4, points, 2, None) is None
    # Two extra operators (capacity 6) hold Q at 4; one extra (capacity 4) lets it reach 10.
    assert mitigate(4, points, 2, 4) == {'extraStaff': 2, 'peakQueuePassengers': 4.0}
    assert mitigate(4, points, 2, 10) == {'extraStaff': 1, 'peakQueuePassengers': 10.0}
    assert mitigate(4, points, 2, 100) == {'extraStaff': 0, 'peakQueuePassengers': 16.0}
    # A closed lane accumulates arrivals regardless of extra staff: staffing alone cannot fix it.
    assert mitigate(0, [{'arrivals': 1, 'availableStaff': 0}], 2, 0) == {'extraStaff': None, 'peakQueuePassengers': None}


def test_zero_capacity_accumulates_arrivals_without_division_or_nonfinite_results():
    value = request()
    value['simulation']['checkpoints'][0]['staffCount'] = 0
    value['simulation']['passengerGeneration']['checkpoints'][0]['staffCount'] = 0
    result = forecast(value)
    assert not validate('QueueForecastRequest', value)
    assert not validate('QueueForecastResult', result)
    assert all(point['capacity'] == 0 for point in result['checkpoints'][0]['points'])
    assert result['checkpoints'][0]['points'][-1]['queuePassengers'] == 2
    json.dumps(result, allow_nan=False)


def test_profiles_reuse_the_captured_plan_and_future_arrival_boundary_is_inclusive_on_the_right():
    value = request()
    result = forecast(value)
    assert not validate('QueueForecastRequest', value)
    assert not validate('QueueForecastResult', result)
    points = result['checkpoints'][0]['points']
    assert [point['arrivals'] for point in points] == [0, 0, 0, 0, 2] + [0] * 10
    assert points[4]['queuePassengers'] == 1
    value['options']['fromMinute'] = 5
    value['backlogs'][0].update(queuedPassengers=1, notArrivedPassengers=0)
    after = forecast(value)
    assert sum(point['arrivals'] for point in after['checkpoints'][0]['points']) == 0
    assert after['checkpoints'][0]['initialQueuePassengers'] == 1
    assert result['kind'] == 'simulated_estimate' and result['fromMinute'] == 0 and result['toMinute'] == 15
    assert result['queueUnit'] == 'passengers' and result['flowUnit'] == 'passengers/minute'


def test_aggregate_rate_is_capped_by_the_observed_remaining_population():
    value = request()
    value['simulation'].pop('passengerGeneration')
    value['simulation'].pop('navigation')
    value['simulation'].update(passengerCount=10, arrivalRatePerMinute=4)
    value['backlogs'][0]['notArrivedPassengers'] = 3
    result = forecast(value)
    assert not validate('QueueForecastRequest', value)
    assert result['arrivalSource'] == 'configured_arrival_rate'
    assert [point['arrivals'] for point in result['checkpoints'][0]['points']] == [3] + [0] * 14


def test_forecast_changes_with_actual_planned_passengers_and_staff_and_explains_capacity():
    value = request(); original = forecast(value)
    value['simulation']['passengerCount'] = 4
    value['simulation']['passengerGeneration']['flights'][0]['passengerCapacity'] = 4
    value['simulation']['passengerGeneration']['batches'][0]['modeledCount'] = 4
    value['backlogs'][0]['notArrivedPassengers'] = 4
    loaded = forecast(value)
    assert loaded['checkpoints'][0]['points'][4]['queuePassengers'] > original['checkpoints'][0]['points'][4]['queuePassengers']
    value['simulation']['checkpoints'][0]['staffCount'] = 2
    value['simulation']['passengerGeneration']['checkpoints'][0]['staffCount'] = 2
    staffed = forecast(value)
    assert staffed['checkpoints'][0]['points'][4]['queuePassengers'] < loaded['checkpoints'][0]['points'][4]['queuePassengers']
    row = staffed['checkpoints'][0]
    assert row['serviceRateSource'] == 'sampled_service_mean'
    assert all(point['capacity'] == point['availableStaff'] * row['serviceRatePerStaffMinute'] for point in row['points'])
    assert not validate('QueueForecastResult', staffed)


def test_live_observations_include_remaining_passengers_and_need_not_align_with_final_kpi_cadence():
    value = request(); value['simulation']['snapshotIntervalMinutes'] = 5
    progress = []
    simulate(SimulationRequest.model_validate(value['simulation']), on_progress=progress.append)
    frame = next(row for row in progress if row['simulationMinute'] == 3)
    assert frame['zones'][0]['notArrivedPassengers'] == 2
    value['backlogs'] = [{'checkpointId': zone['zoneId'], 'queuedPassengers': zone['queuedPassengers'],
                         'notArrivedPassengers': zone['notArrivedPassengers']} for zone in frame['zones']]
    value['options']['fromMinute'] = 3; value['observationSource'] = 'live_simulation'
    assert not validate('QueueForecastRequest', value)
    result = forecast(value)
    assert result['fromMinute'] == 3 and result['observationSource'] == 'live_simulation'
    assert result['checkpoints'][0]['points'][1]['arrivals'] == 2


def test_known_capacity_changes_and_surges_apply_at_their_scheduled_minute():
    value = request(); simulation = value['simulation']; key = simulation['checkpoints'][0]['checkpointId']
    def event(index, kind, minute, **extra):
        return {'eventId': f'90000000-0000-0000-0000-{index:012d}', 'type': kind, 'minute': minute, 'checkpointId': key, **extra}
    simulation['events'] = [event(1, 'checkpoint_unavailable', 2), event(2, 'resume', 4),
                            event(3, 'staff_reduction', 6, staffCount=0), event(4, 'resume', 8),
                            event(5, 'passenger_surge', 8, passengerCount=6, windowMinutes=3)]
    assert not validate('QueueForecastRequest', value)
    points = forecast(value)['checkpoints'][0]['points']
    assert [point['capacity'] for point in points[:9]] == [1, 1, 0, 0, 1, 1, 0, 0, 1]
    assert sum(point['arrivals'] for point in points) == 8
    value['options']['fromMinute'] = 7
    assert forecast(value)['checkpoints'][0]['points'][0]['capacity'] == 0


@pytest.mark.parametrize('mutation', ['horizon', 'period', 'cadence', 'duplicate', 'unknown', 'negative', 'boolean', 'threshold', 'missing'])
def test_invalid_forecast_inputs_are_rejected_before_computation(mutation):
    value = request()
    if mutation == 'horizon': value['options']['horizonMinutes'] = 14
    elif mutation == 'period': value['options']['fromMinute'] = 31
    elif mutation == 'cadence': value['options']['fromMinute'] = -1
    elif mutation == 'duplicate': value['backlogs'].append(copy.deepcopy(value['backlogs'][0]))
    elif mutation == 'unknown': value['backlogs'][0]['checkpointId'] = value['simulation']['runId']
    elif mutation == 'negative': value['backlogs'][0]['queuedPassengers'] = -1
    elif mutation == 'boolean': value['options']['queueThresholdPassengers'] = True
    elif mutation == 'threshold': value['options']['queueThresholdPassengers'] = 0.5
    elif mutation == 'missing': value['options'].pop('queueThresholdPassengers')
    assert validate('QueueForecastRequest', value)
    with TestClient(app) as client:
        assert client.post('/simulation/queue-forecast', json=value).status_code == 422


@pytest.mark.parametrize('mutation', ['formula', 'queue', 'alert', 'mitigation', 'threshold', 'period', 'points', 'duplicate', 'date-shape', 'date-value'])
def test_forged_forecasts_cannot_hide_alerts_or_change_the_recurrence(mutation):
    result = forecast(request())
    if mutation == 'formula': result['formula'] = 'other'
    elif mutation == 'queue': result['checkpoints'][0]['points'][4]['queuePassengers'] += 1
    elif mutation == 'alert': result['checkpoints'][0]['alert'] = None
    elif mutation == 'mitigation': result['checkpoints'][0]['mitigation']['extraStaff'] += 1
    elif mutation == 'threshold': result['queueThresholdPassengers'] = 1
    elif mutation == 'period': result['toMinute'] = 16
    elif mutation == 'points': result['checkpoints'][0]['points'].pop()
    elif mutation == 'duplicate': result['checkpoints'].append(copy.deepcopy(result['checkpoints'][0]))
    elif mutation == 'date-shape': result['periodStartUtc'] = '2026-10-17'
    elif mutation == 'date-value': result['periodStartUtc'] = '2026-02-31T00:00:00Z'
    assert validate('QueueForecastResult', result)


def test_http_decimal_wire_values_and_reproducibility_do_not_mutate_captured_inputs():
    value = request(); original = copy.deepcopy(value)
    with TestClient(app) as client:
        response = client.post('/simulation/queue-forecast', content=json.dumps(value).replace('"horizonMinutes": 15', '"horizonMinutes": 15.0'), headers={'Content-Type': 'application/json'})
        assert response.status_code == 200, response.text
        assert not validate('QueueForecastResult', response.json())
        assert client.post('/simulation/queue-forecast', content='{}', headers={'Content-Type': 'text/plain'}).status_code == 415
        assert client.post('/simulation/queue-forecast', content='{', headers={'Content-Type': 'application/json'}).status_code == 422
    with ThreadPoolExecutor(max_workers=3) as pool:
        assert all(result == response.json() for result in pool.map(lambda _: forecast(value), range(3)))
    assert value == original

import copy
import json

import pytest
from fastapi.testclient import TestClient

from app.anomaly import evaluation as ev
from app.anomaly.detector import envelope, explain, fluid, null_calibration, screen, triggered_episodes
from app.contracts.validation import CONTRACTS, validate
from app.main import app

SETTINGS = {'upperQuantile': 0.8, 'windowMinutes': 3, 'minExceedances': 2, 'minExcessPassengers': 2.0}
SMALL = ev.SPEC | {'tuningScenarios': 1, 'testScenarios': 2, 'replications': 5, 'normalRuns': 2, 'perturbedRuns': 2}


def example(name):
    return json.loads((CONTRACTS / f'anomaly/v1/examples/{name}.json').read_text())


class Ready:
    def __init__(self, value):
        self.value = value

    def current(self):
        return self.value, 'ready'


class Warming:
    def current(self):
        return None, 'warming'


@pytest.fixture(scope='module')
def test_set():
    return ev.build_test_set(SMALL)


def test_envelope_uses_nearest_rank_quantiles_and_the_us026_median():
    replications = [[1, 10], [2, 20], [3, 30], [4, 40], [5, 50]]
    middle, lower, upper = envelope(replications, 0.8)
    assert middle == [3, 30] and upper == [4, 40] and lower == [1, 10]  # ranks ceil(0.8·5)=4 and ceil(0.2·5)=1


def test_p_values_count_replications_at_or_above_the_observation_and_never_reach_zero():
    result = screen([3, 100], [[1, 1], [2, 2], [3, 3], [4, 4], [5, 5]], SETTINGS, 1)
    assert result['pValue'] == [(1 + 3) / 6, 1 / 6]


def test_window_rule_needs_k_exceedances_in_w_samples_and_starts_at_the_first_one():
    assert triggered_episodes([False, True, False, True, True, False, False, False], 3, 2) == [(1, 3, 5)]
    assert triggered_episodes([True, False, False, True], 3, 2) == []


def test_the_excess_floor_and_strict_upper_bound_suppress_alarms_on_small_or_equal_queues():
    replications = [[0, 0, 0, 0]] * 3 + [[1, 1, 1, 1]] * 2  # median 0, upper (rank 4 of 5) 1
    assert not any(screen([1, 1, 1, 1], replications, SETTINGS, 1)['exceeds'])  # equal to the upper quantile
    assert not any(screen([1.5] * 4, replications, SETTINGS, 1)['exceeds'])  # above it, but within the 2-passenger floor
    assert len(screen([0, 3, 3, 3], replications, SETTINGS, 1)['episodes']) == 1


def series(queued, arrived, staff):
    return {'queued': queued, 'arrived': arrived, 'staff': staff}


def test_shapley_split_is_additive_and_attributes_demand_and_capacity_correctly():
    minutes = [0, 1, 2, 3, 4]
    normal = [series([0, 0, 0, 0, 0], [0, 2, 4, 6, 8], [2, 2, 2, 2, 2]) for _ in range(5)]
    surge = explain(series([0, 0, 3, 6, 9], [0, 2, 7, 12, 17], [2, 2, 2, 2, 2]), normal, 1.0, minutes, 0, 4)
    assert surge['contributions']['demand'] == pytest.approx(9) and surge['contributions']['capacity'] == pytest.approx(0)
    assert sum(surge['contributions'].values()) == pytest.approx(surge['explained'])
    assert surge['residual'] == pytest.approx(surge['observedExcess'] - surge['explained'])
    cut = explain(series([0, 1, 2, 3, 4], [0, 2, 4, 6, 8], [1, 1, 1, 1, 1]), normal, 1.0, minutes, 0, 4)
    assert cut['contributions']['capacity'] == pytest.approx(4) and cut['contributions']['demand'] == pytest.approx(0)
    assert cut['inputs']['staffMinutesObserved'] == 4 and cut['inputs']['staffMinutesExpected'] == 8


def test_fluid_counterfactual_is_the_us036_recurrence_over_the_sample_grid():
    assert fluid(2, [3, 0, 1, 0], [1, 5, 0, 2], 1.0, [7, 8, 9, 10, 11], 0, 4) == 0
    assert fluid(0, [4, 4], [1, 1], 1.0, [0, 2, 4], 0, 2) == 4  # capacity 1 × 2 minutes per sample


def test_leave_one_out_calibration_reports_no_alarms_for_identical_replications():
    rows = [{'replications': [series([5] * 10, list(range(10)), [1] * 10) for _ in range(5)]}]
    assert null_calibration(rows, SETTINGS, 1, list(range(10)))['replicationsWithAlerts'] == 0


def test_example_request_and_result_satisfy_the_shared_contract():
    request, result = example('detection-request'), example('detection-response')
    assert not validate('AnomalyDetectionRequest', request) and not validate('AnomalyDetectionResult', result)
    episode = result['checkpoints'][0]['episodes'][0]
    assert episode['explanation']['contributions']['demand'] > 10 * abs(episode['explanation']['contributions']['capacity'])


@pytest.mark.parametrize('kind', ['self', 'seeds', 'grid', 'window', 'arrivals', 'count', 'person', 'scope'])
def test_inconsistent_or_person_level_requests_are_rejected(kind):
    value = example('detection-request')
    row = value['checkpoints'][0]
    if kind == 'self': value['replicationRuns'][0]['runId'] = value['runId']
    if kind == 'seeds': value['replicationRuns'][1]['seed'] = value['replicationRuns'][0]['seed']
    if kind == 'grid': value['minutes'][1] = 2
    if kind == 'window': value['settings'].update(windowMinutes=3, minExceedances=4)
    if kind == 'arrivals': row['observed']['arrived'][-1] = 0
    if kind == 'count': row['replications'].pop()
    if kind == 'person': row['observed']['passengers'] = [{'id': 'p-1', 'queueEnteredMinute': 3}]
    if kind == 'scope': value['dataScope'] = 'airport_operations'
    assert validate('AnomalyDetectionRequest', value)


@pytest.mark.parametrize('kind', ['exceeds', 'episode', 'shapley', 'p-value', 'band', 'calibration'])
def test_forged_results_are_rejected(kind):
    value = example('detection-response')
    row = value['checkpoints'][0]
    if kind == 'exceeds': row['exceeds'][0] = not row['exceeds'][0]
    if kind == 'episode': row['episodes'] = []
    if kind == 'shapley': row['episodes'][0]['explanation']['contributions']['demand'] += 1
    if kind == 'p-value': row['pValue'][0] = 0.01
    if kind == 'band': row['lower'][0] = row['upper'][0] + 1
    if kind == 'calibration': value['nullCalibration']['replicationsWithAlerts'] = 6
    assert validate('AnomalyDetectionResult', value)


def test_synthetic_test_set_is_versioned_contract_valid_and_reproducible(test_set):
    assert test_set['version'] == ev.TEST_SET_VERSION
    assert ev.build_test_set(SMALL)['hash'] == test_set['hash']
    labels = [case['label']['kind'] for case in test_set['cases']]
    assert labels.count('normal') == 6 and len(labels) == 12
    for case in test_set['cases']:
        if case['label']['kind'] != 'normal':
            assert case['label']['impactBucket'] in ('under_5', '5_to_20', 'over_20')
            assert not validate('AnomalyDetectionRequest', ev.detection_request(test_set, case, ev.DEFAULT_SETTINGS))


def test_classification_counts_an_alert_active_during_the_effect_as_detected():
    case = {'label': {'kind': 'staff_reduction', 'onsetMinute': 47, 'effectEndMinute': 64}}
    running = [{'checkpointId': 'a', 'onsetMinute': 46, 'endMinute': 80, 'peakMinute': 80, 'minPValue': 0.1}]
    assert ev.classify(case, running) == {'outcome': 'true_positive', 'delayMinutes': 0, 'preOnsetAlerts': 0}
    early = [{'checkpointId': 'a', 'onsetMinute': 10, 'endMinute': 20, 'peakMinute': 15, 'minPValue': 0.1}]
    assert ev.classify(case, early) == {'outcome': 'false_negative', 'delayMinutes': None, 'preOnsetAlerts': 1}
    assert ev.classify({'label': {'kind': 'normal'}}, early)['outcome'] == 'false_positive'


def test_evaluation_exposes_confusion_cases_sweep_and_validates(test_set):
    result = ev.evaluate(test_set, ev.DEFAULT_SETTINGS)
    assert not validate('AnomalyEvaluationResult', result)
    assert sum(result['confusion'].values()) == len(result['cases']) == 8
    assert len(result['sweep']) == len(ev.SWEEP) and result['nullCheck']['heldOutFalsePositiveRate'] == result['falsePositiveRate']
    forged = copy.deepcopy(result)
    forged['confusion']['true_negative'] += 1
    assert validate('AnomalyEvaluationResult', forged)


def test_endpoints_report_warming_cases_and_unknown_cases(test_set, monkeypatch):
    monkeypatch.setattr(ev, 'TEST_SET', Warming())
    with TestClient(app) as client:
        response = client.post('/anomaly/evaluation', json={'settings': ev.DEFAULT_SETTINGS})
        assert response.status_code == 200 and response.json()['status'] == 'warming'
        assert not validate('AnomalyEvaluationResult', response.json())
        monkeypatch.setattr(ev, 'TEST_SET', Ready(test_set))
        assert client.post('/anomaly/evaluation', json={'settings': ev.DEFAULT_SETTINGS}).json()['status'] == 'ready'
        case = next(item for item in test_set['cases'] if item['split'] == 'test')
        detail = client.post('/anomaly/evaluation/case', json={'settings': ev.DEFAULT_SETTINGS, 'caseKey': case['caseKey']})
        assert detail.status_code == 200 and not validate('AnomalyCaseResult', detail.json())
        assert client.post('/anomaly/evaluation/case', json={'settings': ev.DEFAULT_SETTINGS, 'caseKey': 'test-99-99'}).status_code == 404
        invalid = {'settings': ev.DEFAULT_SETTINGS | {'windowMinutes': 2, 'minExceedances': 3}}
        assert client.post('/anomaly/evaluation', json=invalid).status_code == 422


def test_detection_endpoint_accepts_wire_numbers():
    with TestClient(app) as client:
        response = client.post('/anomaly/detect', json=example('detection-request'))
        assert response.status_code == 200 and not validate('AnomalyDetectionResult', response.json())
        assert response.json() == example('detection-response')

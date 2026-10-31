import copy
import json
import random

import numpy as np
import pytest
from fastapi.testclient import TestClient

from app.contracts.models import SimulationRequest
from app.contracts.validation import CONTRACTS, validate
from app.forecast import model as ml
from app.forecast.contract import CLAIM, DETERMINISTIC_VERSION
from app.forecast.dataset import (DATA_SOURCE, DATASET_VERSION, FAMILIES, FEATURES, SPEC, build_dataset, coefficient_of_variation, family_of, features,
                                  forecast_request, scenario)
from app.main import app
from app.simulation.engine import simulate

SMALL = SPEC | {'scenariosPerFamily': 6}


@pytest.fixture(scope='module')
def dataset():
    return build_dataset(SMALL)


@pytest.fixture(scope='module')
def trained(dataset):
    return ml.train(dataset)


class Ready:
    def __init__(self, model):
        self.model = model

    def current(self):
        return self.model, None


def captured(family='events', number=0):
    simulation = scenario(FAMILIES.index(family) * 1000 + number, random.Random(SMALL['seed'] + number), SMALL, family)
    kpis = simulate(SimulationRequest.model_validate(simulation)).model_dump(mode='json', by_alias=True)['kpiResult']
    return simulation, {(row['zoneId'], row['minute']): row for row in kpis['snapshots']}


def test_dataset_is_versioned_and_regenerates_to_the_same_hash(dataset):
    assert dataset['version'] == DATASET_VERSION and dataset['source'] == DATA_SOURCE
    assert build_dataset(SMALL)['hash'] == dataset['hash']
    assert build_dataset(SMALL | {'seed': SMALL['seed'] + 1})['hash'] != dataset['hash']
    assert {row['family'] for row in dataset['rows']} == set(FAMILIES)


@pytest.mark.parametrize('family', FAMILIES)
def test_every_family_satisfies_the_shared_contracts(family):
    rng = random.Random(7)
    for number in range(20):
        simulation = scenario(FAMILIES.index(family) * 1000 + number, rng, SMALL, family)
        assert not validate('KpiSimulationRequest', simulation) and family_of(simulation) == family
    simulation, snapshots = captured(family)
    assert not validate('QueueForecastRequest', forecast_request(simulation, snapshots, 30, 15))


def test_features_see_only_the_origin_snapshot_not_the_target():
    simulation, snapshots = captured('events')
    future = copy.deepcopy(snapshots)
    for (zone, minute), row in future.items():
        if minute > 30:
            row['queuedPassengers'] = row['notArrivedPassengers'] = 999
    assert features(forecast_request(simulation, snapshots, 30, 15))[1] == features(forecast_request(simulation, future, 30, 15))[1]


def test_service_variation_is_read_from_the_captured_distributions():
    simulation, snapshots = captured('profiles')
    rows = features(forecast_request(simulation, snapshots, 30, 15))[1]
    batches = simulation['passengerGeneration']['batches']
    for key, row in rows.items():
        kinds = {batch['service']['kind'] for batch in batches if batch['checkpointId'] == key}
        assert row['serviceCv'] == 0 if kinds == {'constant'} else row['serviceCv'] > 0
    # The libm-dependent part is rounded, so the dataset hash is identical on macOS and Linux.
    assert all(coefficient_of_variation(batch['service']) == round(coefficient_of_variation(batch['service']), 9) for batch in batches)
    aggregate, snapshots = captured('aggregate')
    assert {row['serviceCv'] for row in features(forecast_request(aggregate, snapshots, 30, 15))[1].values()} == {1.0}


def test_split_keeps_whole_scenarios_apart_stratified_by_family(dataset):
    parts = ml.split(dataset['rows'])
    scenarios = {name: {row['scenario'] for row in rows} for name, rows in parts.items()}
    assert not scenarios['train'] & scenarios['calibration'] and not scenarios['train'] & scenarios['test']
    assert not scenarios['calibration'] & scenarios['test']
    assert all({row['family'] for row in rows} == set(FAMILIES) for rows in parts.values())
    assert sum(len(rows) for rows in parts.values()) == len(dataset['rows'])


def test_training_selects_by_grouped_cv_and_calibrates_per_family(trained):
    model, _, extras = trained
    assert set(extras['cv']) == {'deterministic', *ml.CANDIDATES} and model.estimator == min(ml.CANDIDATES, key=extras['cv'].get)
    assert set(model.margins) == set(FAMILIES) and model.calibration in ml.CALIBRATIONS
    assert set(extras['calibrationSelection']) == set(ml.CALIBRATIONS)


def test_disruption_gate_leaves_undisrupted_rows_on_the_deterministic_forecast(dataset):
    rows = dataset['rows']
    gated = ml.DisruptionGated(ml.CANDIDATES['hgb_shallow']()).fit(ml.matrix(rows), ml.residual(rows))
    x = ml.matrix(rows)
    calm = ~ml.DisruptionGated.gate(x)
    assert calm.any() and not gated.predict(x)[calm].any()


def test_disruption_gate_without_disrupted_rows_keeps_the_deterministic_forecast(dataset):
    calm = [row for row in dataset['rows'] if not ml.DisruptionGated.gate(ml.matrix([row]))[0]]
    gated = ml.DisruptionGated(ml.CANDIDATES['hgb_shallow']()).fit(ml.matrix(calm), ml.residual(calm))
    assert not gated.predict(ml.matrix(dataset['rows'])).any()


def test_conformal_quantile_uses_the_finite_sample_correction():
    scores = np.arange(1, 11, dtype=float)
    assert ml.conformal(scores, 0.8) == 9.0  # ceil(11 × 0.8) / 10 = 0.9 → ninth of ten scores
    assert ml.conformal(scores, 0.95) == 10.0


def test_report_compares_models_with_simple_baselines_and_claims_synthetic_only(dataset):
    report = ml.evaluate(dataset)
    assert report['isSynthetic'] and report['claim'] == CLAIM
    regression = report['regression']
    assert set(regression['methods']) == {'persistence', 'deterministic', 'ml'}
    assert regression['selectedEstimator'] in ml.CANDIDATES and set(regression['permutationImportance']) == set(FEATURES)
    assert [point['trainScenarios'] for point in regression['learningCurve']] == sorted(point['trainScenarios'] for point in regression['learningCurve'])
    for difference in [*regression['differences'].values(), *regression['differencesByFamily'].values()]:
        assert difference['ci95'][0] <= difference['ci95'][1]
    for method in report['intervals']['methods'].values():
        assert 0 <= method['coverage'] <= 1 and method['meanWidth'] >= 0
    risk = report['risk']
    assert {'base_rate', 'deterministic_indicator', 'logistic_margin', 'ml_served'} <= set(risk['methods'])
    for method in risk['methods'].values():
        assert 0 <= method['brier'] <= 1 and 0 <= method['ece'] <= 1 and 0 <= method['auc'] <= 1
        assert sum(row['count'] for row in method['reliability']) == report['split']['test']['rows'] * len(ml.RISK_THRESHOLDS)
    assert report['model']['version'].startswith(ml.MODEL_NAME + '+') and report['dataset']['hash'] == dataset['hash']


def test_disabled_predictor_returns_the_deterministic_forecast_without_training(monkeypatch):
    monkeypatch.delenv('QUEUE_ML_PREDICTOR', raising=False)
    predictor = ml.Predictor()
    simulation, snapshots = captured()
    result = ml.predict(forecast_request(simulation, snapshots, 30, 15, threshold=10), predictor)
    assert predictor.state == 'idle' and not validate('QueuePredictionResult', result)
    assert result['predictor'] == {'kind': 'deterministic', 'modelVersion': DETERMINISTIC_VERSION, 'estimator': None, 'datasetVersion': None,
                                   'datasetHash': None, 'trainingDataSource': None, 'formula': ml.FORMULA,
                                   'fallbackReason': 'predictor_disabled'}
    for row in result['checkpoints']:
        assert row['predictedQueuePassengers'] == row['deterministicQueuePassengers']
        assert row['predictionInterval'] is None and row['exceedanceProbability'] is None


@pytest.mark.parametrize('state,reason', [('warming', 'model_warming'), ('failed', 'model_unavailable')])
def test_enabled_predictor_without_a_ready_model_falls_back_without_blocking(monkeypatch, state, reason):
    monkeypatch.setenv('QUEUE_ML_PREDICTOR', 'enabled')
    predictor = ml.Predictor()
    predictor.state = state
    simulation, snapshots = captured()
    result = ml.predict(forecast_request(simulation, snapshots, 30, 15), predictor)
    assert result['predictor']['kind'] == 'deterministic' and result['predictor']['fallbackReason'] == reason
    assert not validate('QueuePredictionResult', result)


def test_background_training_makes_the_model_ready(monkeypatch):
    monkeypatch.setenv('QUEUE_ML_PREDICTOR', 'enabled')
    monkeypatch.setattr(ml, 'SPEC', SPEC | {'scenariosPerFamily': 3})
    predictor = ml.Predictor()
    assert predictor.current() == (None, 'model_warming')
    predictor.thread.join(timeout=300)
    model, reason = predictor.current()
    assert reason is None and model.version.startswith(ml.MODEL_NAME + '+')


def test_ready_predictor_reports_model_version_interval_and_data_sources(monkeypatch, trained):
    monkeypatch.setenv('QUEUE_ML_PREDICTOR', 'enabled')
    model = trained[0]
    simulation, snapshots = captured()
    result = ml.predict(forecast_request(simulation, snapshots, 30, 15, threshold=10, source='live_simulation'), Ready(model))
    predictor = result['predictor']
    assert not validate('QueuePredictionResult', result)
    assert predictor['kind'] == 'ml' and predictor['modelVersion'] == model.version and predictor['estimator'] == model.estimator
    assert predictor['datasetVersion'] == DATASET_VERSION and predictor['trainingDataSource'] == DATA_SOURCE
    assert result['observationSource'] == 'live_simulation' and result['validity'] == CLAIM
    for row in result['checkpoints']:
        interval = row['predictionInterval']
        assert 0 <= interval['lowerPassengers'] <= interval['upperPassengers'] and interval['nominalCoverage'] == ml.COVERAGE
        assert 0 <= row['exceedanceProbability'] <= 1
    no_threshold = ml.predict(forecast_request(simulation, snapshots, 30, 15), Ready(model))
    assert all(row['exceedanceProbability'] is None for row in no_threshold['checkpoints'])


def example():
    return json.loads((CONTRACTS / 'forecast/v1/examples/prediction-response.json').read_text())


def deterministic(value):
    value['predictor'] = {'kind': 'deterministic', 'modelVersion': DETERMINISTIC_VERSION, 'estimator': None, 'datasetVersion': None,
                          'datasetHash': None, 'trainingDataSource': None, 'formula': ml.FORMULA, 'fallbackReason': 'predictor_disabled'}
    for row in value['checkpoints']:
        row.update(predictedQueuePassengers=row['deterministicQueuePassengers'], predictionInterval=None, exceedanceProbability=None)
    return value


@pytest.mark.parametrize('kind', ['ml', 'deterministic', 'interval-on-fallback', 'missing-provenance', 'probability-without-threshold',
                                  'missing-probability', 'inverted-interval', 'formula-on-ml', 'disabled-model-version', 'horizon',
                                  'duplicate', 'real-source', 'claim'])
def test_prediction_contract_rejects_inconsistent_answers(kind):
    value = deterministic(example()) if kind in ('deterministic', 'interval-on-fallback', 'disabled-model-version') else example()
    row = value['checkpoints'][0]
    if kind == 'interval-on-fallback': row['predictionInterval'] = example()['checkpoints'][0]['predictionInterval']
    if kind == 'disabled-model-version': value['predictor']['modelVersion'] = example()['predictor']['modelVersion']
    if kind == 'missing-provenance': value['predictor']['datasetHash'] = None
    if kind == 'probability-without-threshold': value['queueThresholdPassengers'] = None
    if kind == 'missing-probability': row['exceedanceProbability'] = None
    if kind == 'inverted-interval': row['predictionInterval'].update(lowerPassengers=5, upperPassengers=1)
    if kind == 'formula-on-ml': value['predictor']['formula'] = ml.FORMULA
    if kind == 'horizon': value['toMinute'] = value['fromMinute'] + 31
    if kind == 'duplicate': value['checkpoints'].append(copy.deepcopy(row))
    if kind == 'real-source': value['predictor']['trainingDataSource'] = 'airport_operations'
    if kind == 'claim': value['validity'] = 'validated'
    errors = validate('QueuePredictionResult', value)
    assert bool(errors) == (kind not in ('ml', 'deterministic'))
    with TestClient(app) as client:
        assert client.post('/contracts/v1/forecast/prediction/validate', json=value).status_code == (422 if errors else 200)


def test_prediction_endpoint_validates_the_forecast_contract(monkeypatch):
    monkeypatch.setenv('QUEUE_ML_PREDICTOR', 'disabled')
    simulation, snapshots = captured()
    value = forecast_request(simulation, snapshots, 30, 15, threshold=10)
    with TestClient(app) as client:
        response = client.post('/simulation/queue-prediction', json=value)
        assert response.status_code == 200 and response.json()['predictor']['kind'] == 'deterministic'
        assert response.json()['queueThresholdPassengers'] == 10 and not validate('QueuePredictionResult', response.json())
        value['options']['horizonMinutes'] = 31
        assert client.post('/simulation/queue-prediction', json=value).status_code == 422

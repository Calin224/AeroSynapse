"""Scikit-learn queue models compared with simple baselines on synthetic data only.

Point forecasts learn the residual of the deterministic forecast; the estimator is chosen
by grouped cross-validation on the training scenarios. Intervals are conformalised quantile
regression and risk scores are calibrated, both on scenarios the models were not fitted on.
The test scenarios are scored once. Nothing here is evidence of real-world operational accuracy.
"""
import argparse
import json
import logging
import math
import os
import random
import sys
import threading
from dataclasses import dataclass

import numpy as np
import sklearn
from sklearn.calibration import CalibratedClassifierCV
from sklearn.ensemble import HistGradientBoostingClassifier, HistGradientBoostingRegressor, RandomForestRegressor
from sklearn.frozen import FrozenEstimator
from sklearn.linear_model import LogisticRegression, Ridge
from sklearn.metrics import roc_auc_score
from sklearn.model_selection import GroupKFold
from sklearn.pipeline import make_pipeline
from sklearn.preprocessing import StandardScaler

from app.forecast.contract import CLAIM, DETERMINISTIC_VERSION
from app.forecast.dataset import DATA_SOURCE, FAMILIES, FEATURES, SPEC, build_dataset, features
from app.simulation.forecast import FORMULA
from app.simulation.provenance import fingerprint

logger = logging.getLogger('aerosynapse.intelligence.forecast')
MODEL_NAME = 'us038-residual-cqr-calibrated-2.0'
SPLIT_SEED, SPLIT = 3801, (('train', 0.6), ('calibration', 0.2), ('test', 0.2))
CV_FOLDS, BOOTSTRAP, BINS, IMPORTANCE_REPEATS = 5, 1000, 10, 5
COVERAGE = 0.8
# Evaluation thresholds for the risk score; they are research settings, not operational defaults.
RISK_THRESHOLDS = (5, 10, 20, 40)
LEARNING_CURVE = (0.25, 0.5, 1.0)
DISRUPTION = [FEATURES.index(name) for name in ('zeroCapacityMinutes', 'capacityChanges', 'surgeArrivals')]


class DisruptionGated:
    """Residual model applied only where capacity changes or surges break the fluid model's assumptions."""

    def __init__(self, base):
        self.base = base

    @staticmethod
    def gate(x):
        return (x[:, DISRUPTION] > 0).any(axis=1)

    def fit(self, x, y):
        mask = self.gate(x)
        # Without disrupted rows there is nothing to correct: the gate keeps the deterministic forecast.
        self.fitted = bool(mask.any())
        if self.fitted:
            self.base.fit(x[mask], y[mask])
        return self

    def predict(self, x):
        mask, values = self.gate(x), np.zeros(len(x))
        if self.fitted and mask.any():
            values[mask] = self.base.predict(x[mask])
        return values


CANDIDATES = {
    'ridge': lambda: make_pipeline(StandardScaler(), Ridge(alpha=10.0)),
    'hgb_shallow': lambda: HistGradientBoostingRegressor(loss='absolute_error', max_iter=100, learning_rate=0.05, max_depth=3,
                                                         min_samples_leaf=100, random_state=0),
    'hgb_medium': lambda: HistGradientBoostingRegressor(loss='absolute_error', max_iter=200, learning_rate=0.05, max_depth=6,
                                                        min_samples_leaf=40, random_state=0),
    'hgb_deep': lambda: HistGradientBoostingRegressor(loss='absolute_error', max_iter=300, learning_rate=0.05, max_leaf_nodes=31,
                                                      min_samples_leaf=20, random_state=0),
    'random_forest': lambda: RandomForestRegressor(n_estimators=200, min_samples_leaf=20, max_features=0.5, random_state=0, n_jobs=-1),
    'hgb_medium_disruption_gated': lambda: DisruptionGated(CANDIDATES['hgb_medium']()),
}
CALIBRATIONS = ('none', 'sigmoid', 'isotonic')


def quantile_model(alpha):
    return HistGradientBoostingRegressor(loss='quantile', quantile=alpha, max_iter=200, learning_rate=0.05, max_depth=6,
                                         min_samples_leaf=40, random_state=0)


@dataclass(frozen=True)
class Model:
    estimator: str
    regressor: object
    lower: HistGradientBoostingRegressor
    upper: HistGradientBoostingRegressor
    margins: dict
    calibration: str
    classifier: object
    version: str
    dataset_version: str
    dataset_hash: str


def split(rows, seed=SPLIT_SEED):
    """Whole scenarios go to one split, stratified by family: no run, configuration or seed is shared."""
    groups = {name: set() for name, _ in SPLIT}
    rng = random.Random(seed)
    for family in FAMILIES:
        scenarios = sorted({row['scenario'] for row in rows if row['family'] == family})
        rng.shuffle(scenarios)
        start = 0
        for index, (name, share) in enumerate(SPLIT):
            end = len(scenarios) if index == len(SPLIT) - 1 else start + round(share * len(scenarios))
            groups[name].update(scenarios[start:end])
            start = end
    return {name: [row for row in rows if row['scenario'] in members] for name, members in groups.items()}


def matrix(rows):
    return np.array([[row[name] for name in FEATURES] for row in rows], dtype=float)


def column(rows, name):
    return np.array([row[name] for row in rows], dtype=float)


def residual(rows):
    return column(rows, 'observedQueue') - column(rows, 'deterministicQueue')


def risk_rows(rows, thresholds=RISK_THRESHOLDS):
    expanded = [(row, threshold) for row in rows for threshold in thresholds]
    x = np.array([[row[name] for name in FEATURES] + [threshold] for row, threshold in expanded], dtype=float)
    y = np.array([row['observedQueue'] > threshold for row, threshold in expanded], dtype=int)
    return x, y, expanded


def margin_rows(expanded):
    return np.array([[row['deterministicQueue'] - threshold, threshold] for row, threshold in expanded], dtype=float)


def conformal(scores, coverage=COVERAGE):
    """Split-conformal margin: the ⌈(n+1)·coverage⌉-th smallest score (capped at the largest for small n)."""
    rank = min(len(scores), math.ceil((len(scores) + 1) * coverage))
    return float(np.sort(scores)[rank - 1])


def mondrian(rows, low, high):
    """One conformal margin per scenario family, so coverage holds within each family, not only on average."""
    observed, families = column(rows, 'observedQueue'), np.array([row['family'] for row in rows])
    scores = np.maximum(low - observed, observed - high)
    return {family: conformal(scores[families == family]) for family in FAMILIES if (families == family).any()}


def select_calibration(raw, x, y, groups):
    """Choose none/sigmoid/isotonic by grouped two-fold cross-fitting inside the calibration split."""
    folds = list(GroupKFold(n_splits=2).split(x, groups=groups))
    scores = {'none': float(np.mean((raw.predict_proba(x)[:, 1] - y) ** 2))}
    for method in CALIBRATIONS[1:]:
        losses = []
        for fit, hold in folds:
            calibrated = CalibratedClassifierCV(FrozenEstimator(raw), method=method).fit(x[fit], y[fit])
            losses.append(np.mean((calibrated.predict_proba(x[hold])[:, 1] - y[hold]) ** 2))
        scores[method] = float(np.mean(losses))
    chosen = min(CALIBRATIONS, key=scores.get)
    return chosen, scores


def cross_validate(rows):
    x, y, deterministic = matrix(rows), column(rows, 'observedQueue'), column(rows, 'deterministicQueue')
    folds = list(GroupKFold(n_splits=CV_FOLDS).split(x, groups=[row['scenario'] for row in rows]))
    scores = {'deterministic': float(np.mean([np.abs(deterministic[hold] - y[hold]).mean() for _, hold in folds]))}
    for name, make in CANDIDATES.items():
        errors = []
        for fit, hold in folds:
            model = make().fit(x[fit], y[fit] - deterministic[fit])
            errors.append(np.abs(np.maximum(0.0, deterministic[hold] + model.predict(x[hold])) - y[hold]).mean())
        scores[name] = float(np.mean(errors))
    return scores


def fit_regressor(name, rows):
    return CANDIDATES[name]().fit(matrix(rows), residual(rows))


def train(dataset):
    """Select by grouped CV on train, fit on train, then conformalise and calibrate on the calibration split."""
    parts = split(dataset['rows'])
    train_rows, calibration = parts['train'], parts['calibration']
    scores = cross_validate(train_rows)
    estimator = min(CANDIDATES, key=scores.get)
    regressor = fit_regressor(estimator, train_rows)
    x, y = matrix(train_rows), residual(train_rows)
    lower, upper = quantile_model((1 - COVERAGE) / 2).fit(x, y), quantile_model((1 + COVERAGE) / 2).fit(x, y)
    deterministic = column(calibration, 'deterministicQueue')
    margins = mondrian(calibration, deterministic + lower.predict(matrix(calibration)), deterministic + upper.predict(matrix(calibration)))
    x, labels, expanded = risk_rows(train_rows)
    raw = HistGradientBoostingClassifier(max_iter=200, learning_rate=0.05, max_depth=6, min_samples_leaf=40, random_state=0).fit(x, labels)
    x_calibration, y_calibration, calibration_expanded = risk_rows(calibration)
    calibration_method, calibration_scores = select_calibration(raw, x_calibration, y_calibration,
                                                                [row['scenario'] for row, _ in calibration_expanded])
    fitted = {method: CalibratedClassifierCV(FrozenEstimator(raw), method=method).fit(x_calibration, y_calibration) for method in CALIBRATIONS[1:]}
    classifier = raw if calibration_method == 'none' else fitted[calibration_method]
    version = MODEL_NAME + '+' + fingerprint({'model': MODEL_NAME, 'estimator': estimator, 'calibration': calibration_method, 'dataset': dataset['hash'],
                                              'build': dataset['engineBuild'], 'sklearn': sklearn.__version__,
                                              'split': [SPLIT_SEED, [list(part) for part in SPLIT]]}, 'model')[:16]
    model = Model(estimator, regressor, lower, upper, margins, calibration_method, classifier, version, dataset['version'], dataset['hash'])
    extras = {'cv': scores, 'raw': raw, **fitted, 'calibrationSelection': calibration_scores,
              'margin_baseline': LogisticRegression().fit(margin_rows(expanded), labels),
              'deterministic_margins': mondrian(calibration, deterministic, deterministic),
              'base_rate': {t: float(labels[x[:, -1] == t].mean()) for t in RISK_THRESHOLDS}}
    return model, parts, extras


def predict_queue(model, rows, regressor=None):
    return np.maximum(0.0, column(rows, 'deterministicQueue') + (regressor or model.regressor).predict(matrix(rows)))


def predict_interval(model, rows):
    deterministic, x = column(rows, 'deterministicQueue'), matrix(rows)
    margin = np.array([model.margins[row['family']] for row in rows])
    low = np.maximum(0.0, deterministic + model.lower.predict(x) - margin)
    return low, np.maximum(low, deterministic + model.upper.predict(x) + margin)


def bootstrap(errors_a, errors_b, groups, seed=SPLIT_SEED):
    """Paired difference of mean loss (a − b) with a 95% interval resampling whole test scenarios."""
    keys = sorted(set(groups))
    by_group = {key: np.flatnonzero(groups == key) for key in keys}
    rng, values = np.random.default_rng(seed), []
    for _ in range(BOOTSTRAP):
        index = np.concatenate([by_group[key] for key in rng.choice(keys, size=len(keys))])
        values.append(errors_a[index].mean() - errors_b[index].mean())
    return {'difference': float(errors_a.mean() - errors_b.mean()),
            'ci95': [float(np.percentile(values, 2.5)), float(np.percentile(values, 97.5))]}


def reliability(probabilities, labels):
    bins, ece = [], 0.0
    edges = np.linspace(0, 1, BINS + 1)
    for index in range(BINS):
        mask = (probabilities >= edges[index]) & ((probabilities < edges[index + 1]) if index < BINS - 1 else (probabilities <= 1))
        if mask.any():
            mean, observed = float(probabilities[mask].mean()), float(labels[mask].mean())
            ece += mask.mean() * abs(mean - observed)
            bins.append({'from': float(edges[index]), 'to': float(edges[index + 1]), 'count': int(mask.sum()),
                         'meanPredicted': mean, 'observedRate': observed})
    return float(ece), bins


def by_family(values, families):
    return {family: float(values[families == family].mean()) for family in FAMILIES if (families == family).any()}


def importance(model, rows, seed=SPLIT_SEED):
    """MAE increase when one feature is permuted (the deterministic base stays intact)."""
    x, deterministic, y = matrix(rows), column(rows, 'deterministicQueue'), column(rows, 'observedQueue')
    error = lambda data: np.abs(np.maximum(0.0, deterministic + model.regressor.predict(data)) - y).mean()
    base, rng, scores = error(x), np.random.default_rng(seed), {}
    for index, name in enumerate(FEATURES):
        increases = []
        for _ in range(IMPORTANCE_REPEATS):
            shuffled = x.copy()
            shuffled[:, index] = rng.permutation(shuffled[:, index])
            increases.append(error(shuffled) - base)
        scores[name] = float(np.mean(increases))
    return dict(sorted(scores.items(), key=lambda item: -item[1]))


def learning_curve(model, parts, seed=SPLIT_SEED):
    scenarios = sorted({row['scenario'] for row in parts['train']})
    random.Random(seed).shuffle(scenarios)
    test, observed = parts['test'], column(parts['test'], 'observedQueue')
    curve = []
    for share in LEARNING_CURVE:
        chosen = set(scenarios[:max(CV_FOLDS, round(share * len(scenarios)))])
        rows = [row for row in parts['train'] if row['scenario'] in chosen]
        regressor = fit_regressor(model.estimator, rows)
        curve.append({'trainScenarios': len(chosen), 'trainRows': len(rows),
                      'testMae': float(np.abs(predict_queue(model, test, regressor) - observed).mean())})
    return curve


def evaluate(dataset):
    model, parts, extras = train(dataset)
    test = parts['test']
    observed = column(test, 'observedQueue')
    groups = np.array([row['scenario'] for row in test])
    families = np.array([row['family'] for row in test])
    horizons = column(test, 'horizonMinutes')
    predictions = {'persistence': column(test, 'initialQueue'), 'deterministic': column(test, 'deterministicQueue'),
                   'ml': predict_queue(model, test)}
    errors = {name: np.abs(values - observed) for name, values in predictions.items()}
    regression = {'metric': 'MAE (passengers)', 'selectedEstimator': model.estimator,
                  'crossValidation': {'folds': CV_FOLDS, 'unit': 'scenario', 'mae': extras['cv']},
                  'methods': {name: {'mae': float(error.mean()), 'maeByHorizon': {str(int(h)): float(error[horizons == h].mean()) for h in sorted(set(horizons))},
                                     'maeByFamily': by_family(error, families)} for name, error in errors.items()},
                  'differences': {f'ml-{name}': bootstrap(errors['ml'], errors[name], groups) for name in ('persistence', 'deterministic')},
                  'differencesByFamily': {family: bootstrap(errors['ml'][families == family], errors['deterministic'][families == family], groups[families == family])
                                          for family in FAMILIES if (families == family).any()},
                  'permutationImportance': importance(model, test), 'learningCurve': learning_curve(model, parts)}

    low, high = predict_interval(model, test)
    deterministic = predictions['deterministic']
    margin = np.array([extras['deterministic_margins'][family] for family in families])
    baseline = np.maximum(0.0, deterministic - margin), deterministic + margin
    intervals = {'nominalCoverage': COVERAGE, 'method': 'Mondrian (per family) conformalised quantile regression on the calibration split',
                 'marginsPassengers': {'ml_cqr': model.margins, 'deterministic_conformal': extras['deterministic_margins']}, 'methods': {}}
    for name, (lower, upper) in {'ml_cqr': (low, high), 'deterministic_conformal': baseline}.items():
        inside = ((observed >= lower) & (observed <= upper)).astype(float)
        intervals['methods'][name] = {'coverage': float(inside.mean()), 'meanWidth': float((upper - lower).mean()),
                                      'coverageByFamily': by_family(inside, families), 'widthByFamily': by_family(upper - lower, families)}

    x, y, expanded = risk_rows(test)
    thresholds = x[:, -1]
    risk_groups = np.array([row['scenario'] for row, _ in expanded])
    risk_families = np.array([row['family'] for row, _ in expanded])
    scores = {'base_rate': np.array([extras['base_rate'][threshold] for threshold in thresholds]),
              'deterministic_indicator': np.array([row['deterministicQueue'] > threshold for row, threshold in expanded], dtype=float),
              'logistic_margin': extras['margin_baseline'].predict_proba(margin_rows(expanded))[:, 1],
              'ml_uncalibrated': extras['raw'].predict_proba(x)[:, 1],
              'ml_sigmoid': extras['sigmoid'].predict_proba(x)[:, 1],
              'ml_isotonic': extras['isotonic'].predict_proba(x)[:, 1],
              'ml_served': model.classifier.predict_proba(x)[:, 1]}
    losses = {name: (probability - y) ** 2 for name, probability in scores.items()}
    clipped = {name: np.clip(probability, 1e-6, 1 - 1e-6) for name, probability in scores.items()}
    log_losses = {name: -(y * np.log(value) + (1 - y) * np.log(1 - value)) for name, value in clipped.items()}
    risk = {'event': 'observedQueue > threshold at the horizon', 'thresholds': list(RISK_THRESHOLDS),
            'served': f'ml_served ({model.calibration})', 'calibrationSelection': {'chosen': model.calibration, 'unit': 'scenario',
                                                                                  'brier': extras['calibrationSelection']}, 'methods': {},
            'differences': {f'ml_served-{name}': {'brier': bootstrap(losses['ml_served'], losses[name], risk_groups),
                                                  'logLoss': bootstrap(log_losses['ml_served'], log_losses[name], risk_groups)}
                            for name in ('deterministic_indicator', 'logistic_margin')}}
    for name, probability in scores.items():
        ece, bins = reliability(probability, y)
        risk['methods'][name] = {
            'brier': float(losses[name].mean()), 'logLoss': float(log_losses[name].mean()),
            'auc': float(roc_auc_score(y, probability)), 'ece': ece, 'reliability': bins,
            'brierByThreshold': {str(int(t)): float(losses[name][thresholds == t].mean()) for t in RISK_THRESHOLDS},
            'brierByFamily': by_family(losses[name], risk_families)}
    return {'isSynthetic': True, 'claim': CLAIM,
            'dataset': {'version': dataset['version'], 'hash': dataset['hash'], 'source': dataset['source'], 'spec': dataset['spec'],
                        'engineBuild': dataset['engineBuild'], 'rows': len(dataset['rows'])},
            'model': {'version': model.version, 'name': MODEL_NAME, 'estimator': model.estimator, 'sklearn': sklearn.__version__,
                      'features': list(FEATURES), 'candidates': list(CANDIDATES),
                      'target': 'observedQueue − deterministicQueue (residual), clipped at zero after adding back',
                      'calibration': model.calibration},
            'split': {'unit': 'scenario', 'stratifiedBy': 'family', 'seed': SPLIT_SEED,
                      **{name: {'rows': len(rows), 'scenarios': len({row['scenario'] for row in rows})} for name, rows in parts.items()}},
            'baselines': {'persistence': 'Q[t+h] = Q[t]', 'deterministic': FORMULA,
                          'deterministic_conformal': 'deterministic ± per-family split-conformal absolute residual',
                          'base_rate': 'train prevalence per threshold', 'deterministic_indicator': '1 if deterministic Q[t+h] > threshold',
                          'logistic_margin': 'logistic regression on (deterministic Q[t+h] − threshold, threshold)'},
            'regression': regression, 'intervals': intervals, 'risk': risk}


class Predictor:
    """Process-wide model, trained once in the background so requests never wait for a fit."""

    def __init__(self):
        self.lock, self.model, self.state, self.thread = threading.Lock(), None, 'idle', None

    def start(self):
        with self.lock:
            if self.state != 'idle':
                return
            self.state = 'warming'
            self.thread = threading.Thread(target=self._train, name='us038-model', daemon=True)
        self.thread.start()

    def _train(self):
        try:
            model = train(build_dataset(SPEC))[0]
        except Exception:
            logger.exception('Queue predictor training failed; serving the deterministic forecast')
            with self.lock:
                self.state = 'failed'
            return
        logger.info('Queue predictor model %s ready (estimator %s)', model.version, model.estimator)
        with self.lock:
            self.model, self.state = model, 'ready'

    def current(self):
        """(model, None) when ready, otherwise (None, fallback reason) — starting training if needed."""
        if not enabled():
            return None, 'predictor_disabled'
        self.start()
        with self.lock:
            return (self.model, None) if self.state == 'ready' else (None, 'model_unavailable' if self.state == 'failed' else 'model_warming')


PREDICTOR = Predictor()


def enabled():
    return os.getenv('QUEUE_ML_PREDICTOR', 'disabled').strip().lower() == 'enabled'


def predict(value, predictor=PREDICTOR):
    """Queue at the horizon per checkpoint; without a ready model the forecast is returned."""
    result, rows = features(value)
    threshold = value['options']['queueThresholdPassengers']
    threshold = int(threshold) if threshold is not None else None  # Wire numbers arrive as Decimal.
    model, reason = predictor.current()
    keys = sorted(rows)
    ordered_rows = [rows[key] for key in keys]
    if model is None:
        predictor_info = {'kind': 'deterministic', 'modelVersion': DETERMINISTIC_VERSION, 'estimator': None, 'datasetVersion': None,
                          'datasetHash': None, 'trainingDataSource': None, 'formula': FORMULA, 'fallbackReason': reason}
        queues, intervals, risks = column(ordered_rows, 'deterministicQueue'), None, None
    else:
        predictor_info = {'kind': 'ml', 'modelVersion': model.version, 'estimator': model.estimator, 'datasetVersion': model.dataset_version,
                          'datasetHash': model.dataset_hash, 'trainingDataSource': DATA_SOURCE, 'formula': None, 'fallbackReason': None}
        queues, intervals = predict_queue(model, ordered_rows), predict_interval(model, ordered_rows)
        risks = None if threshold is None else model.classifier.predict_proba(
            np.array([[row[name] for name in FEATURES] + [threshold] for row in ordered_rows], dtype=float))[:, 1]
    return {'contractVersion': '1.0', 'runId': result['runId'], 'kind': 'simulated_estimate', 'validity': CLAIM,
            'fromMinute': result['fromMinute'], 'toMinute': result['toMinute'], 'queueThresholdPassengers': threshold,
            'observationSource': value['observationSource'], 'predictor': predictor_info,
            'checkpoints': [{'checkpointId': key, 'deterministicQueuePassengers': rows[key]['deterministicQueue'],
                             'predictedQueuePassengers': float(queues[index]),
                             'predictionInterval': None if intervals is None else {
                                 'lowerPassengers': float(intervals[0][index]), 'upperPassengers': float(intervals[1][index]),
                                 'nominalCoverage': COVERAGE},
                             'exceedanceProbability': None if risks is None else float(risks[index])} for index, key in enumerate(keys)]}


def main():
    parser = argparse.ArgumentParser(description='Train and evaluate the queue models against simple baselines (synthetic data).')
    parser.add_argument('--output', help='write the JSON report here instead of stdout')
    args = parser.parse_args()
    report = json.dumps(evaluate(build_dataset(SPEC)), indent=2, ensure_ascii=False)
    if args.output:
        with open(args.output, 'w', encoding='utf-8') as handle:
            handle.write(report + '\n')
    else:
        sys.stdout.write(report + '\n')


if __name__ == '__main__':
    main()

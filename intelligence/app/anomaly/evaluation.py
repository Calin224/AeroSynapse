"""Synthetic test set with known perturbations, so false positives and negatives can be inspected.

Every scenario is replicated with fresh seeds to form its normal pattern (as a batch would).
Held-out seeds of the same plan are the negatives; the same plan with one injected event is a
positive. Default settings are chosen on the tuning scenarios; the test scenarios only report.
"""
import argparse
import json
import logging
import random
import sys
import threading
from uuid import UUID

from app.anomaly.detector import DATA_SCOPE, normalized, screen, window_samples
from app.contracts.models import SimulationRequest
from app.contracts.validation import validate
from app.forecast.dataset import SPEC as PLAN_SPEC, scenario
from app.simulation.engine import simulate
from app.simulation.forecast import forecast
from app.simulation.provenance import engine_build, fingerprint

logger = logging.getLogger('aerosynapse.intelligence.anomaly')
TEST_SET_VERSION = 'us039-synthetic-1.0'
SPEC = {'version': TEST_SET_VERSION, 'seed': 39, 'tuningScenarios': 8, 'testScenarios': 10, 'replications': 15,
        'normalRuns': 4, 'perturbedRuns': 4, 'onsetMinutes': [20, 60], 'effectMinutes': [8, 25], 'detectionGraceMinutes': 30}
KINDS = ('staff_reduction', 'checkpoint_unavailable', 'passenger_surge')
IMPACT_BUCKETS = ((0, 'under_5'), (5, '5_to_20'), (20, 'over_20'))
# Chosen on the tuning scenarios: the highest recall with run-level false-alarm rate ≤ 10% (see docs/product/algorithms-and-assumptions.md).
DEFAULT_SETTINGS = {'upperQuantile': 0.95, 'windowMinutes': 10, 'minExceedances': 6, 'minExcessPassengers': 10.0}
SWEEP = [{'upperQuantile': q, 'windowMinutes': window, 'minExceedances': needed, 'minExcessPassengers': excess}
         for q in (0.8, 0.9, 0.95) for window, needed in ((1, 1), (3, 2), (5, 3), (5, 4), (10, 6)) for excess in (0.0, 3.0, 10.0)]


def uuid(*parts):
    return str(UUID(int=int(''.join(f'{part:04d}' for part in parts))))


def reseeded(simulation, seed, run):
    """A replication: the captured plan with only the seed (and run identity) replaced."""
    value = json.loads(json.dumps(simulation))
    value['seed'] = value['passengerGeneration']['seed'] = seed
    value['runId'] = run
    return value


def perturb(simulation, rng, index):
    cp = rng.choice(simulation['checkpoints'])
    kind = rng.choice(KINDS)
    onset = rng.randint(*SPEC['onsetMinutes'])
    length = rng.randint(*SPEC['effectMinutes'])
    key = cp['checkpointId']
    if kind == 'staff_reduction' and cp['staffCount'] < 1:
        kind = 'passenger_surge'
    if kind == 'passenger_surge':
        length = min(length, 15)
        events = [{'type': kind, 'minute': onset, 'checkpointId': key, 'passengerCount': rng.randint(10, 120), 'windowMinutes': length}]
    elif kind == 'staff_reduction':
        events = [{'type': kind, 'minute': onset, 'checkpointId': key, 'staffCount': rng.randint(0, cp['staffCount'] - 1)},
                  {'type': 'resume', 'minute': onset + length, 'checkpointId': key}]
    else:
        events = [{'type': kind, 'minute': onset, 'checkpointId': key}, {'type': 'resume', 'minute': onset + length, 'checkpointId': key}]
    value = json.loads(json.dumps(simulation)) | {'events': [{'eventId': uuid(9, index, number), **event} for number, event in enumerate(events)]}
    label = {'kind': kind, 'checkpointId': key, 'onsetMinute': onset, 'effectEndMinute': onset + length,
             'impactPassengers': planned_impact(simulation, value, key)}
    label['impactBucket'] = [name for floor, name in IMPACT_BUCKETS if label['impactPassengers'] >= floor][-1]
    return value, label


def planned_impact(plan, perturbed, key):
    """Largest extra queue the fluid model expects from the event, on the planned demand."""
    def queues(simulation):
        backlogs = [{'checkpointId': cp['checkpointId'], 'queuedPassengers': 0, 'notArrivedPassengers': 0} for cp in simulation['checkpoints']]
        result = forecast({'simulation': simulation, 'observationSource': 'captured_snapshot', 'backlogs': backlogs,
                           'options': {'fromMinute': 0, 'horizonMinutes': simulation['durationMinutes'], 'queueThresholdPassengers': None}})
        return next(row for row in result['checkpoints'] if row['checkpointId'] == key)['points']
    return round(max(after['queuePassengers'] - before['queuePassengers'] for before, after in zip(queues(plan), queues(perturbed))), 6)


def series(simulation):
    """Aggregate per-checkpoint counts from the KPI snapshots; staff is the capacity in effect."""
    kpis = simulate(SimulationRequest.model_validate(simulation)).model_dump(mode='json', by_alias=True)['kpiResult']
    minutes = sorted({row['minute'] for row in kpis['snapshots']})
    rows = {}
    for row in sorted(kpis['snapshots'], key=lambda item: item['minute']):
        target = rows.setdefault(row['zoneId'], {'queued': [], 'arrived': [], 'staff': []})
        target['queued'].append(row['queuedPassengers'])
        target['arrived'].append(row['arrivedPassengers'])
        target['staff'].append(row.get('effectiveCapacity', row['capacity']))
    return minutes, rows


def build_test_set(spec=SPEC):
    rng, cases, scenarios = random.Random(spec['seed']), [], []
    total = spec['tuningScenarios'] + spec['testScenarios']
    for number in range(total):
        split = 'tuning' if number < spec['tuningScenarios'] else 'test'
        plan = scenario(3000 + number, rng, PLAN_SPEC, 'profiles')
        if validate('KpiSimulationRequest', plan):
            raise ValueError(f'Plan {number} violates the shared contract.')
        seeds = rng.sample(range(2**31), spec['replications'] + spec['normalRuns'] + spec['perturbedRuns'])
        replications, minutes = [], None
        for index, seed in enumerate(seeds[:spec['replications']]):
            run = uuid(10, number, index)
            minutes, rows = series(reseeded(plan, seed, run))
            replications.append({'runId': run, 'seed': seed, 'rows': rows})
        scenarios.append({'scenario': number, 'split': split, 'plan': plan, 'minutes': minutes, 'replications': replications})
        for offset, seed in enumerate(seeds[spec['replications']:]):
            run = uuid(11, number, offset)
            observed = reseeded(plan, seed, run)
            label = {'kind': 'normal'}
            if offset >= spec['normalRuns']:
                observed, label = perturb(observed, rng, number * 100 + offset)
                if validate('KpiSimulationRequest', observed):
                    raise ValueError(f'Perturbed run {run} violates the shared contract.')
            cases.append({'caseKey': f'{split}-{number:02d}-{offset:02d}', 'split': split, 'scenario': number, 'runId': run,
                          'seed': seed, 'label': label, 'simulation': observed, 'rows': series(observed)[1]})
    payload = [{'caseKey': case['caseKey'], 'label': case['label'], 'rows': case['rows']} for case in cases]
    return {'version': spec['version'], 'spec': spec, 'engineBuild': engine_build(), 'scenarios': scenarios, 'cases': cases,
            'hash': fingerprint({'version': spec['version'], 'spec': spec, 'cases': payload,
                                 'replications': [[rep['rows'] for rep in item['replications']] for item in scenarios]}, 'dataset')}


def detection_request(test_set, case, settings):
    """The same request a real run produces: observed counts and the replications of its normal pattern."""
    item = test_set['scenarios'][case['scenario']]
    return {'contractVersion': '1.0', 'dataScope': DATA_SCOPE, 'runId': case['runId'], 'intervalMinutes': 1, 'minutes': item['minutes'],
            'settings': settings, 'simulation': case['simulation'],
            'replicationRuns': [{'runId': rep['runId'], 'seed': rep['seed']} for rep in item['replications']],
            'checkpoints': [{'checkpointId': key, 'observed': case['rows'][key], 'replications': [rep['rows'][key] for rep in item['replications']]}
                            for key in sorted(case['rows'])]}


def episodes(test_set, case, settings):
    item, found = test_set['scenarios'][case['scenario']], []
    for key in sorted(case['rows']):
        result = screen(case['rows'][key]['queued'], [rep['rows'][key]['queued'] for rep in item['replications']], settings, 1)
        found += [{'checkpointId': key, 'onsetMinute': item['minutes'][episode['onset']], 'endMinute': item['minutes'][episode['end']],
                   'peakMinute': item['minutes'][episode['peak']], 'minPValue': episode['minPValue']} for episode in result['episodes']]
    return sorted(found, key=lambda episode: (episode['onsetMinute'], episode['checkpointId']))


def classify(case, found):
    label = case['label']
    if label['kind'] == 'normal':
        return {'outcome': 'false_positive' if found else 'true_negative', 'delayMinutes': None, 'preOnsetAlerts': 0}
    # Detected when an alert is active during the effect window, as the operator would see it; an alert already
    # running at the onset has delay 0. Only alerts that end before the onset are false alarms.
    window = (label['onsetMinute'], label['effectEndMinute'] + SPEC['detectionGraceMinutes'])
    hits = [episode for episode in found if episode['onsetMinute'] <= window[1] and episode['endMinute'] >= window[0]]
    early = sum(episode['endMinute'] < window[0] for episode in found)
    return {'outcome': 'true_positive' if hits else 'false_negative',
            'delayMinutes': max(0, min(episode['onsetMinute'] for episode in hits) - label['onsetMinute']) if hits else None,
            'preOnsetAlerts': early}


def rate(numerator, denominator):
    return numerator / denominator if denominator else None


def summarize(rows):
    count = {name: sum(row['outcome'] == name for row in rows) for name in ('true_positive', 'false_negative', 'false_positive', 'true_negative')}
    delays = sorted(row['delayMinutes'] for row in rows if row['delayMinutes'] is not None)
    return {'confusion': count, 'recall': rate(count['true_positive'], count['true_positive'] + count['false_negative']),
            'falsePositiveRate': rate(count['false_positive'], count['false_positive'] + count['true_negative']),
            'precision': rate(count['true_positive'], count['true_positive'] + count['false_positive']),
            'medianDelayMinutes': (delays[len(delays) // 2] + delays[(len(delays) - 1) // 2]) / 2 if delays else None,
            'preOnsetAlerts': sum(row['preOnsetAlerts'] for row in rows)}


def score(test_set, settings, split):
    rows = []
    for case in test_set['cases']:
        if case['split'] != split:
            continue
        found = episodes(test_set, case, settings)
        rows.append({'caseKey': case['caseKey'], 'scenario': case['scenario'], 'label': case['label'], **classify(case, found), 'episodes': found})
    return rows


def null_check(test_set, settings, split):
    """Leave-one-out alarm rate on replications against the observed false-alarm rate on held-out normal runs."""
    alerted = total = 0
    for item in test_set['scenarios']:
        if item['split'] != split:
            continue
        reps = item['replications']
        for held in range(len(reps)):
            found = sum(len(screen(reps[held]['rows'][key]['queued'], [rep['rows'][key]['queued'] for index, rep in enumerate(reps) if index != held],
                                   settings, 1)['episodes']) for key in reps[held]['rows'])
            alerted += found > 0
            total += 1
    return rate(alerted, total)


def evaluate(test_set, settings):
    settings = normalized(settings)
    rows = score(test_set, settings, 'test')
    # Positives only: recall and delay per perturbation kind and per planned impact.
    groups = lambda key: {name: summarize([row for row in rows if row['label'].get(key) == name])
                          for name in sorted({row['label'][key] for row in rows if key in row['label'] and row['label']['kind'] != 'normal'})}
    sweep = []
    for candidate in SWEEP:
        summary = summarize(score(test_set, candidate, 'test'))
        sweep.append({'settings': candidate, 'recall': summary['recall'], 'falsePositiveRate': summary['falsePositiveRate'],
                      'precision': summary['precision']})
    return {'status': 'ready', 'contractVersion': '1.0', 'dataScope': DATA_SCOPE, 'testSetVersion': test_set['version'],
            'testSetHash': test_set['hash'], 'settings': settings, 'windowSamples': window_samples(settings, 1),
            'replicationsPerScenario': test_set['spec']['replications'], 'scenarios': test_set['spec']['testScenarios'],
            **summarize(rows), 'byKind': groups('kind'), 'byImpact': groups('impactBucket'),
            'nullCheck': {'leaveOneOutAlarmRate': null_check(test_set, settings, 'test'),
                          'heldOutFalsePositiveRate': summarize(rows)['falsePositiveRate']},
            'sweep': sweep, 'cases': rows}


class TestSetHolder:
    """Built once per process in the background: the request returns 'warming' instead of blocking."""

    def __init__(self):
        self.lock, self.value, self.state, self.thread = threading.Lock(), None, 'idle', None

    def start(self):
        with self.lock:
            if self.state != 'idle':
                return
            self.state = 'warming'
            self.thread = threading.Thread(target=self._build, name='us039-test-set', daemon=True)
        self.thread.start()

    def _build(self):
        try:
            value = build_test_set()
        except Exception:
            logger.exception('Anomaly synthetic test set could not be built')
            with self.lock:
                self.state = 'failed'
            return
        logger.info('Anomaly synthetic test set %s ready (%d cases)', value['hash'][:12], len(value['cases']))
        with self.lock:
            self.value, self.state = value, 'ready'

    def current(self):
        self.start()
        with self.lock:
            return self.value, self.state


TEST_SET = TestSetHolder()


def evaluation(value):
    test_set, state = TEST_SET.current()
    if test_set is None:
        return {'status': 'warming' if state == 'warming' else 'unavailable', 'contractVersion': '1.0', 'dataScope': DATA_SCOPE}
    return evaluate(test_set, value['settings'])


def case_detail(value):
    from app.anomaly.detector import detect
    test_set, state = TEST_SET.current()
    if test_set is None:
        return {'status': 'warming' if state == 'warming' else 'unavailable', 'contractVersion': '1.0', 'dataScope': DATA_SCOPE}
    case = next((item for item in test_set['cases'] if item['caseKey'] == value['caseKey'] and item['split'] == 'test'), None)
    if case is None:
        return None
    settings = normalized(value['settings'])
    found = episodes(test_set, case, settings)
    return {'status': 'ready', 'contractVersion': '1.0', 'dataScope': DATA_SCOPE, 'caseKey': case['caseKey'], 'label': case['label'],
            **classify(case, found), 'detection': detect(detection_request(test_set, case, settings))}


def tune(test_set):
    """Default selection on the tuning split: false-alarm rate ≤ 10%, then highest recall, fewest false alarms,
    shortest median delay, and finally the more conservative quantile and excess."""
    results = []
    for candidate in SWEEP:
        summary = summarize(score(test_set, candidate, 'tuning'))
        results.append({'settings': candidate, 'recall': summary['recall'], 'falsePositiveRate': summary['falsePositiveRate'],
                        'medianDelayMinutes': summary['medianDelayMinutes']})
    eligible = [row for row in results if row['falsePositiveRate'] is not None and row['falsePositiveRate'] <= 0.10]
    key = lambda row: (row['recall'] or 0, -row['falsePositiveRate'], -(row['medianDelayMinutes'] if row['medianDelayMinutes'] is not None else 1e9),
                       row['settings']['upperQuantile'], row['settings']['minExcessPassengers'])
    return max(eligible, key=key) if eligible else None, results


def main():
    parser = argparse.ArgumentParser(description='Build the synthetic anomaly test set, tune defaults on the tuning split and report the test split.')
    parser.add_argument('--output', help='write the JSON report here instead of stdout')
    args = parser.parse_args()
    test_set = build_test_set()
    chosen, tuning = tune(test_set)
    if chosen is None or chosen['settings'] != DEFAULT_SETTINGS:
        sys.stderr.write(f'DEFAULT_SETTINGS differs from the tuning selection: {chosen}\n')
    report = {'isSynthetic': True, 'testSetVersion': test_set['version'], 'testSetHash': test_set['hash'],
              'engineBuild': test_set['engineBuild'], 'spec': test_set['spec'], 'tuning': {'selected': chosen, 'candidates': tuning},
              'defaultSettings': DEFAULT_SETTINGS, 'test': evaluate(test_set, DEFAULT_SETTINGS)}
    text = json.dumps(report, indent=2, ensure_ascii=False) + '\n'
    if args.output:
        with open(args.output, 'w', encoding='utf-8') as handle:
            handle.write(text)
    else:
        sys.stdout.write(text)
    return 0 if chosen is not None and chosen['settings'] == DEFAULT_SETTINGS else 1


if __name__ == '__main__':
    sys.exit(main())

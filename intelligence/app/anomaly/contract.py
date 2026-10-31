"""Wire invariants beyond the JSON schema, mirrored by AnomalyValidation.cs."""
import math

from app.anomaly.detector import ASSUMPTIONS, triggered_episodes, window_samples

TOLERANCE = 1e-6


def sample_minutes(horizon, interval):
    return [*range(0, int(horizon), int(interval)), int(horizon)]


def validate_settings(settings, interval, path, errors, error):
    if settings['minExceedances'] > window_samples(settings, interval):
        errors.append(error(path + '/settings/minExceedances', 'out_of_range'))


def validate_request(value, path, errors, error):
    from app.contracts.validation import semantics
    simulation, interval, minutes = value['simulation'], value['intervalMinutes'], value['minutes']
    semantics('KpiSimulationRequest', simulation, path + '/simulation', errors)
    validate_settings(value['settings'], interval, path, errors, error)
    if simulation['runId'] != value['runId'] or simulation.get('snapshotIntervalMinutes') != interval:
        errors.append(error(path + '/simulation', 'invalid_value'))
    if minutes != sample_minutes(simulation['durationMinutes'], interval):
        errors.append(error(path + '/minutes', 'invalid_value'))
    runs = value['replicationRuns']
    if (len({run['runId'] for run in runs}) != len(runs) or len({run['seed'] for run in runs}) != len(runs)
            or any(run['runId'] == value['runId'] for run in runs)):
        errors.append(error(path + '/replicationRuns', 'duplicate_id'))
    keys = [row['checkpointId'] for row in value['checkpoints']]
    if len(set(keys)) != len(keys) or set(keys) != {cp['checkpointId'] for cp in simulation['checkpoints']}:
        errors.append(error(path + '/checkpoints', 'invalid_value'))
    for index, row in enumerate(value['checkpoints']):
        field = f'{path}/checkpoints/{index}'
        if len(row['replications']) != len(runs):
            errors.append(error(field + '/replications', 'invalid_value'))
        for series in (row['observed'], *row['replications']):
            if any(len(series[name]) != len(minutes) for name in ('queued', 'arrived', 'staff')) \
                    or any(later < earlier for earlier, later in zip(series['arrived'], series['arrived'][1:])):
                errors.append(error(field, 'invalid_value'))
                break


def valid_result(value):
    settings, minutes, count = value['settings'], value['minutes'], value['replicationCount']
    width = window_samples(settings, value['intervalMinutes'])
    if (count != len(value['replicationRuns']) or not math.isclose(value['minimumPValue'], 1 / (count + 1), abs_tol=1e-12)
            or value['windowSamples'] != width or settings['minExceedances'] > width or sorted(value['assumptions']) != sorted(ASSUMPTIONS)
            or value['nullCalibration']['replicationCount'] != count or value['nullCalibration']['replicationsWithAlerts'] > count
            or any(later <= earlier for earlier, later in zip(minutes, minutes[1:]))
            or len({row['checkpointId'] for row in value['checkpoints']}) != len(value['checkpoints'])):
        return False
    for row in value['checkpoints']:
        if any(len(row[name]) != len(minutes) for name in ('observed', 'median', 'lower', 'upper', 'pValue', 'exceeds')):
            return False
        for index, observed in enumerate(row['observed']):
            if not row['lower'][index] <= row['median'][index] <= row['upper'][index] or row['pValue'][index] < value['minimumPValue'] - 1e-12:
                return False
            if row['exceeds'][index] != (observed > row['upper'][index] and observed - row['median'][index] >= settings['minExcessPassengers']):
                return False
        expected = triggered_episodes(row['exceeds'], width, settings['minExceedances'])
        if len(expected) != len(row['episodes']):
            return False
        for (onset, start, end), episode in zip(expected, row['episodes']):
            span = range(onset, end + 1)
            peak = minutes.index(episode['peakMinute']) if episode['peakMinute'] in minutes else -1
            if ((episode['onsetMinute'], episode['triggerMinute'], episode['endMinute']) != (minutes[onset], minutes[start], minutes[end])
                    or peak not in span or episode['peakObserved'] != row['observed'][peak] or episode['peakMedian'] != row['median'][peak]
                    or episode['peakUpper'] != row['upper'][peak] or episode['exceedingSamples'] != sum(row['exceeds'][position] for position in span)
                    or not math.isclose(episode['minPValue'], min(row['pValue'][position] for position in span), abs_tol=1e-12)):
                return False
            explanation = episode['explanation']
            if explanation is not None and (
                    abs(sum(explanation['contributions'].values()) - explanation['explained']) > TOLERANCE
                    or abs(explanation['observedExcess'] - explanation['explained'] - explanation['residual']) > TOLERANCE
                    or explanation['lookbackStartMinute'] > episode['peakMinute']):
                return False
    return True


def validate_result(value, path, errors, error):
    if not valid_result(value):
        errors.append(error(path, 'invalid_result'))


ENVELOPE = ('status', 'contractVersion', 'dataScope')
READY = {'AnomalyEvaluationResult': ('testSetVersion', 'testSetHash', 'settings', 'windowSamples', 'replicationsPerScenario', 'scenarios',
                                     'confusion', 'recall', 'falsePositiveRate', 'precision', 'medianDelayMinutes', 'preOnsetAlerts',
                                     'byKind', 'byImpact', 'nullCheck', 'sweep', 'cases'),
         'AnomalyCaseResult': ('caseKey', 'label', 'outcome', 'delayMinutes', 'preOnsetAlerts', 'detection')}


def complete(definition, value, path, errors, error):
    """A ready report carries every field; a warming or unavailable one carries only its status."""
    if value['status'] != 'ready':
        if set(value) - set(ENVELOPE):
            errors.append(error(path, 'invalid_result'))
        return False
    missing = [name for name in READY[definition] if name not in value]
    errors.extend(error(f'{path}/{name}', 'required') for name in missing)
    return not missing


def validate_evaluation(value, path, errors, error):
    if not complete('AnomalyEvaluationResult', value, path, errors, error):
        return
    validate_settings(value['settings'], 1, path, errors, error)
    confusion = value['confusion']
    if (sum(confusion.values()) != len(value['cases']) or value['windowSamples'] != window_samples(value['settings'], 1)
            or any(sum(case['outcome'] == name for case in value['cases']) != total for name, total in confusion.items())
            or any((case['label']['kind'] == 'normal') != (case['outcome'] in ('false_positive', 'true_negative')) for case in value['cases'])):
        errors.append(error(path, 'invalid_result'))


def validate_case(value, path, errors, error):
    if complete('AnomalyCaseResult', value, path, errors, error) \
            and (value['outcome'] in ('false_positive', 'true_negative')) != (value['label']['kind'] == 'normal'):
        errors.append(error(path, 'invalid_result'))

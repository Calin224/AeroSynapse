"""Separate normal variation from significant perturbations.

The normal pattern is the empirical distribution of Monte Carlo replications of the same captured
plan, never an assumed distribution. Only aggregate counts per checkpoint are read: no
passenger, person or identifier enters the detector.
"""
import itertools
import math

import numpy as np

from app.contracts.models import SimulationRequest
from app.passengers.generator import generate_population
from app.simulation.forecast import service_rates

DETECTOR_VERSION = 'us039-envelope-window-1.0'
DATA_SCOPE = 'aggregate_synthetic'
FACTORS = ('demand', 'capacity', 'backlog')
MIN_REPLICATIONS, MAX_REPLICATIONS = 5, 25
# Machine-readable assumptions; the UI renders their explanation next to the values that set them.
ASSUMPTIONS = ('replications_share_the_captured_plan', 'empirical_quantiles_nearest_rank', 'upper_tail_only',
               'minutes_are_not_independent_window_rule', 'fluid_model_explains_factors', 'aggregate_counts_only')


def nearest_rank(values, probability):
    """Convention: the element of rank ceil(p·n), counting from 1."""
    return values[max(0, math.ceil(probability * len(values)) - 1)]


def median(values):
    return values[len(values) // 2] / 2 + values[(len(values) - 1) // 2] / 2


def window_samples(settings, interval):
    return max(1, int(settings['windowMinutes']) // int(interval))


def envelope(replications, upper_quantile):
    """Per sample: median, lower and upper empirical quantiles of the replicated queues."""
    ordered = np.sort(np.asarray(replications, dtype=float), axis=0)
    columns = ordered.T.tolist()
    return ([median(column) for column in columns], [nearest_rank(column, 1 - upper_quantile) for column in columns],
            [nearest_rank(column, upper_quantile) for column in columns])


def triggered_episodes(exceeds, width, needed):
    """Maximal runs of samples whose trailing window holds ≥ needed exceedances, as (onset, trigger, end).

    The onset is the first exceedance inside the window that raised the alert."""
    triggered = [sum(exceeds[max(0, index - width + 1):index + 1]) >= needed for index in range(len(exceeds))]
    episodes, index = [], 0
    while index < len(exceeds):
        if triggered[index]:
            start = index
            while index + 1 < len(exceeds) and triggered[index + 1]:
                index += 1
            onset = next(position for position in range(max(0, start - width + 1), start + 1) if exceeds[position])
            episodes.append((onset, start, index))
        index += 1
    return episodes


def screen(observed, replications, settings, interval):
    """Exceedances, upper-tail p-values and windowed alert episodes for one checkpoint."""
    q, minimum = float(settings['upperQuantile']), float(settings['minExcessPassengers'])
    middle, lower, upper = envelope(replications, q)
    reps = np.asarray(replications, dtype=float)
    count = len(replications)
    p_values = [(1 + int((reps[:, index] >= value).sum())) / (count + 1) for index, value in enumerate(observed)]
    exceeds = [value > upper[index] and value - middle[index] >= minimum for index, value in enumerate(observed)]
    episodes = []
    for onset, start, end in triggered_episodes(exceeds, window_samples(settings, interval), int(settings['minExceedances'])):
        span = range(onset, end + 1)
        # Largest excess over the median; the earliest sample wins a tie.
        peak = max(span, key=lambda position: (observed[position] - middle[position], -position))
        episodes.append({'onset': onset, 'trigger': start, 'end': end, 'peak': peak,
                         'exceedingSamples': sum(exceeds[position] for position in span),
                         'minPValue': min(p_values[position] for position in span)})
    return {'median': middle, 'lower': lower, 'upper': upper, 'pValue': p_values, 'exceeds': exceeds, 'episodes': episodes}


def fluid(start, arrivals, staff, rate, minutes, first, last):
    """Recurrence over the captured sample grid: Q ← max(0, Q + arrivals − staff·rate·Δ)."""
    queue = float(start)
    for position in range(first, last):
        queue = max(0.0, queue + arrivals[position] - staff[position] * rate * (minutes[position + 1] - minutes[position]))
    return queue


def explain(observed, replications, rate, minutes, first, peak):
    """Exact Shapley split of the fluid model's excess over the expected inputs, plus the unexplained residual."""
    def interval_arrivals(series):
        return [series['arrived'][position + 1] - series['arrived'][position] for position in range(len(minutes) - 1)]
    expected = {
        'arrivals': [median(sorted(values)) for values in zip(*(interval_arrivals(rep) for rep in replications))],
        'staff': [median(sorted(values)) for values in zip(*(rep['staff'] for rep in replications))],
        'backlog': median(sorted(rep['queued'][first] for rep in replications)),
        'queuePeak': median(sorted(rep['queued'][peak] for rep in replications)),
    }
    actual = {'arrivals': interval_arrivals(observed), 'staff': list(observed['staff']), 'backlog': observed['queued'][first]}

    def value(players):
        return fluid(actual['backlog'] if 'backlog' in players else expected['backlog'],
                     actual['arrivals'] if 'demand' in players else expected['arrivals'],
                     actual['staff'] if 'capacity' in players else expected['staff'], rate, minutes, first, peak)

    contributions = {}
    for factor in FACTORS:
        others, total = [item for item in FACTORS if item != factor], 0.0
        for size in range(len(others) + 1):
            weight = math.factorial(size) * math.factorial(len(FACTORS) - size - 1) / math.factorial(len(FACTORS))
            for subset in itertools.combinations(others, size):
                total += weight * (value({*subset, factor}) - value(set(subset)))
        contributions[factor] = total
    explained = value(set(FACTORS)) - value(set())
    observed_excess = observed['queued'][peak] - expected['queuePeak']
    span = range(first, peak)
    staff_minutes = lambda staff: sum(staff[position] * (minutes[position + 1] - minutes[position]) for position in span)
    return {'lookbackStartMinute': minutes[first], 'contributions': contributions, 'explained': explained,
            'observedExcess': observed_excess, 'residual': observed_excess - explained,
            'inputs': {'arrivalsObserved': sum(actual['arrivals'][position] for position in span),
                       'arrivalsExpected': sum(expected['arrivals'][position] for position in span),
                       'staffMinutesObserved': staff_minutes(actual['staff']), 'staffMinutesExpected': staff_minutes(expected['staff']),
                       'backlogObserved': actual['backlog'], 'backlogExpected': expected['backlog'],
                       'serviceRatePerStaffMinute': rate}}


def checkpoint_report(row, settings, interval, minutes, rate):
    observed, replications = row['observed'], row['replications']
    result = screen(observed['queued'], [rep['queued'] for rep in replications], settings, interval)
    width = window_samples(settings, interval)
    episodes = []
    for episode in result['episodes']:
        first = max(0, episode['onset'] - width)
        peak = episode['peak']
        episodes.append({'onsetMinute': minutes[episode['onset']], 'triggerMinute': minutes[episode['trigger']],
                         'endMinute': minutes[episode['end']], 'peakMinute': minutes[peak],
                         'peakObserved': observed['queued'][peak], 'peakMedian': result['median'][peak], 'peakUpper': result['upper'][peak],
                         'minPValue': episode['minPValue'], 'exceedingSamples': episode['exceedingSamples'],
                         'explanation': explain(observed, replications, rate, minutes, first, peak) if peak > first else None})
    return {'checkpointId': row['checkpointId'], 'serviceRatePerStaffMinute': rate, 'observed': list(observed['queued']),
            'median': result['median'], 'lower': result['lower'], 'upper': result['upper'], 'pValue': result['pValue'],
            'exceeds': result['exceeds'], 'episodes': episodes}


def null_calibration(rows, settings, interval, minutes):
    """Leave-one-out on the replications themselves: how often the rule alarms on normal variation."""
    count = len(rows[0]['replications'])
    alerted, alerts = 0, 0
    for held in range(count):
        found = 0
        for row in rows:
            others = [rep['queued'] for index, rep in enumerate(row['replications']) if index != held]
            found += len(screen(row['replications'][held]['queued'], others, settings, interval)['episodes'])
        alerted += found > 0
        alerts += found
    hours = (minutes[-1] - minutes[0]) / 60
    return {'replicationCount': count, 'replicationsWithAlerts': alerted, 'alerts': alerts,
            'alertsPerReplicationHour': alerts / (count * hours) if hours else 0.0, 'method': 'leave_one_out'}


def rates_for(simulation):
    request = SimulationRequest.model_validate(simulation)
    population = generate_population(request.passenger_generation) if request.passenger_generation is not None else None
    return service_rates(request, population)[0]


def normalized(settings):
    """Wire numbers arrive as Decimal; the echo and the arithmetic use plain floats and integers."""
    return {'upperQuantile': float(settings['upperQuantile']), 'windowMinutes': int(settings['windowMinutes']),
            'minExceedances': int(settings['minExceedances']), 'minExcessPassengers': float(settings['minExcessPassengers'])}


def detect(value):
    settings, interval, minutes = normalized(value['settings']), int(value['intervalMinutes']), [int(minute) for minute in value['minutes']]
    rates = rates_for(value['simulation'])
    counts = lambda series: {name: [int(item) for item in series[name]] for name in ('queued', 'arrived', 'staff')}
    rows = sorted(({'checkpointId': row['checkpointId'], 'observed': counts(row['observed']),
                    'replications': [counts(rep) for rep in row['replications']]} for row in value['checkpoints']),
                  key=lambda row: row['checkpointId'])
    checkpoints = [checkpoint_report(row, settings, interval, minutes, rates[row['checkpointId']]) for row in rows]
    runs = [{'runId': run['runId'], 'seed': int(run['seed'])} for run in value['replicationRuns']]
    count = len(runs)
    return {'contractVersion': '1.0', 'runId': value['runId'], 'kind': 'anomaly_screening', 'dataScope': DATA_SCOPE,
            'detectorVersion': DETECTOR_VERSION, 'settings': settings, 'intervalMinutes': interval, 'minutes': minutes,
            'replicationRuns': runs, 'replicationCount': count, 'minimumPValue': 1 / (count + 1),
            'windowSamples': window_samples(settings, interval), 'assumptions': list(ASSUMPTIONS),
            'nullCalibration': null_calibration(rows, settings, interval, minutes), 'checkpoints': checkpoints}

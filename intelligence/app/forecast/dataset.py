"""Versioned synthetic dataset from SimPy runs.

Features come only from the forecast request: the snapshot at the origin and the
captured plan. The target is the queue the simulation observed h minutes later. History
before the origin is deliberately absent: the live monitor only sees the latest observation,
so a lag feature would differ between training and serving.
"""
import math
import random
from uuid import UUID

import numpy as np

from app.contracts.models import PassengerDistribution, SimulationRequest
from app.passengers.generator import quantile
from app.simulation.engine import simulate
from app.simulation.events import ordered
from app.simulation.forecast import forecast
from app.simulation.provenance import engine_build, fingerprint

DATASET_VERSION = 'us038-synthetic-2.0'
DATA_SOURCE = 'synthetic_simpy_simulation'
FAMILIES = ('aggregate', 'profiles', 'events')
# Every scenario draws its own configuration and seed, so the grouped split never shares a run.
SPEC = {'scenariosPerFamily': 90, 'families': list(FAMILIES), 'seed': 38, 'durationMinutes': 90, 'originStepMinutes': 5,
        'horizonsMinutes': [15, 30], 'checkpoints': [1, 3], 'staffPerCheckpoint': [1, 4], 'serviceRatePerStaffMinute': [0.5, 3.0],
        'utilization': [0.6, 1.3], 'flightsPerCheckpoint': [1, 3], 'engine': 'simpy-kpi-1.0'}
FEATURES = ('horizonMinutes', 'initialQueue', 'notArrived', 'staff', 'nominalStaff', 'minStaff', 'zeroCapacityMinutes',
            'capacityChanges', 'serviceRate', 'serviceCv', 'plannedArrivals', 'peakArrivals', 'arrivalsFrontShare',
            'plannedCapacity', 'netFlow', 'utilization', 'surgeArrivals', 'profiled', 'deterministicQueue', 'deterministicPeak')
GRID = (np.arange(4000) + 0.5) / 4000
SOLO = [{'kind': 'solo', 'weight': 1, 'minSize': 1, 'maxSize': 1, 'serviceMultiplier': 1, 'travelMultiplier': 1}]
TRAVEL = {'kind': 'constant', 'minimum': 1, 'maximum': 1, 'mean': 1, 'stdDev': 1, 'mode': 1}


def uuid(*parts):
    return str(UUID(int=int(''.join(f'{part:04d}' for part in parts))))


def checkpoints(index, rng, spec):
    return [{'checkpointId': uuid(1, index, slot), 'staffCount': rng.randint(*spec['staffPerCheckpoint']),
             'serviceRatePerMinute': round(rng.uniform(*spec['serviceRatePerStaffMinute']), 3)} for slot in range(rng.randint(*spec['checkpoints']))]


def service_distribution(rng):
    kind = rng.choice(('constant', 'uniform', 'normal', 'exponential'))
    if kind == 'constant':
        return {'kind': kind, 'minimum': 1, 'maximum': 1, 'mean': 1, 'stdDev': 1, 'mode': 1}
    if kind == 'uniform':
        spread = round(rng.uniform(0.2, 0.8), 3)
        return {'kind': kind, 'minimum': 1 - spread, 'maximum': 1 + spread, 'mean': 1, 'stdDev': spread / math.sqrt(3), 'mode': 1}
    if kind == 'normal':
        return {'kind': kind, 'minimum': 0.2, 'maximum': 2.5, 'mean': 1, 'stdDev': round(rng.uniform(0.15, 0.5), 3), 'mode': 1}
    return {'kind': kind, 'minimum': 0.05, 'maximum': 5, 'mean': 0.95, 'stdDev': 0.95, 'mode': 0.05}


def arrival_distribution(rng, close):
    low = rng.randint(0, max(0, close - 30))
    high = rng.randint(low + 20, close)
    mean = round(rng.uniform(low + 0.3 * (high - low), low + 0.7 * (high - low)), 3)
    kind = rng.choice(('uniform', 'normal', 'triangular'))
    return {'kind': kind, 'minimum': low, 'maximum': high, 'mean': mean, 'stdDev': round((high - low) / 4, 3), 'mode': mean}


def profiles(index, rng, spec, stations, seed):
    """Flight banks per checkpoint, each sized to load the control at the drawn utilisation over its arrival window."""
    flights, batches, duration = [], [], spec['durationMinutes']
    for slot, cp in enumerate(stations):
        count, utilization = rng.randint(*spec['flightsPerCheckpoint']), rng.uniform(*spec['utilization'])
        for number in range(count):
            close = rng.randint(50, duration)
            arrival = arrival_distribution(rng, close)
            # Each bank loads its own window, so overlapping banks build the peaks a schedule really has.
            size = max(1, min(600, round(utilization * cp['staffCount'] * cp['serviceRatePerMinute'] * (arrival['maximum'] - arrival['minimum']))))
            flight = uuid(2, index, slot * 10 + number)
            flights.append({'flightId': flight, 'passengerCapacity': size, 'boardingCloseMinute': close, 'isCancelled': False,
                            'checkpointId': cp['checkpointId'], 'routeId': uuid(3, index, slot), 'routeTravelMinutes': 1})
            batches.append({'flightId': flight, 'checkpointId': cp['checkpointId'], 'countMode': 'full', 'modeledCount': size,
                            'arrival': arrival, 'service': service_distribution(rng), 'travel': TRAVEL, 'groups': SOLO, 'surges': []})
    return {'generatorVersion': '1.0', 'seed': seed, 'durationMinutes': duration, 'checkpoints': stations,
            'flights': flights, 'batches': batches, 'isSynthetic': True}


def disruptions(index, rng, spec, stations):
    """At most one capacity disruption per checkpoint (so no event contradicts another), plus demand surges."""
    events, duration = [], spec['durationMinutes']
    for slot, cp in enumerate(stations):
        kind = rng.choice(('none', 'staff_reduction', 'checkpoint_unavailable'))
        start = rng.randint(10, 60)
        end = min(duration - 1, start + rng.randint(5, 25))
        if kind == 'staff_reduction':
            events.append({'type': kind, 'minute': start, 'checkpointId': cp['checkpointId'], 'staffCount': rng.randint(0, cp['staffCount'] - 1)})
        elif kind == 'checkpoint_unavailable':
            events.append({'type': kind, 'minute': start, 'checkpointId': cp['checkpointId']})
        if kind != 'none':
            events.append({'type': 'resume', 'minute': end, 'checkpointId': cp['checkpointId']})
    for _ in range(rng.randint(1, 2)):
        window, cp = rng.randint(5, 20), rng.choice(stations)
        events.append({'type': 'passenger_surge', 'minute': rng.randint(5, duration - window), 'checkpointId': cp['checkpointId'],
                       'passengerCount': rng.randint(10, 80), 'windowMinutes': window})
    return [{'eventId': uuid(4, index, number), **event} for number, event in enumerate(events)]


def scenario(index, rng, spec=SPEC, family='aggregate'):
    stations = checkpoints(index, rng, spec)
    duration = spec['durationMinutes']
    value = {'contractVersion': '1.0', 'runId': uuid(5, index, 0), 'scenarioId': uuid(6, index, 0), 'scenarioRevision': 1,
             'layoutVersionId': uuid(7, 0, 0), 'startsAtUtc': '2026-10-17T06:00:00Z', 'timeZone': 'UTC', 'durationMinutes': duration,
             'seed': rng.randrange(2**31), 'engine': spec['engine'], 'snapshotIntervalMinutes': 1, 'checkpoints': stations}
    if family == 'aggregate':
        # Aggregate arrivals are split round-robin, so demand targets the mean checkpoint capacity.
        capacity = sum(cp['staffCount'] * cp['serviceRatePerMinute'] for cp in stations) / len(stations)
        rate = round(rng.uniform(*spec['utilization']) * capacity * len(stations), 3)
        return value | {'passengerCount': round(rate * duration), 'arrivalRatePerMinute': rate}
    generation = profiles(index, rng, spec, stations, value['seed'])
    count = sum(batch['modeledCount'] for batch in generation['batches'])
    value |= {'passengerCount': count, 'arrivalRatePerMinute': count / duration, 'passengerGeneration': generation}
    return value | ({'events': disruptions(index, rng, spec, stations)} if family == 'events' else {})


def coefficient_of_variation(distribution):
    values = quantile(PassengerDistribution.model_validate(distribution), GRID)
    # libm/SIMD differ in the last ulp between platforms; rounding (as the generator does) keeps the dataset hash portable.
    return round(float(np.std(values) / np.mean(values)), 9)


def service_variation(simulation):
    """Service-time CV per checkpoint from the captured plan; aggregate demo service is exponential (CV 1)."""
    generation = simulation.get('passengerGeneration')
    if generation is None:
        return {cp['checkpointId']: 1.0 for cp in simulation['checkpoints']}
    totals = {cp['checkpointId']: [0.0, 0.0] for cp in simulation['checkpoints']}
    for batch in generation['batches']:
        if batch['modeledCount']:
            totals[batch['checkpointId']][0] += batch['modeledCount'] * coefficient_of_variation(batch['service'])
            totals[batch['checkpointId']][1] += batch['modeledCount']
    return {key: weighted / count if count else 0.0 for key, (weighted, count) in totals.items()}


def family_of(simulation):
    """The scenario family a request belongs to, known before any observation (used by Mondrian calibration)."""
    return 'events' if simulation.get('events') else 'profiles' if simulation.get('passengerGeneration') else 'aggregate'


def forecast_request(simulation, snapshots, origin, horizon, threshold=None, source='captured_snapshot'):
    backlogs = [{'checkpointId': cp['checkpointId'], 'queuedPassengers': snapshots[cp['checkpointId'], origin]['queuedPassengers'],
                 'notArrivedPassengers': snapshots[cp['checkpointId'], origin]['notArrivedPassengers']} for cp in simulation['checkpoints']]
    return {'simulation': simulation, 'observationSource': source, 'backlogs': backlogs,
            'options': {'fromMinute': origin, 'horizonMinutes': horizon, 'queueThresholdPassengers': threshold}}


def features(value):
    """The single feature path for training and serving: nothing after the origin is visible here."""
    result = forecast(value)
    # Wire payloads carry Decimal numbers; the validated model gives JSON-native ones.
    simulation = SimulationRequest.model_validate(value['simulation']).model_dump(mode='json', by_alias=True)
    origin = int(value['options']['fromMinute'])
    horizon = int(value['options']['horizonMinutes'])
    backlogs = {row['checkpointId']: row for row in value['backlogs']}
    nominal = {cp['checkpointId']: int(cp['staffCount']) for cp in simulation['checkpoints']}
    variation = service_variation(simulation)
    surges = {key: 0.0 for key in nominal}
    for event in ordered(simulation.get('events') or []):
        if event['type'] == 'passenger_surge':
            start, end = event['minute'], event['minute'] + event['windowMinutes']
            overlap = max(0, min(end, origin + horizon) - max(start, origin))
            surges[event['checkpointId']] += event['passengerCount'] * overlap / event['windowMinutes']
    rows = {}
    for row in result['checkpoints']:
        points, key = row['points'], row['checkpointId']
        arrivals = [p['arrivals'] for p in points]
        capacity, staffing = sum(p['capacity'] for p in points), [p['availableStaff'] for p in points]
        total = sum(arrivals)
        rows[key] = {
            'horizonMinutes': horizon, 'initialQueue': row['initialQueuePassengers'], 'notArrived': int(backlogs[key]['notArrivedPassengers']),
            'staff': staffing[0], 'nominalStaff': nominal[key], 'minStaff': min(staffing), 'zeroCapacityMinutes': sum(s == 0 for s in staffing),
            'capacityChanges': sum(a != b for a, b in zip(staffing, staffing[1:])), 'serviceRate': row['serviceRatePerStaffMinute'],
            'serviceCv': variation[key], 'plannedArrivals': total, 'peakArrivals': max(arrivals),
            'arrivalsFrontShare': sum(arrivals[:horizon // 2]) / total if total else 0.5, 'plannedCapacity': capacity,
            'netFlow': total - capacity, 'utilization': total / (capacity + 1), 'surgeArrivals': surges[key],
            'profiled': float(result['arrivalSource'] == 'planned_passenger_profiles'),
            'deterministicQueue': points[-1]['queuePassengers'], 'deterministicPeak': max(p['queuePassengers'] for p in points),
            'family': family_of(simulation)}
    return result, rows


def build_dataset(spec=SPEC):
    rng, rows = random.Random(spec['seed']), []
    for family in spec['families']:
        for number in range(spec['scenariosPerFamily']):
            index = FAMILIES.index(family) * 1000 + number
            simulation = scenario(index, rng, spec, family)
            if family_of(simulation) != family:
                raise ValueError(f'Scenario {index} does not belong to family {family}.')
            kpis = simulate(SimulationRequest.model_validate(simulation)).model_dump(mode='json', by_alias=True)['kpiResult']
            snapshots = {(row['zoneId'], row['minute']): row for row in kpis['snapshots']}
            for horizon in spec['horizonsMinutes']:
                for origin in range(spec['originStepMinutes'], spec['durationMinutes'] - horizon + 1, spec['originStepMinutes']):
                    _, features_by_checkpoint = features(forecast_request(simulation, snapshots, origin, horizon))
                    for key, row in sorted(features_by_checkpoint.items()):
                        rows.append({'scenario': index, 'checkpointId': key, 'fromMinute': origin, **row,
                                     'observedQueue': snapshots[key, origin + horizon]['queuedPassengers']})
    return {'version': DATASET_VERSION, 'source': DATA_SOURCE, 'spec': spec, 'engineBuild': engine_build(),
            'hash': fingerprint({'version': DATASET_VERSION, 'spec': spec, 'rows': rows}, 'dataset'), 'rows': rows}

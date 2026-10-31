"""Routed journeys: SimPy FIFO queues per stage; all times are simulated minutes.

A journey starts at arrivalMinute, walks transferMinutes, visits its explicit stages in order and walks
travelMinutes to the gate. Without a flow every passenger follows the base security → gate route.
"""
from datetime import timedelta

import numpy as np
import simpy

from app.contracts.models import PassengerGenerationRequest, SimulationResponse
from app.passengers.generator import generate_population

STAGES = ('bagDrop', 'security', 'assistedSecurity', 'transferSecurity')
SEGMENTS = ('standard', 'holdBaggage', 'reducedMobility', 'transfer')


def simulate_journeys(generation: PassengerGenerationRequest, passengers, closures=True):
    """Run every journey to completion; callers apply the horizon to the recorded times."""
    env = simpy.Environment()
    flow = generation.flow
    staff = {('security', str(checkpoint.checkpoint_id)): checkpoint.staff_count for checkpoint in generation.checkpoints}
    staff |= {(stage.kind, None): stage.staff_count for stage in flow.stages} if flow else {}
    # A stage without staff has no resource: its visitors stay queued, as in the v1 engine.
    resources = {key: simpy.Resource(env, capacity=count) if count else None for key, count in staff.items()}
    windows = {kind: merged(flow, kind) if closures else [] for kind in STAGES}
    journeys = [{'passenger': person, 'visits': [], 'gateMinute': None} for person in passengers]
    for journey in journeys:
        env.process(walk(env, resources, windows, journey))
    env.run()
    return journeys


def walk(env, resources, windows, journey):
    person = journey['passenger']
    yield env.timeout(person['arrivalMinute'] + person.get('transferMinutes', 0))
    for visit in person.get('stages', [{'stage': 'security', 'serviceMinutes': person['serviceMinutes']}]):
        zone = person['checkpointId'] if visit['stage'] == 'security' else None
        record = {'stage': visit['stage'], 'zoneId': zone, 'arrive': env.now, 'start': None, 'finish': None}
        journey['visits'].append(record)
        resource = resources[(visit['stage'], zone)]
        if resource is None:
            return
        with resource.request() as request:
            yield request
            # A blocked stage starts no service: services in progress finish, the queue head waits for reopening.
            # Looping absorbs a float sum that lands one ulp before the window end.
            while (reopens := next((ends for starts, ends in windows[visit['stage']] if starts <= env.now < ends), None)) is not None:
                yield env.timeout(reopens - env.now)
            record['start'] = env.now
            yield env.timeout(visit['serviceMinutes'])
        record['finish'] = env.now
    yield env.timeout(person['travelMinutes'])
    journey['gateMinute'] = env.now


class Timeline:
    """Counts at minute m with v1 semantics: queued arrived by m and did not start before m."""

    def __init__(self, visits):
        self.arrivals = np.sort([visit['arrive'] for visit in visits])
        self.starts = np.sort([visit['start'] for visit in visits if visit['start'] is not None])
        finished = sorted((visit['finish'], visit['start'] - visit['arrive']) for visit in visits if visit['finish'] is not None)
        self.finishes = np.array([finish for finish, _ in finished])
        self.waits = [wait for _, wait in finished]

    def arrived(self, minute):
        return int(np.searchsorted(self.arrivals, minute, 'right'))

    def queued(self, minute):
        return self.arrived(minute) - int(np.searchsorted(self.starts, minute, 'left'))

    def processed(self, minute):
        return int(np.searchsorted(self.finishes, minute, 'right'))

    def wait(self, minute):
        # Sequential addition in finish order matches the v1 engine's running total.
        total = 0.0
        for wait in self.waits[:self.processed(minute)]:
            total += wait
        return total


def checkpoint_timelines(generation: PassengerGenerationRequest):
    journeys = simulate_journeys(generation, generate_population(generation)['passengers'])
    visits = {str(checkpoint.checkpoint_id): [] for checkpoint in generation.checkpoints}
    for journey in journeys:
        for visit in journey['visits']:
            if visit['zoneId'] is not None:
                visits[visit['zoneId']].append(visit)
    return {zone: Timeline(items) for zone, items in visits.items()}


def simulate(request, on_progress=None):
    """Durable runs with stage routes keep the v1 checkpoint contract; other stages are in the flow report."""
    timelines = checkpoint_timelines(request.passenger_generation)
    zones = [str(checkpoint.checkpoint_id) for checkpoint in request.checkpoints]
    horizon = request.duration_minutes
    # At most 60 progress frames, at the same minutes as the other engines.
    step = max(1, (horizon + 59) // 60)
    for minute in [*range(step, horizon, step), horizon]:
        if on_progress is not None:
            on_progress({'simulationMinute': minute, 'zones': [
                {'zoneId': zone, 'queuedPassengers': timelines[zone].queued(minute), 'processedPassengers': timelines[zone].processed(minute)}
                for zone in zones]})
    recorded = (request.starts_at_utc + timedelta(minutes=horizon)).isoformat().replace('+00:00', 'Z')
    envelope = {'contractVersion': '1.0', 'runId': str(request.run_id), 'status': 'completed', 'simulatedMinutes': horizon,
                'metrics': [], 'snapshots': [], 'failure': None}
    for zone in zones:
        processed = timelines[zone].processed(horizon)
        envelope['metrics'].append({'zoneId': zone, 'recordedAtUtc': recorded,
            'meanWaitMinutes': round(timelines[zone].wait(horizon) / processed, 6) if processed else 0,
            'throughputPerMinute': round(processed / horizon, 6), 'processedPassengers': processed})
        envelope['snapshots'].append({'contractVersion': '1.0', 'zoneId': zone, 'elapsedMinutes': horizon,
            'queuedPassengers': timelines[zone].queued(horizon), 'processedPassengers': processed})
    return SimulationResponse.model_validate(envelope)


def report(generation: PassengerGenerationRequest):
    """Aggregate stage and passenger-type statistics; no passenger or group identifiers leave this function."""
    population = generate_population(generation)
    horizon = generation.duration_minutes
    flow = generation.flow
    journeys = simulate_journeys(generation, population['passengers'])
    unblocked = simulate_journeys(generation, population['passengers'], closures=False) if flow and flow.closures else journeys
    closes = {str(flight.flight_id): flight.boarding_close_minute for flight in generation.flights}
    defined = {'security', *(stage.kind for stage in flow.stages)} if flow else {'security'}
    visits = {kind: [] for kind in STAGES if kind in defined}
    for journey in journeys:
        for visit in journey['visits']:
            visits[visit['stage']].append(visit)
    stages = []
    for kind, items in visits.items():
        timeline = Timeline(items)
        processed = timeline.processed(horizon)
        waits = timeline.waits[:processed]
        stages.append({'stage': kind, 'arrivedPassengers': timeline.arrived(horizon), 'processedPassengers': processed,
                       'queuedPassengers': timeline.queued(horizon),
                       'meanWaitMinutes': round(timeline.wait(horizon) / processed, 6) if processed else 0,
                       'maxWaitMinutes': round(max(waits), 6) if waits else 0,
                       'blockedMinutes': round(blocked(flow, kind, horizon), 6)})
    configured = {segment.kind for segment in flow.segments} if flow else {'standard'}
    groups = {kind: [] for kind in SEGMENTS if kind in configured}
    for journey, baseline in zip(journeys, unblocked):
        groups[journey['passenger'].get('segment', 'standard')].append((journey, baseline))
    segments = []
    for kind, items in groups.items():
        reached = [journey for journey, _ in items if journey['gateMinute'] is not None and journey['gateMinute'] <= horizon]
        journey_minutes = [journey['gateMinute'] - journey['passenger']['arrivalMinute'] for journey in reached]
        on_time = sum(1 for journey, _ in items if journey['gateMinute'] is not None
                      and journey['gateMinute'] <= closes[journey['passenger']['flightId']])
        # Closure delay compares complete journeys with and without closures; stranded journeys are excluded.
        delays = [journey['gateMinute'] - baseline['gateMinute'] for journey, baseline in items
                  if journey['gateMinute'] is not None and baseline['gateMinute'] is not None]
        segments.append({'segment': kind, 'passengers': len(items), 'reachedGatePassengers': len(reached),
                         'missedBoardingPassengers': len(items) - on_time,
                         'meanJourneyMinutes': round(float(np.mean(journey_minutes)), 6) if reached else 0,
                         'maxJourneyMinutes': round(max(journey_minutes), 6) if reached else 0,
                         'meanQueueMinutes': round(float(np.mean([sum(visit['start'] - visit['arrive'] for visit in journey['visits'])
                                                                  for journey in reached])), 6) if reached else 0,
                         'meanClosureDelayMinutes': round(float(np.mean(delays)), 6) if delays else 0})
    return {'generatorVersion': population['generatorVersion'], 'seed': population['seed'], 'isSynthetic': True,
            'fingerprint': population['fingerprint'], 'durationMinutes': horizon, 'modeledCount': population['modeledCount'],
            'stages': stages, 'segments': segments}


def merged(flow, stage):
    """Union of the closure windows of one stage; security windows apply to every checkpoint."""
    windows = []
    for starts, ends in sorted((closure.starts_minute, closure.ends_minute)
                               for closure in (flow.closures if flow else []) if closure.stage == stage):
        if windows and starts <= windows[-1][1]:
            windows[-1] = (windows[-1][0], max(windows[-1][1], ends))
        else:
            windows.append((starts, ends))
    return windows


def blocked(flow, stage, horizon):
    return sum((min(ends, horizon) - starts for starts, ends in merged(flow, stage) if starts < horizon), 0.0)

"""Generator v1: local PCG64 streams keyed by seed, flight UUID and purpose; no wall clock or PII.

Passenger types only add explicit routes and multipliers; without a flow the output is unchanged."""
import hashlib
import json
import math
from statistics import NormalDist

import numpy as np

from app.contracts.models import PassengerCheckpointRoute, PassengerGenerationRequest

QUARTER_HOUR = 15


def stream(seed, flight_id, purpose):
    digest = hashlib.sha256(f'us019/v1/{flight_id}/{purpose}'.encode('ascii')).digest()
    words = [int.from_bytes(digest[index:index + 4], 'little') for index in range(0, 32, 4)]
    return np.random.Generator(np.random.PCG64(np.random.SeedSequence([seed, *words])))


def sample(distribution, rng, size):
    if distribution.kind == 'constant':
        return np.full(size, distribution.minimum)
    # Quantiles are drawn once; bounded inverse CDFs avoid rejection loops and clipping artifacts.
    return quantile(distribution, rng.random(size))


def quantile(distribution, u):
    lo, hi = distribution.minimum, distribution.maximum
    kind = distribution.kind
    if kind == 'constant':
        return np.full(np.shape(u), lo)
    if kind == 'uniform':
        return lo + u * (hi - lo)
    if kind == 'normal':
        normal = NormalDist(distribution.mean, distribution.std_dev)
        lower, upper = normal.cdf(lo), normal.cdf(hi)
        return np.array([normal.inv_cdf(lower + float(value) * (upper - lower)) for value in u])
    if kind == 'exponential':
        mass = -math.expm1(-(hi - lo) / distribution.mean)
        return lo - distribution.mean * np.log1p(-u * mass)
    mode = distribution.mode
    threshold = (mode - lo) / (hi - lo)
    return np.where(u < threshold, lo + np.sqrt(u * (hi - lo) * (mode - lo)),
                    hi - np.sqrt((1 - u) * (hi - lo) * (hi - mode)))


def generate_population(request: PassengerGenerationRequest):
    flights = {flight.flight_id: flight for flight in request.flights}
    checkpoints = {checkpoint.checkpoint_id: checkpoint for checkpoint in request.checkpoints}
    flow = request.flow
    # Ordered by behaviour, not label, so renaming a type never reassigns groups.
    segments = sorted(flow.segments, key=behaviour) if flow else []
    stages = {stage.kind: stage for stage in sorted(flow.stages, key=lambda item: item.kind)} if flow else {}
    passengers, batches = [], []
    for batch in sorted(request.batches, key=lambda item: str(item.flight_id)):
        flight = flights[batch.flight_id]
        lanes = open_lanes(flight, checkpoints)
        load = {lane.checkpoint_id: 0 for lane in lanes}
        groups = sorted(batch.groups, key=lambda item: item.kind)
        surges = sorted(batch.surges, key=lambda item: (item.starts_minute, item.ends_minute, item.share))
        group_rng = stream(request.seed, flight.flight_id, 'groups')
        arrival_rng = stream(request.seed, flight.flight_id, 'arrival')
        surge_rng = stream(request.seed, flight.flight_id, 'surges')
        service_rng = stream(request.seed, flight.flight_id, 'service')
        travel_rng = stream(request.seed, flight.flight_id, 'travel')
        services = sample(batch.service, service_rng, batch.modeled_count)
        if flow is not None:
            # Streams are separate, so configuring types never shifts the draws.
            segment_rng = stream(request.seed, flight.flight_id, 'segments')
            transfer_rng = stream(request.seed, flight.flight_id, 'transfer')
            stage_services = {kind: sample(stage.service, stream(request.seed, flight.flight_id, 'stage/' + kind), batch.modeled_count)
                              for kind, stage in stages.items()}
        segment = None
        person, group_index, surge_count = 0, 0, 0
        while person < batch.modeled_count:
            remaining = batch.modeled_count - person
            eligible = [group for group in groups if group.min_size <= remaining]
            weights = np.array([group.weight for group in eligible], dtype=float)
            group = eligible[int(group_rng.choice(len(eligible), p=weights / weights.sum()))]
            size = int(group_rng.integers(group.min_size, min(group.max_size, remaining) + 1))
            group_index += 1
            arrival = float(sample(batch.arrival, arrival_rng, 1)[0])
            draw, in_surge = float(surge_rng.random()), False
            cumulative = 0.0
            for surge in surges:
                cumulative += surge.share
                if draw < cumulative:
                    arrival = surge.starts_minute + float(surge_rng.random()) * (surge.ends_minute - surge.starts_minute)
                    in_surge = True
                    surge_count += size
                    break
            # A group shares one lane: the open checkpoint with the least load for its capacity, so a
            # spread flight splits in proportion to staffed capacity. No draw, so no stream shifts.
            lane = min(lanes, key=lambda item: (load[item.checkpoint_id] + size)
                       / (checkpoints[item.checkpoint_id].staff_count * checkpoints[item.checkpoint_id].service_rate_per_minute or 1))
            load[lane.checkpoint_id] += size
            checkpoint = checkpoints[lane.checkpoint_id]
            travel = float(sample(batch.travel, travel_rng, 1)[0]) * lane.route_travel_minutes * group.travel_multiplier
            if flow is not None:
                # Members of a group share type, transfer walk and route; services stay individual.
                segment = choose(segments, float(segment_rng.random()))
                transfer = float(quantile(segment.transfer_minutes, transfer_rng.random(1))[0]) if segment.transfer_minutes else 0.0
                travel *= segment.travel_multiplier
            scale = group.service_multiplier * (segment.service_multiplier if segment else 1)
            for _ in range(size):
                passenger = {
                    'id': f'demo-pax-{flight.flight_id.hex}-{person + 1:06}', 'flightId': str(flight.flight_id),
                    'checkpointId': str(checkpoint.checkpoint_id), 'routeId': str(lane.route_id),
                    'groupId': f'demo-group-{flight.flight_id.hex}-{group_index:06}', 'groupKind': group.kind,
                    'arrivalMinute': min(max(round(arrival, 9), batch.arrival.minimum), batch.arrival.maximum),
                    'serviceMinutes': round(float(services[person]) * scale / checkpoint.service_rate_per_minute, 12),
                    'travelMinutes': max(round(travel, 12), batch.travel.minimum * lane.route_travel_minutes * group.travel_multiplier
                                         * (segment.travel_multiplier if segment else 1)),
                    'inSurge': in_surge, 'isSynthetic': True,
                }
                if segment is not None:
                    route = (['bagDrop'] if segment.hold_baggage else []) + [segment.control]
                    visits = [{'stage': kind, 'serviceMinutes': passenger['serviceMinutes'] if kind == 'security'
                               else round(float(stage_services[kind][person]) * scale, 12)} for kind in route]
                    # serviceMinutes is the control service, at the checkpoint or its dedicated lane.
                    passenger |= {'serviceMinutes': visits[-1]['serviceMinutes'], 'segment': segment.kind,
                                  'transferMinutes': round(transfer, 12), 'stages': visits}
                passengers.append(passenger)
                person += 1
        batches.append({'flightId': str(flight.flight_id), 'configuredCount': flight.passenger_capacity,
                        'modeledCount': batch.modeled_count, 'unmodeledCount': flight.passenger_capacity - batch.modeled_count,
                        'countMode': batch.count_mode, 'groupCount': group_index, 'surgePassengerCount': surge_count, 'isSynthetic': True})
    passengers.sort(key=lambda item: (item['arrivalMinute'], item['flightId'], item['id']))
    fingerprint = hashlib.sha256(json.dumps({'generatorVersion': request.generator_version, 'seed': request.seed, 'passengers': passengers},
                                           sort_keys=True, separators=(',', ':'), allow_nan=False).encode('utf-8')).hexdigest()
    demand = []
    by_checkpoint = {}
    for passenger in passengers:
        by_checkpoint.setdefault(passenger['checkpointId'], []).append(passenger)
    for checkpoint in sorted(request.checkpoints, key=lambda item: str(item.checkpoint_id)):
        # Dedicated control lanes do not load the checkpoint queue.
        items = [item for item in by_checkpoint.get(str(checkpoint.checkpoint_id), [])
                 if item.get('stages', [{'stage': 'security'}])[-1]['stage'] == 'security']
        arrivals = np.array([item['arrivalMinute'] for item in items])
        buckets = np.floor(np.minimum(arrivals, request.duration_minutes - 1e-9)).astype(int)
        peak = int(np.bincount(buckets).max()) if items else 0
        # The same arrivals per quarter hour, so staffing can follow the waves of a long day.
        quarters = np.bincount(buckets // QUARTER_HOUR, minlength=-(-request.duration_minutes // QUARTER_HOUR)) if items \
            else np.zeros(-(-request.duration_minutes // QUARTER_HOUR), dtype=int)
        mean_service = float(np.mean([item['serviceMinutes'] for item in items])) if items else 0.0
        capacity = checkpoint.staff_count / mean_service if mean_service else 0.0
        demand.append({'checkpointId': str(checkpoint.checkpoint_id), 'modeledCount': len(items),
                       'peakArrivalsPerMinute': peak, 'arrivalsPerQuarterHour': [int(count) for count in quarters],
                       'meanServiceMinutes': round(mean_service, 12),
                       'staffCount': checkpoint.staff_count, 'estimatedCapacityPerMinute': round(capacity, 9),
                       'overloaded': bool(items and (checkpoint.staff_count == 0 or peak > capacity))})
    configured = sum(batch['configuredCount'] for batch in batches)
    return {'generatorVersion': request.generator_version, 'seed': request.seed, 'isSynthetic': True, 'rng': 'PCG64',
            'numpyVersion': np.__version__, 'fingerprint': fingerprint, 'configuredCount': configured,
            'modeledCount': len(passengers), 'hasSubset': len(passengers) != configured,
            'batches': batches, 'checkpointDemand': demand, 'passengers': passengers}


def open_lanes(flight, checkpoints):
    """The flight's own route, or for a spread batch every staffed checkpoint routed to its gate.

    Lanes are ordered by checkpoint so input order never changes an assignment. A spread flight
    with no staffed checkpoint keeps its own route, where passengers wait as with a fixed batch."""
    home = PassengerCheckpointRoute.model_construct(checkpoint_id=flight.checkpoint_id, route_id=flight.route_id,
                                                    route_travel_minutes=flight.route_travel_minutes)
    staffed = [route for route in flight.checkpoint_routes or [] if checkpoints[route.checkpoint_id].staff_count > 0]
    return sorted(staffed, key=lambda route: str(route.checkpoint_id)) or [home]


def behaviour(segment):
    transfer = segment.transfer_minutes
    return (segment.share, segment.hold_baggage, segment.control, segment.service_multiplier, segment.travel_multiplier,
            () if transfer is None else (transfer.kind, transfer.minimum, transfer.maximum, transfer.mean, transfer.std_dev, transfer.mode),
            segment.kind)


def choose(segments, value):
    cumulative = 0.0
    for segment in segments:
        cumulative += segment.share
        if value < cumulative:
            return segment
    return segments[-1]


def page(population, offset=0, limit=100):
    passengers = population['passengers'][offset:offset + limit]
    return population | {'passengers': passengers, 'offset': offset, 'returnedCount': len(passengers),
                         'hasMore': offset + len(passengers) < population['modeledCount']}

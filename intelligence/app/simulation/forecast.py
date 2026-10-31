"""One-minute fluid queue estimates from captured plans, never future observations."""
import math
from datetime import timedelta

from app.contracts.models import SimulationRequest
from app.passengers.generator import generate_population
from app.simulation.events import ordered, planned_at

FORMULA = 'Q[t+1]=max(0,Q[t]+arrivals-capacity)'
MAX_EXTRA_STAFF = 10000


def project_queue(initial, arrivals, capacities, origin, threshold):
    origin = int(origin)
    queue, points, alert = float(initial), [], None
    for minute, (arriving, capacity) in enumerate(zip(arrivals, capacities, strict=True), origin + 1):
        arriving, capacity = float(arriving), float(capacity)
        queue = max(0.0, queue + arriving - capacity)
        points.append({'minute': minute, 'arrivals': arriving, 'capacity': capacity, 'queuePassengers': queue})
        if threshold is not None and queue > threshold and alert is None:
            alert = {'minute': minute, 'queuePassengers': queue, 'thresholdPassengers': threshold}
    return points, alert


def peak_with_extra_staff(initial, points, rate, extra):
    # Extra operators join staffed lanes only; closed or unstaffed minutes keep zero capacity.
    queue, peak = float(initial), 0.0
    for point in points:
        staff = int(point['availableStaff'])
        queue = max(0.0, queue + float(point['arrivals']) - ((staff + extra) * float(rate) if staff > 0 else 0.0))
        peak = max(peak, queue)
    return peak


def mitigate(initial, points, rate, threshold):
    """Smallest extra staffing that keeps every estimated minute at or below the threshold."""
    if threshold is None:
        return None
    if peak_with_extra_staff(initial, points, rate, MAX_EXTRA_STAFF) > threshold:
        return {'extraStaff': None, 'peakQueuePassengers': None}
    low, high = 0, MAX_EXTRA_STAFF
    while low < high:  # The peak never increases with more staff, so the search is monotone.
        middle = (low + high) // 2
        low, high = (low, middle) if peak_with_extra_staff(initial, points, rate, middle) <= threshold else (middle + 1, high)
    return {'extraStaff': low, 'peakQueuePassengers': peak_with_extra_staff(initial, points, rate, low)}


def service_rates(request, population=None):
    """Passengers per staff-minute per checkpoint: the sampled service mean of captured profiles, else the configured rate."""
    rates = {str(cp.checkpoint_id): cp.service_rate_per_minute for cp in request.checkpoints}
    sources = {key: 'configured_service_rate' for key in rates}
    if population is not None:
        for row in population['checkpointDemand']:
            if row['meanServiceMinutes'] > 0:
                rates[row['checkpointId']] = 1 / row['meanServiceMinutes']
                sources[row['checkpointId']] = 'sampled_service_mean'
    return rates, sources


def forecast(value):
    request = SimulationRequest.model_validate(value['simulation'])
    options = value['options']
    origin, horizon, threshold = (options[key] for key in ('fromMinute', 'horizonMinutes', 'queueThresholdPassengers'))
    origin, horizon = int(origin), int(horizon)
    threshold = int(threshold) if threshold is not None else None
    backlogs = {row['checkpointId']: row for row in value['backlogs']}
    checkpoints = {str(cp.checkpoint_id): cp for cp in request.checkpoints}
    arrivals = {key: [0.0] * horizon for key in checkpoints}
    population = generate_population(request.passenger_generation) if request.passenger_generation is not None else None
    rates, rate_sources = service_rates(request, population)
    if population is not None:
        # Snapshots include arrivals at their minute; the future bins are (T, T+1].
        for person in population['passengers']:
            arrival = person['arrivalMinute']
            if origin < arrival <= origin + horizon:
                arrivals[person['checkpointId']][math.ceil(arrival - origin) - 1] += 1
    else:
        # Aggregate demand is an expected rate, capped by the observed remaining population.
        for key in checkpoints:
            remaining = int(backlogs[key]['notArrivedPassengers'])
            for index in range(horizon):
                arrivals[key][index] = min(remaining, request.arrival_rate_per_minute / len(checkpoints))
                remaining -= arrivals[key][index]

    events = ordered([event.wire() for event in request.events or []])
    for event in events:
        if event['type'] != 'passenger_surge':
            continue
        start, end = event['minute'], event['minute'] + event['windowMinutes']
        for index in range(horizon):
            left = origin + index
            overlap = max(0, min(left + 1, end) - max(left, start))
            arrivals[event['checkpointId']][index] += event['passengerCount'] * overlap / event['windowMinutes']

    schedules = {str(item.checkpoint_id): item.steps for item in request.staff_schedules or []}
    series = []
    for key, checkpoint in sorted(checkpoints.items()):
        staff, available, position = checkpoint.staff_count, True, 0
        steps = schedules.get(key)
        capacities = []
        staffing = []
        for left in range(origin, origin + horizon):
            # Events take effect at the start of their minute, including events before the origin.
            while position < len(events) and events[position]['minute'] <= left:
                event = events[position]
                if event.get('checkpointId') == key:
                    if event['type'] == 'checkpoint_unavailable': available = False
                    elif event['type'] == 'staff_reduction': staff = event['staffCount']
                    elif event['type'] == 'resume': staff, available = checkpoint.staff_count, True
                position += 1
            # A scheduled checkpoint takes no staff events: its staffing is the step in effect.
            if steps: staff = planned_at(checkpoint, steps, left)
            staffing.append(staff if available else 0)
            capacities.append(staffing[-1] * rates[key])
        initial = int(backlogs[key]['queuedPassengers'])
        points, alert = project_queue(initial, arrivals[key], capacities, origin, threshold)
        for point, count in zip(points, staffing, strict=True):
            point['availableStaff'] = count
        series.append({'checkpointId': key, 'initialQueuePassengers': initial, 'points': points, 'alert': alert,
                       'mitigation': mitigate(initial, points, rates[key], threshold),
                       'serviceRatePerStaffMinute': rates[key], 'serviceRateSource': rate_sources[key]})

    timestamp = lambda minute: (request.starts_at_utc + timedelta(minutes=minute)).isoformat().replace('+00:00', 'Z')
    return {'contractVersion': '1.0', 'runId': str(request.run_id), 'kind': 'simulated_estimate', 'formula': FORMULA,
            'fromMinute': origin, 'toMinute': origin + horizon, 'stepMinutes': 1,
            'periodStartUtc': timestamp(origin), 'periodEndUtc': timestamp(origin + horizon),
            'queueUnit': 'passengers', 'flowUnit': 'passengers/minute', 'timeUnit': 'simulated minutes',
            'queueThresholdPassengers': threshold, 'observationSource': value['observationSource'],
            'arrivalSource': 'planned_passenger_profiles' if request.passenger_generation else 'configured_arrival_rate',
            'checkpoints': series}


def validate_request(value, path, errors, error):
    from app.contracts.validation import semantics
    simulation, options = value['simulation'], value['options']
    semantics('KpiSimulationRequest', simulation, path + '/simulation', errors)
    if simulation.get('passengerGeneration', {}).get('flow') is not None:
        errors.append(error(path + '/simulation/passengerGeneration/flow', 'invalid_value'))
    origin = options['fromMinute']
    if origin + options['horizonMinutes'] > simulation['durationMinutes']:
        errors.append(error(path + '/options/fromMinute', 'out_of_range'))
    keys = [row['checkpointId'] for row in value['backlogs']]
    if len(set(keys)) != len(keys):
        errors.append(error(path + '/backlogs', 'duplicate_id'))
    if set(keys) != {cp['checkpointId'] for cp in simulation['checkpoints']}:
        errors.append(error(path + '/backlogs', 'invalid_value'))
    population = simulation['passengerCount'] + sum(event.get('passengerCount', 0) for event in simulation.get('events', []))
    if sum(row['queuedPassengers'] + row['notArrivedPassengers'] for row in value['backlogs']) > population:
        errors.append(error(path + '/backlogs', 'out_of_range'))


def validate_result(value, path, errors, error):
    from datetime import datetime
    horizon = int(value['toMinute'] - value['fromMinute'])
    valid = 15 <= horizon <= 30
    try:
        start, end = (datetime.fromisoformat(value[key].replace('Z', '+00:00')) for key in ('periodStartUtc', 'periodEndUtc'))
    except (ValueError, OverflowError):
        errors.append(error(path, 'invalid_result'))
        return
    valid = valid and end - start == timedelta(minutes=horizon)
    keys = [row['checkpointId'] for row in value['checkpoints']]
    valid = valid and len(set(keys)) == len(keys)
    for row in value['checkpoints']:
        points, alert = project_queue(row['initialQueuePassengers'], [point['arrivals'] for point in row['points']],
                                      [point['capacity'] for point in row['points']], value['fromMinute'], value['queueThresholdPassengers'])
        actual_points = [{key: float(point[key]) for key in ('minute', 'arrivals', 'capacity', 'queuePassengers')} for point in row['points']]
        actual_alert = None if row['alert'] is None else {key: float(number) for key, number in row['alert'].items()}
        valid = valid and len(points) == horizon and points == actual_points and alert == actual_alert
        valid = valid and all(float(point['capacity']) == int(point['availableStaff']) * float(row['serviceRatePerStaffMinute']) for point in row['points'])
        valid = valid and row['mitigation'] == mitigate(row['initialQueuePassengers'], row['points'], row['serviceRatePerStaffMinute'], value['queueThresholdPassengers'])
    if not valid:
        errors.append(error(path, 'invalid_result'))

"""Scheduled operational events: one deterministic order and one contradiction check.

An event at minute T acts at the start of T, before any arrival, grant or completion at T, so every
interval before T is unchanged. Simultaneous events apply by (minute, type priority, eventId).
Mirrored by EventContractValidation.cs.
"""

# Infrastructure first, then capacity, then demand: a surge at T already sees T's closures.
TYPES = ('flight_change', 'corridor_closure', 'checkpoint_unavailable', 'staff_reduction', 'resume', 'passenger_surge', 'queue_redistribution')
PRIORITY = {kind: index for index, kind in enumerate(TYPES)}
OPTIONAL = ('checkpointId', 'flightId', 'zoneId', 'passengerCount', 'windowMinutes', 'staffCount', 'delayMinutes', 'targetCheckpointId')
FIELDS = {
    'queue_redistribution': ({'checkpointId', 'targetCheckpointId', 'passengerCount', 'windowMinutes'}, set()),
    'passenger_surge': ({'checkpointId', 'passengerCount', 'windowMinutes'}, {'flightId'}),
    'checkpoint_unavailable': ({'checkpointId'}, set()),
    'staff_reduction': ({'checkpointId', 'staffCount'}, set()),
    'flight_change': ({'flightId', 'delayMinutes'}, set()),
    'corridor_closure': ({'zoneId'}, set()),
    'resume': (set(), {'checkpointId', 'zoneId'}),
}
MAX_PASSENGERS = 100000


def planned_at(checkpoint, steps, minute):
    """People scheduled on duty at `minute`; without a schedule, the fixed staffing."""
    if not steps:
        return checkpoint.staff_count
    return [step.staff_count for step in steps if step.start_minute <= minute][-1]


def ordered(events):
    return sorted(events, key=lambda event: (event['minute'], PRIORITY[event['type']], event['eventId']))


def validate_events(value, path, errors, error):
    """Structure, references and timeline of request['events']; codes match the .NET validator."""
    events = value.get('events')
    if events is None:
        return
    generation = value.get('passengerGeneration')
    if not events:
        return
    if generation is None:
        errors.append(error(path + '/passengerGeneration', 'required'))
        return
    if generation.get('flow') is not None or value.get('engine') != 'simpy-kpi-1.0':
        errors.append(error(path + '/events', 'invalid_value'))
        return
    horizon = value['durationMinutes']
    checkpoints = {item['checkpointId']: item['staffCount'] for item in value['checkpoints']}
    flights = {item['flightId']: item for item in generation['flights']}
    navigation = value.get('navigation')
    zones = {node['id'] for node in navigation['graph']['nodes']} if navigation else set()
    static_closed = set(navigation['closedZoneIds']) if navigation else set()
    surge_total = 0
    valid = []
    identifiers = set()
    for index, event in enumerate(events):
        field = f'{path}/events/{index}'
        required, optional = FIELDS[event['type']]
        before = len(errors)
        if event['eventId'] in identifiers:
            errors.append(error(f'{field}/eventId', 'duplicate_id'))
        identifiers.add(event['eventId'])
        for name in sorted(required):
            if name not in event:
                errors.append(error(f'{field}/{name}', 'required'))
        for name in OPTIONAL:
            if name in event and name not in required | optional:
                errors.append(error(f'{field}/{name}', 'invalid_value'))
        if event['type'] == 'resume' and ('checkpointId' in event) == ('zoneId' in event):
            errors.append(error(field, 'invalid_value'))
        if event['minute'] >= horizon:
            errors.append(error(f'{field}/minute', 'out_of_range'))
        if 'checkpointId' in event and event['checkpointId'] not in checkpoints:
            errors.append(error(f'{field}/checkpointId', 'invalid_value'))
        if 'flightId' in event and event['flightId'] not in flights:
            errors.append(error(f'{field}/flightId', 'invalid_value'))
        if 'zoneId' in event and event['zoneId'] not in zones:
            errors.append(error(f'{field}/zoneId', 'invalid_value'))
        if 'windowMinutes' in event and event['minute'] + event['windowMinutes'] > horizon:
            errors.append(error(f'{field}/windowMinutes', 'out_of_range'))
        if event['type'] == 'flight_change' and event.get('delayMinutes') == 0:
            errors.append(error(f'{field}/delayMinutes', 'out_of_range'))
        if 'targetCheckpointId' in event and (event['targetCheckpointId'] not in checkpoints or event['targetCheckpointId'] == event.get('checkpointId')):
            errors.append(error(f'{field}/targetCheckpointId', 'invalid_value'))
        if event['type'] == 'passenger_surge': surge_total += event.get('passengerCount', 0)
        if len(errors) == before:
            valid.append((index, event))
    if value['passengerCount'] + surge_total > MAX_PASSENGERS:
        errors.append(error(path + '/events', 'out_of_range'))
    if len(valid) != len(events):
        return
    scheduled = {item['checkpointId'] for item in value.get('staffSchedules') or []}
    for index, field in conflicts(events, checkpoints, flights, static_closed, scheduled, value.get('journey') is not None):
        errors.append(error(f'{path}/events/{index}{field}', 'event_conflict'))


def conflicts(events, checkpoints, flights, static_closed, scheduled=frozenset(), rolling_gates=False):
    """Replay the timeline and yield (index, field) of each event contradicting the state it meets.
    A checkpoint with a staff schedule takes no staff_reduction, and resume only ends an outage."""
    staff = dict(checkpoints)
    unavailable = set()
    closed = set(static_closed)
    dynamic = set()
    close = {key: flight['boardingCloseMinute'] for key, flight in flights.items()}
    position = {id(event): index for index, event in enumerate(events)}
    for event in ordered(events):
        index, kind, minute = position[id(event)], event['type'], event['minute']
        if kind == 'checkpoint_unavailable':
            if event['checkpointId'] in unavailable: yield index, '/checkpointId'
            unavailable.add(event['checkpointId'])
        elif kind == 'staff_reduction' and event['checkpointId'] in scheduled:
            yield index, '/checkpointId'
        elif kind == 'staff_reduction':
            if event['staffCount'] >= staff[event['checkpointId']]: yield index, '/staffCount'
            staff[event['checkpointId']] = event['staffCount']
        elif kind == 'corridor_closure':
            if event['zoneId'] in closed: yield index, '/zoneId'
            closed.add(event['zoneId']); dynamic.add(event['zoneId'])
        elif kind == 'resume' and 'zoneId' in event:
            if event['zoneId'] not in dynamic: yield index, '/zoneId'
            closed.discard(event['zoneId']); dynamic.discard(event['zoneId'])
        elif kind == 'resume':
            key = event['checkpointId']
            if key not in unavailable and (staff[key] == checkpoints[key] or key in scheduled): yield index, '/checkpointId'
            unavailable.discard(key); staff[key] = checkpoints[key]
        elif kind == 'flight_change' and not rolling_gates:
            key = event['flightId']
            changed = close[key] + event['delayMinutes']
            # Boarding already closed, or the new deadline would rewrite the past.
            if close[key] <= minute or changed < minute or changed > 10080: yield index, '/delayMinutes'
            close[key] = changed


def effective_close(events, flights):
    """Final boarding close per flight after every flight_change."""
    close = {key: flight['boardingCloseMinute'] for key, flight in flights.items()}
    for event in ordered(events):
        if event['type'] == 'flight_change':
            close[event['flightId']] += event['delayMinutes']
    return close

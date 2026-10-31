"""Cross-field journey constraints, mirrored at the .NET boundary."""
def validate_request(value, path, errors, error):
    if errors:
        return
    j = value.get('journey')
    if j is None:
        return
    base = path + '/journey'
    def invalid(field='', code='invalid_value'):
        errors.append(error(base + field, code))
    generation = value.get('passengerGeneration')
    if generation is None or generation.get('flow') is not None:
        invalid()
        return
    # Snapshot verification re-derives per-passenger states. Bound that work as well
    # as the existing population and snapshot limits for long-day captures.
    grid = (value['durationMinutes'] + value.get('snapshotIntervalMinutes',1) - 1)//value.get('snapshotIntervalMinutes',1) + 1
    population = sum(b['modeledCount'] for b in generation['batches']) + sum(e.get('passengerCount',0) for e in value.get('events',[]))
    if grid * population > 10_000_000: invalid('', 'out_of_range')
    flights = {f['flightId']: f for f in j['flights']}
    profiles = {f['flightId']: f for f in generation['flights']}
    if len(flights) != len(j['flights']): invalid('/flights', 'duplicate_id')
    predecessors = [f['previousFlightId'] for f in j['flights'] if f['previousFlightId']]
    if len(predecessors) != len(set(predecessors)): invalid('/flights')
    if flights.keys() != profiles.keys(): invalid('/flights')
    if len({c['id'] for c in j['connections']}) != len(j['connections']): invalid('/connections', 'duplicate_id')
    routes = {(r['checkpointId'], r['gateId']) for r in j['routes']}
    if len(routes) != len(j['routes']): invalid('/routes', 'duplicate_id')
    checkpoints = {c['checkpointId'] for c in value['checkpoints']}
    for r in j['routes']:
        if r['checkpointId'] not in checkpoints: invalid('/routes')
    for f in j['flights']:
        key = f['flightId']
        if not (f['arrivalMinute'] <= f['boardingOpenMinute'] < f['boardingCloseMinute'] < f['departureMinute'] <= value['durationMinutes']):
            invalid('/flights')
        if any(abs(f[k]*60-round(f[k]*60)) > 1e-6 for k in ('arrivalMinute','boardingOpenMinute','boardingCloseMinute','departureMinute')):
            invalid('/flights')
        if f['gateId'] not in f['eligibleGateIds'] or len(set(f['eligibleGateIds'])) != len(f['eligibleGateIds']): invalid('/flights')
        if key in profiles:
            p = profiles[key]
            if p['isCancelled'] != f['isCancelled'] or p['passengerCapacity'] != f['passengerCapacity'] or abs(p['boardingCloseMinute'] - max(0,f['boardingCloseMinute'] + f['delayMinutes'])) > 1e-6:
                invalid('/flights')
            origins = {p['checkpointId'], *(r['checkpointId'] for r in p.get('checkpointRoutes', []))}
            if any((cp, g) not in routes for cp in origins for g in f['eligibleGateIds']): invalid('/routes')
        previous = f['previousFlightId']
        if previous and (previous not in flights or flights[previous]['isCancelled'] or f['isCancelled']): invalid('/flights')
        seen = {key}
        while previous in flights:
            if previous in seen:
                invalid('/flights'); break
            seen.add(previous)
            previous = flights[previous]['previousFlightId']
    totals = {}
    for c in j['connections']:
        a, b = c['inboundFlightId'], c['outboundFlightId']
        if a not in flights or b not in flights or a == b or flights[a]['isCancelled'] or flights[b]['isCancelled']:
            invalid('/connections'); continue
        if flights[a]['arrivalMinute'] < 0 or flights[a]['arrivalMinute'] >= flights[b]['boardingCloseMinute']:
            invalid('/connections')
        totals[b] = totals.get(b,0) + c['passengerCount']
    counts = {b['flightId']: b['modeledCount'] for b in generation['batches']}
    if any(total > counts.get(key,0) for key,total in totals.items()): invalid('/connections')
    for key, flight in flights.items():
        if sum(c['passengerCount'] for c in j['connections'] if c['inboundFlightId']==key) > flight['passengerCapacity']:
            invalid('/connections')
    if any(e['type']=='passenger_surge' and 'flightId' not in e for e in value.get('events',[])): invalid()
    changes = [e for e in value.get('events',[]) if e['type']=='flight_change']
    if len(changes)>100: invalid('/flights', 'out_of_range')
    if any(e['delayMinutes'] <= 0 or flights.get(e['flightId'],{}).get('isCancelled',True) for e in changes): invalid('/flights')
    choices = [c['eventId'] for c in j.get('gateChoices', [])]
    if len(choices) != len(set(choices)) or not set(choices) <= {e['eventId'] for e in changes}: invalid('/gateChoices')


def validate_result(value, path, errors, error):
    def invalid(field=''):
        errors.append(error(path+field,'invalid_result'))
    people = value['passengers']
    if len(people)!=value['populationCount'] or len({p['id'] for p in people})!=len(people): invalid('/passengers')
    if sum(p['connectionId'] is not None for p in people)!=value['connectionPassengerCount']: invalid()
    if sum(p['boardedMinute'] is not None for p in people)!=value['boardedCount']: invalid()
    if sum(p['connectionStatus']=='missed' for p in people)!=value['missedConnectionCount']: invalid()
    horizon = value['horizonMinutes']
    for p in people:
        if (p['stage']=='boarded') != (p['boardedMinute'] is not None): invalid('/passengers')
        for name in ('arrivalMinute','gateArrivalMinute','boardedMinute'):
            if p[name] is not None and p[name]>horizon: invalid('/passengers')
        for visit in p['visits']:
            a,s,f = visit['arrivalMinute'],visit['startedMinute'],visit['finishedMinute']
            if a>horizon or s is not None and not a<=s<=horizon or f is not None and (s is None or not s<=f<=horizon): invalid('/passengers')
    for plan in value['plans']:
        flights = plan['flights']
        if len({f['flightId'] for f in flights})!=len(flights): invalid('/plans')
        for f in flights:
            if not f['arrivalMinute']<=f['boardingOpenMinute']<f['boardingCloseMinute']<f['departureMinute']: invalid('/plans')
        gates = {f['gateId'] for f in flights}
        for gate in gates:
            row = sorted((f for f in flights if f['gateId']==gate and not f['isCancelled']),key=lambda f:f['arrivalMinute'])
            if any(a['departureMinute']>b['arrivalMinute'] for a,b in zip(row,row[1:])): invalid('/plans')

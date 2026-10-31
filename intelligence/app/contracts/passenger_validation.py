"""Semantic rules mirrored by PassengerContractValidation.cs after shared JSON Schema checks."""
import math


def validate_passengers(value, path, errors, error):
    def add(field, code='invalid_value'):
        errors.append(error(path + field, code))
    def unique(items, key, field):
        seen = set()
        for index, item in enumerate(items):
            if item[key] in seen:
                add(f'{field}/{index}/{key}', 'duplicate_id')
            seen.add(item[key])
    unique(value['checkpoints'], 'checkpointId', '/checkpoints')
    unique(value['flights'], 'flightId', '/flights')
    unique(value['batches'], 'flightId', '/batches')
    checkpoints, flights = {}, {}
    for item in value['checkpoints']:
        checkpoints.setdefault(item['checkpointId'], item)
    for item in value['flights']:
        flights.setdefault(item['flightId'], item)
    if set(flights) != {item['flightId'] for item in value['batches']}:
        add('/batches')
    if sum(int(batch['modeledCount']) for batch in value['batches']) > 100000:
        add('/batches', 'out_of_range')
    for index, flight in enumerate(value['flights']):
        if flight['checkpointId'] not in checkpoints:
            add(f'/flights/{index}/checkpointId')
        if flight['boardingCloseMinute'] > value['durationMinutes']:
            add(f'/flights/{index}/boardingCloseMinute', 'out_of_range')
        # Spread alternatives name each checkpoint once, all known, and include the flight's own route.
        alternatives = flight.get('checkpointRoutes')
        if alternatives is not None:
            unique(alternatives, 'checkpointId', f'/flights/{index}/checkpointRoutes')
            for route_index, route in enumerate(alternatives):
                if route['checkpointId'] not in checkpoints:
                    add(f'/flights/{index}/checkpointRoutes/{route_index}/checkpointId')
            if not any(route['checkpointId'] == flight['checkpointId'] and route['routeId'] == flight['routeId'] for route in alternatives):
                add(f'/flights/{index}/checkpointRoutes')
    # Passenger types scale every stage; operational bounds use the largest configured multiplier.
    flow = value.get('flow')
    segments = flow['segments'] if flow else []
    service_scale = max((segment['serviceMultiplier'] for segment in segments), default=1)
    travel_scale = (min((segment['travelMultiplier'] for segment in segments), default=1),
                    max((segment['travelMultiplier'] for segment in segments), default=1))
    for index, batch in enumerate(value['batches']):
        field = f'/batches/{index}'
        flight = flights.get(batch['flightId'])
        checkpoint = checkpoints.get(batch['checkpointId'])
        if checkpoint is None:
            add(field + '/checkpointId')
        if flight is None:
            add(field + '/flightId')
        else:
            count = batch['modeledCount']
            capacity = flight['passengerCapacity']
            if batch['checkpointId'] != flight['checkpointId']:
                add(field + '/checkpointId')
            if (batch['countMode'] == 'full' and count != capacity
                    or batch['countMode'] == 'subset' and count >= capacity
                    or flight['isCancelled'] and count != 0):
                add(field + '/modeledCount')
            if count and batch['arrival']['maximum'] > flight['boardingCloseMinute']:
                add(field + '/arrival/maximum', 'out_of_range')
            if (batch.get('checkpointMode') == 'spread') != ('checkpointRoutes' in flight):
                add(field + '/checkpointMode')
        # Bounds hold on every checkpoint and route a spread batch may use.
        lanes = (flight.get('checkpointRoutes') or []) if flight else []
        rates = [checkpoints[lane['checkpointId']]['serviceRatePerMinute'] for lane in lanes if lane['checkpointId'] in checkpoints]
        if checkpoint:
            rates.append(checkpoint['serviceRatePerMinute'])
        travel = [lane['routeTravelMinutes'] for lane in lanes] + ([flight['routeTravelMinutes']] if flight else [])
        distribution(batch['arrival'], field + '/arrival', add)
        distribution(batch['service'], field + '/service', add, (0.01, 20))
        distribution(batch['travel'], field + '/travel', add, (0.01, 20))
        groups = batch['groups']
        unique(groups, 'kind', field + '/groups')
        if not math.isclose(sum(float(group['weight']) for group in groups), 1, rel_tol=0, abs_tol=1e-9):
            add(field + '/groups')
        if not any(group['minSize'] == 1 for group in groups):
            add(field + '/groups')
        for group_index, group in enumerate(groups):
            if group['minSize'] > group['maxSize'] or group['kind'] == 'solo' and (group['minSize'] != 1 or group['maxSize'] != 1):
                add(f'{field}/groups/{group_index}/maxSize')
        if rates and batch['service']['maximum'] * max(group['serviceMultiplier'] for group in groups) * service_scale / min(rates) > 240:
            add(field + '/service/maximum', 'out_of_range')
        if flight and batch['travel']['maximum'] * max(group['travelMultiplier'] for group in groups) * travel_scale[1] * max(travel) > 240:
            add(field + '/travel/maximum', 'out_of_range')
        if flight and (float(batch['travel']['minimum']) * float(min(travel))
                       * min(float(group['travelMultiplier']) for group in groups) * float(travel_scale[0])) <= 0:
            add(field + '/travel/minimum', 'out_of_range')
        if sum(float(surge['share']) for surge in batch['surges']) > 1 + 1e-9:
            add(field + '/surges')
        for surge_index, surge in enumerate(batch['surges']):
            if not batch['arrival']['minimum'] <= surge['startsMinute'] < surge['endsMinute'] <= batch['arrival']['maximum']:
                add(f'{field}/surges/{surge_index}')
    if flow:
        validate_flow(value, flow, add, unique, service_scale)


def validate_flow(value, flow, add, unique, service_scale):
    unique(flow['segments'], 'kind', '/flow/segments')
    unique(flow['stages'], 'kind', '/flow/stages')
    stages = {stage['kind'] for stage in flow['stages']}
    if not math.isclose(sum(float(segment['share']) for segment in flow['segments']), 1, rel_tol=0, abs_tol=1e-9):
        add('/flow/segments')
    for index, segment in enumerate(flow['segments']):
        field = f'/flow/segments/{index}'
        if segment['holdBaggage'] and 'bagDrop' not in stages:
            add(field + '/holdBaggage')
        if segment['control'] != 'security' and segment['control'] not in stages:
            add(field + '/control')
        if 'transferMinutes' in segment:
            distribution(segment['transferMinutes'], field + '/transferMinutes', add, (0, 240))
    group_scale = max(group['serviceMultiplier'] for batch in value['batches'] for group in batch['groups'])
    for index, stage in enumerate(flow['stages']):
        field = f'/flow/stages/{index}/service'
        distribution(stage['service'], field, add, (0.01, 240))
        if stage['service']['maximum'] * group_scale * service_scale > 240:
            add(field + '/maximum', 'out_of_range')
    for index, closure in enumerate(flow['closures']):
        if closure['stage'] != 'security' and closure['stage'] not in stages:
            add(f'/flow/closures/{index}/stage')
        if not closure['startsMinute'] < closure['endsMinute'] <= value['durationMinutes']:
            add(f'/flow/closures/{index}')


def distribution(value, field, add, bounds=None):
    lo, hi = float(value['minimum']), float(value['maximum'])
    kind = value['kind']
    if kind == 'constant' and lo != hi or kind != 'constant' and lo >= hi:
        add(field + '/maximum')
    if bounds and (lo < bounds[0] or hi > bounds[1]):
        add(field, 'out_of_range')
    if kind == 'normal':
        if not lo <= float(value['mean']) <= hi:
            add(field + '/mean')
        if not 0 < float(value['stdDev']) <= hi - lo:
            add(field + '/stdDev')
    if kind == 'exponential' and float(value['mean']) <= 0:
        add(field + '/mean')
    if kind == 'triangular' and not lo <= float(value['mode']) <= hi:
        add(field + '/mode')

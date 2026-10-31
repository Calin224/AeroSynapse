"""Shift plans: who works at which checkpoint in each 15-minute slot of a long day.

One contiguous shift per person, starting and ending on a worked slot; a break inside every run of
`breakAfterSlots` worked slots; moves between checkpoints cost `transferMinutes` of capacity at the
destination and are capped per rolling hour. Queues carry over between slots, so staffing a checkpoint
ahead of a wave pays off. Mirrored by ShiftPlanValidation in StaffingContracts.cs.
"""
import math
from decimal import Decimal

from ortools.sat.python import cp_model

from app.optimization.staffing import configured_solver

SLOT = 15
HOUR = 60 // SLOT
OBJECTIVE = ('min queueWeight*sum(queueMilli) + staffSlotCost*dutySlots + moveCost*moves; '
             'queue[t]=max(0, queue[t-1] + 1000*arrivals - floor(capacityMilliPerMinute*targetUtilizationPercent/100)'
             '*(15*staff - transferMinutes*arrivingMovers))')
STATUS = {'OPTIMAL': 'optimal', 'FEASIBLE': 'feasible', 'INFEASIBLE': 'infeasible', 'UNKNOWN': 'timeout', 'MODEL_INVALID': 'invalid_model'}
MAX_ASSIGNMENTS = 200_000


def planned_capacity(checkpoint, rules):
    """Capacity per operator-minute the plan relies on. A fluid queue only grows once arrivals exceed
    capacity, but random arrivals and service times make queues long well before an operator is fully
    busy (about 9 minutes at 90% for one operator with exponential service), so plans keep headroom."""
    return checkpoint['capacityMilliPerMinute'] * rules['targetUtilizationPercent'] // 100


def validate_request(value, path, errors, error):
    """Cross-references after JSON Schema validation; impossible rules remain solver infeasibility."""
    for name, key in [('staff', 'staffMemberId'), ('checkpoints', 'checkpointId')]:
        seen = set()
        for index, item in enumerate(value[name]):
            if item[key] in seen:
                errors.append(error(f'{path}/{name}/{index}/{key}', 'duplicate_id'))
            seen.add(item[key])
    rules = value['rules']
    if rules['maxShiftSlots'] < rules['minShiftSlots']:
        errors.append(error(path + '/rules/maxShiftSlots', 'out_of_range'))
    for index, checkpoint in enumerate(value['checkpoints']):
        field = f'{path}/checkpoints/{index}'
        if len(checkpoint['arrivalsPerSlot']) != value['slotCount']:
            errors.append(error(field + '/arrivalsPerSlot', 'comparison_mismatch'))
        if checkpoint['maximumStaff'] < checkpoint['minimumStaff']:
            errors.append(error(field + '/maximumStaff', 'out_of_range'))
        if checkpoint['requiredOpen'] and checkpoint['closed']:
            errors.append(error(field + '/requiredOpen', 'invalid_value'))
    terminals = {}
    for checkpoint in value['checkpoints']:
        terminals[checkpoint['terminalId']] = terminals.get(checkpoint['terminalId'], 0) + 1
    if sum(terminals.get(person['terminalId'], 0) for person in value['staff']) * value['slotCount'] > MAX_ASSIGNMENTS:
        errors.append(error(path + '/staff', 'out_of_range'))


def build_model(request):
    slots, rules = request['slotCount'], request['rules']
    checkpoints = sorted(request['checkpoints'], key=lambda item: item['checkpointId'])
    people = sorted(request['staff'], key=lambda item: item['staffMemberId'])
    model = cp_model.CpModel()
    x, on, duty, move, arriving, begins = {}, {}, {}, {}, {}, {}
    for p, person in enumerate(people):
        places = [c for c, item in enumerate(checkpoints) if item['terminalId'] == person['terminalId'] and not item['closed']]
        works = model.new_bool_var(f'works_{p}')
        start = [model.new_bool_var(f'start_{p}_{t}') for t in range(slots)]
        for t in range(slots):
            duty[p, t] = model.new_bool_var(f'duty_{p}_{t}')
            for c in places:
                x[p, c, t] = model.new_bool_var(f'x_{p}_{c}_{t}')
        for t in range(slots):
            on[p, t] = sum(x[p, c, t] for c in places)
            model.add(on[p, t] <= duty[p, t])
            model.add(start[t] <= duty[p, t])
            # A single start makes the duty slots one contiguous block.
            model.add(duty[p, t] <= start[t] + (duty[p, t - 1] if t else 0))
            # The shift starts and ends on a worked slot: breaks fall strictly inside it.
            model.add(on[p, t] >= start[t])
            model.add(on[p, t] >= duty[p, t] - (duty[p, t + 1] if t + 1 < slots else 0))
        model.add(sum(start) == works)
        # People of one terminal are interchangeable: ordering them by start slot (a day off last)
        # removes symmetric plans without excluding any staffing pattern.
        begins[p] = sum(t * start[t] for t in range(slots)) + slots * (1 - works)
        previous = next((q for q in range(p - 1, -1, -1) if people[q]['terminalId'] == person['terminalId']), None)
        if previous is not None:
            model.add(begins[previous] <= begins[p])
        length = sum(duty[p, t] for t in range(slots))
        model.add(length >= rules['minShiftSlots'] * works)
        model.add(length <= rules['maxShiftSlots'] * works)
        window = rules['breakAfterSlots'] + 1
        for t in range(slots - window + 1):
            model.add(sum(on[p, k] for k in range(t, t + window)) <= rules['breakAfterSlots'])
        for t in range(slots):
            for c in places:
                if t == 0:
                    arriving[p, c, t] = 0
                    continue
                # Working at c now after working elsewhere in the previous slot is a move into c.
                arriving[p, c, t] = model.new_bool_var(f'in_{p}_{c}_{t}')
                model.add(arriving[p, c, t] >= x[p, c, t] - x[p, c, t - 1] + on[p, t - 1] - 1)
                model.add(arriving[p, c, t] <= x[p, c, t])
                model.add(arriving[p, c, t] <= on[p, t - 1])
                model.add(arriving[p, c, t] <= 1 - x[p, c, t - 1])
            move[p, t] = sum(arriving[p, c, t] for c in places)
    staff, movers, queues, opened = {}, {}, {}, {}
    for c, item in enumerate(checkpoints):
        previous = 0
        total = 1000 * sum(item['arrivalsPerSlot'])
        for t in range(slots):
            staff[c, t] = sum(x[p, c, t] for p in range(len(people)) if (p, c, t) in x)
            movers[c, t] = sum(arriving[p, c, t] for p in range(len(people)) if (p, c, t) in arriving)
            opened[c, t] = model.new_bool_var(f'open_{c}_{t}')
            top = min(item['maximumStaff'], item['physicalLimit'])
            model.add(staff[c, t] <= top * opened[c, t])
            model.add(staff[c, t] >= max(1, item['minimumStaff']) * opened[c, t])
            if item['closed']: model.add(opened[c, t] == 0)
            # Over a long day "required open" means serving whoever arrives: open in every slot with
            # arrivals, rather than a person standing at an empty checkpoint all day.
            if item['requiredOpen'] and item['arrivalsPerSlot'][t] > 0: model.add(opened[c, t] == 1)
            capacity = planned_capacity(item, rules) * (SLOT * staff[c, t] - rules['transferMinutes'] * movers[c, t])
            queue = model.new_int_var(0, total, f'queue_{c}_{t}')
            model.add_max_equality(queue, [0, previous + 1000 * item['arrivalsPerSlot'][t] - capacity])
            queues[c, t] = previous = queue
    for t in range(slots):
        model.add(sum(move[p, k] for p in range(len(people)) for k in range(t, min(slots, t + HOUR))) <= rules['maxMovesPerHour'])
    model.minimize(rules['queueWeight'] * sum(queues.values()) + rules['staffSlotCost'] * sum(duty.values())
                   + rules['moveCost'] * sum(move.values()))
    return model, (people, checkpoints, x, duty)


def shifts_from(solver, people, checkpoints, x, duty, slots):
    result = []
    for p, person in enumerate(people):
        worked = [next((checkpoints[c]['checkpointId'] for c in range(len(checkpoints))
                        if (p, c, t) in x and solver.value(x[p, c, t])), None) for t in range(slots)]
        days = [t for t in range(slots) if solver.value(duty[p, t])]
        if not days:
            continue
        start, end = days[0], days[-1] + 1
        segments = []
        for t in range(start, end):
            if worked[t] is None:
                continue
            if segments and segments[-1]['checkpointId'] == worked[t] and segments[-1]['endSlot'] == t:
                segments[-1]['endSlot'] = t + 1
            else:
                segments.append({'checkpointId': worked[t], 'startSlot': t, 'endSlot': t + 1})
        result.append({'staffMemberId': person['staffMemberId'], 'startSlot': start, 'endSlot': end,
                       'breaks': [t for t in range(start, end) if worked[t] is None], 'segments': segments})
    return result


def evaluate(request, shifts):
    """Per-slot staffing, movers and queues implied by `shifts`, with the totals the objective uses."""
    slots, rules = request['slotCount'], request['rules']
    checkpoints = sorted(request['checkpoints'], key=lambda item: item['checkpointId'])
    staff = {item['checkpointId']: [0] * slots for item in checkpoints}
    movers = {item['checkpointId']: [0] * slots for item in checkpoints}
    moves = duty = 0
    for shift in shifts:
        duty += shift['endSlot'] - shift['startSlot']
        for index, segment in enumerate(shift['segments']):
            for t in range(segment['startSlot'], segment['endSlot']):
                staff[segment['checkpointId']][t] += 1
            previous = shift['segments'][index - 1] if index else None
            # Consecutive worked slots at different checkpoints are a move; a break in between is not.
            if previous and previous['endSlot'] == segment['startSlot'] and previous['checkpointId'] != segment['checkpointId']:
                movers[segment['checkpointId']][segment['startSlot']] += 1
                moves += 1
    plans, queued = [], 0
    for item in checkpoints:
        key, queue, row = item['checkpointId'], 0, []
        for t in range(slots):
            capacity = planned_capacity(item, rules) * (SLOT * staff[key][t] - rules['transferMinutes'] * movers[key][t])
            queue = max(0, queue + 1000 * item['arrivalsPerSlot'][t] - capacity)
            row.append(queue)
        queued += sum(row)
        plans.append({'checkpointId': key, 'staff': staff[key], 'arrivingMovers': movers[key], 'queueMilli': row})
    score = rules['queueWeight'] * queued + rules['staffSlotCost'] * duty + rules['moveCost'] * moves
    return plans, duty, moves, score


def solve(request):
    model, (people, checkpoints, x, duty) = build_model(request)
    solver = configured_solver(request['timeLimitSeconds'])
    status = solver.solve(model)
    name = solver.status_name(status)
    result = {'status': STATUS[name], 'solverStatus': name, 'timedOut': name in ('FEASIBLE', 'UNKNOWN'),
              'score': None, 'bestBound': None, 'dutySlots': None, 'moves': None, 'shifts': [], 'checkpoints': [],
              'request': request, 'objective': OBJECTIVE, 'wallTimeSeconds': solver.wall_time}
    # Value() is legal only when CP-SAT actually found an incumbent.
    if status not in (cp_model.OPTIMAL, cp_model.FEASIBLE):
        return result
    shifts = shifts_from(solver, people, checkpoints, x, duty, request['slotCount'])
    plans, duties, moves, score = evaluate(request, shifts)
    result |= {'shifts': shifts, 'checkpoints': plans, 'dutySlots': duties, 'moves': moves, 'score': score,
               'bestBound': min(score, math.ceil(solver.best_objective_bound))}
    return result


def plain(value):
    """HTTP validation parses numbers as Decimal; slot arithmetic needs Python ints."""
    if isinstance(value, dict): return {key: plain(child) for key, child in value.items()}
    if isinstance(value, list): return [plain(child) for child in value]
    if isinstance(value, Decimal): return int(value) if value == value.to_integral_value() else float(value)
    return value


def validate_result(value, path, errors, error):
    """Recompute every rule, slot count, queue and the score from the shifts alone."""
    value = plain(value)
    request = value['request']
    validate_request(request, path + '/request', errors, error)
    if errors: return
    def invalid(field=''): errors.append(error(path + field, 'invalid_result'))
    state = STATUS[value['solverStatus']]
    if value['status'] != state or value['timedOut'] != (state in ('feasible', 'timeout')):
        invalid(); return
    if state not in ('optimal', 'feasible'):
        if value['shifts'] or value['checkpoints'] or any(value[key] is not None for key in ('score', 'bestBound', 'dutySlots', 'moves')):
            invalid()
        return
    if value['score'] is None or value['bestBound'] is None or value['bestBound'] > value['score'] \
            or state == 'optimal' and value['score'] != value['bestBound']:
        invalid(); return
    slots, rules = request['slotCount'], request['rules']
    people = {person['staffMemberId']: person for person in request['staff']}
    checkpoints = {item['checkpointId']: item for item in request['checkpoints']}
    seen = set()
    for index, shift in enumerate(value['shifts']):
        field = f'/shifts/{index}'
        person = people.get(shift['staffMemberId'])
        segments, start, end = shift['segments'], shift['startSlot'], shift['endSlot']
        worked = set()
        for segment in segments:
            worked.update(range(segment['startSlot'], segment['endSlot']))
        if (person is None or shift['staffMemberId'] in seen or not start < end <= slots
                or not rules['minShiftSlots'] <= end - start <= rules['maxShiftSlots']
                or any(segment['startSlot'] >= segment['endSlot'] for segment in segments)
                or any(b['startSlot'] < a['endSlot'] for a, b in zip(segments, segments[1:]))
                # Adjacent segments at one checkpoint would be one segment.
                or any(a['endSlot'] == b['startSlot'] and a['checkpointId'] == b['checkpointId'] for a, b in zip(segments, segments[1:]))
                or segments[0]['startSlot'] != start or segments[-1]['endSlot'] != end
                or shift['breaks'] != [t for t in range(start, end) if t not in worked]
                or any(checkpoints.get(segment['checkpointId']) is None
                       or checkpoints[segment['checkpointId']]['terminalId'] != person['terminalId']
                       or checkpoints[segment['checkpointId']]['closed'] for segment in segments)
                or any(all(t in worked for t in range(k, k + rules['breakAfterSlots'] + 1))
                       for k in range(slots - rules['breakAfterSlots']))):
            invalid(field); return
        seen.add(shift['staffMemberId'])
    plans, duties, moves, score = evaluate(request, value['shifts'])
    if value['checkpoints'] != plans or value['dutySlots'] != duties or value['moves'] != moves or value['score'] != score:
        invalid(); return
    for plan in plans:
        item = checkpoints[plan['checkpointId']]
        for t in range(slots):
            count = plan['staff'][t]
            if (count > min(item['maximumStaff'], item['physicalLimit']) or 0 < count < item['minimumStaff']
                    or item['requiredOpen'] and item['arrivalsPerSlot'][t] > 0 and count < max(1, item['minimumStaff'])):
                invalid('/checkpoints'); return
    for t in range(slots):
        if sum(plan['arrivingMovers'][k] for plan in plans for k in range(t, min(slots, t + HOUR))) > rules['maxMovesPerHour']:
            invalid('/checkpoints'); return

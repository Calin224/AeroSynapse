"""Bounded weighted CP-SAT searches. Proxies generate candidates; SimPy validates them."""
import math
import time

from app.optimization.staffing import build_model, configured_solver, assignments

SCALE = 1_000_000
FORMULA = 'sum(round(1000000 * (wT * pressureMinutes/Tref + wC * operatingPoints/Cref + wR * shortageFraction/Rref) / (wT+wC+wR)))'


def values(request, checkpoint, count, weights):
    options = request['options']; n = options['normalization']
    total = max(1, sum(cp['demandMilli'] for cp in request['constraints']['checkpoints']))
    demand = checkpoint['demandMilli']; capacity = count * checkpoint['capacityMilli']
    pressure = 0 if demand == 0 else request['durationMinutes'] * float(demand) / (demand + float(capacity)) * demand / total
    risk = max(0, demand - capacity) / total
    cost = count * request['durationMinutes'] * options['staffMinuteCost'] + abs(count - checkpoint['currentStaff']) * checkpoint['changeCost']
    score = (weights['time'] * (pressure / n['timeMinutes']) + weights['cost'] * (cost / n['costPoints'])
             + weights['risk'] * (risk / n['riskFraction'])) / sum(weights.values())
    return pressure, risk, cost, math.floor(SCALE * score + .5)


def search(request):
    started = time.monotonic(); deadline = started + request['constraints']['timeLimitSeconds']
    options = request['options']; selected = options['weights']
    profiles = [('selected', selected), ('cost', {'time': 0, 'cost': 1, 'risk': 0}),
                ('time', {'time': 1, 'cost': 0, 'risk': 0}), ('risk', {'time': 0, 'cost': 0, 'risk': 1})]
    candidates, solves, seen = [], [], set()
    state = 'exhausted'
    for index in range(8):
        if index >= 4 and len(candidates) >= request['candidateCount']: break
        name, weights = profiles[index] if index < 4 else (f'exploration-{index - 3}', selected)
        model, variables, checkpoints = build_model(request['constraints'])
        ordered = [variables[cp['checkpointId']] for cp in checkpoints]
        model.add_forbidden_assignments(ordered, [[cp['currentStaff'] for cp in checkpoints]])
        if index >= 4 and seen: model.add_forbidden_assignments(ordered, sorted(seen))
        costs, scores = [], []
        for cp in checkpoints:
            rows = []
            for count in range(min(cp['physicalLimit'], cp['maximumStaff'], request['constraints']['availableStaff']) + 1):
                cost = count * request['durationMinutes'] * options['staffMinuteCost'] + abs(count - cp['currentStaff']) * cp['changeCost']
                if cost > options['operationalBudget']: continue
                _, _, cost, score = values(request, cp, count, weights)
                rows.append((count, cost, score))
            c = model.new_int_var(0, options['operationalBudget'], 'cost_' + cp['checkpointId'])
            s = model.new_int_var(0, max((row[2] for row in rows), default=0), 'score_' + cp['checkpointId'])
            model.add_allowed_assignments([variables[cp['checkpointId']], c, s], rows)
            costs.append(c); scores.append(s)
        model.add(sum(costs) <= options['operationalBudget'])
        model.minimize(sum(scores))
        remaining = max(0, deadline - time.monotonic())
        seconds = remaining / max(1, 4 - index)
        row = {'profile': name, 'weights': weights, 'solverStatus': 'UNKNOWN', 'attempted': seconds > 0,
               'scaledObjective': None, 'bestBound': None, 'proxyTimeMinutes': None, 'proxyRisk': None,
               'operationalCost': None, 'assignments': [], 'wallTimeSeconds': 0}
        if seconds > 0:
            solver = configured_solver(seconds); status = solver.solve(model)
            row['solverStatus'] = solver.status_name(status); row['wallTimeSeconds'] = solver.wall_time
            if row['solverStatus'] in ('OPTIMAL', 'FEASIBLE'):
                allocation = assignments(solver, variables, checkpoints)
                measurements = [values(request, cp, item['staffCount'], weights) for cp, item in zip(checkpoints, allocation)]
                pressure, risk, cost, score = [sum(items) for items in zip(*measurements)]
                row.update(assignments=allocation, proxyTimeMinutes=pressure, proxyRisk=risk, operationalCost=cost,
                           scaledObjective=score, bestBound=math.ceil(solver.best_objective_bound))
                key = tuple(item['staffCount'] for item in allocation)
                if key not in seen and len(candidates) < request['candidateCount']:
                    seen.add(key); candidates.append({'assignments': allocation,
                        'score': sum(item['unmetDemandMilli'] + item['changePenalty'] for item in allocation)})
        solves.append(row)
        if row['solverStatus'] == 'MODEL_INVALID': state = 'invalid_model'
        elif row['solverStatus'] == 'UNKNOWN' and state != 'invalid_model': state = 'timeout'
        if index >= 4 and row['solverStatus'] == 'INFEASIBLE': break
        if index >= 4 and time.monotonic() >= deadline: break
    if len(candidates) == request['candidateCount']: state = 'complete'
    return {'request': request, 'status': state, 'formula': FORMULA, 'candidates': candidates,
            'solves': solves, 'wallTimeSeconds': time.monotonic() - started}


def validate_request(value, path, errors, error):
    from app.optimization.staffing import validate_request as staffing_request
    staffing_request(value['constraints'], path + '/constraints', errors, error)
    options = value['options']
    if sum(options['weights'].values()) == 0 or options['operationalBudget'] / options['normalization']['costPoints'] > 1_000_000:
        errors.append(error(path + '/options', 'out_of_range'))
    if sum(min(cp['physicalLimit'], cp['maximumStaff'], value['constraints']['availableStaff']) + 1
           for cp in value['constraints']['checkpoints']) > 50000:
        errors.append(error(path + '/constraints/checkpoints', 'out_of_range'))


def validate_result(value, path, errors, error):
    from app.optimization.staffing import validate_candidate_result, validate_result as staffing_result
    request = value['request']
    validate_request(request, path + '/request', errors, error)
    if errors: return
    legacy = {'constraints': request['constraints'], 'candidateCount': request['candidateCount']}
    validate_candidate_result({'request': legacy, 'status': value['status'], 'candidates': value['candidates'],
                               'wallTimeSeconds': value['wallTimeSeconds']}, path, errors, error)
    def invalid(): errors.append(error(path, 'invalid_result'))
    if value['formula'] != FORMULA: invalid(); return
    expected = [request['options']['weights'], {'time': 0, 'cost': 1, 'risk': 0},
                {'time': 1, 'cost': 0, 'risk': 0}, {'time': 0, 'cost': 0, 'risk': 1}]
    if [row['weights'] for row in value['solves'][:4]] != expected: invalid(); return
    if [row['profile'] for row in value['solves'][:4]] != ['selected', 'cost', 'time', 'risk']: invalid(); return
    found = set()
    def key(items): return tuple(sorted((item['checkpointId'], item['staffCount']) for item in items))
    for row in value['solves']:
        if sum(row['weights'].values()) == 0: invalid(); return
        if not row['attempted'] and row['solverStatus'] != 'UNKNOWN': invalid(); return
        if row['solverStatus'] not in ('OPTIMAL', 'FEASIBLE'):
            if row['assignments'] or any(row[name] is not None for name in ('scaledObjective', 'bestBound', 'proxyTimeMinutes', 'proxyRisk', 'operationalCost')):
                invalid(); return
            continue
        allocation = {item['checkpointId']: item['staffCount'] for item in row['assignments']}
        if not row['attempted'] or set(allocation) != {cp['checkpointId'] for cp in request['constraints']['checkpoints']}:
            invalid(); return
        old_score = sum(item['unmetDemandMilli'] + item['changePenalty'] for item in row['assignments'])
        used = sum(item['staffCount'] for item in row['assignments'])
        staffing_result({'constraints': request['constraints'], 'status': 'feasible', 'solverStatus': 'FEASIBLE', 'timedOut': True,
            'score': old_score, 'bestBound': 0, 'usedStaff': used, 'unassignedStaff': request['constraints']['availableStaff'] - used,
            'assignments': row['assignments']}, path, errors, error)
        if errors: return
        if all(allocation[cp['checkpointId']] == cp['currentStaff'] for cp in request['constraints']['checkpoints']): invalid(); return
        measurements = [values(request, cp, allocation[cp['checkpointId']], row['weights']) for cp in request['constraints']['checkpoints']]
        t, r, c, score = [sum(items) for items in zip(*measurements)]
        if (row['scaledObjective'] != score or row['operationalCost'] != c or c > request['options']['operationalBudget']
            or row['bestBound'] is None or not 0 <= row['bestBound'] <= score
            or row['solverStatus'] == 'OPTIMAL' and row['bestBound'] != score
            or row['proxyTimeMinutes'] is None or abs(row['proxyTimeMinutes'] - t) > 1e-7
            or row['proxyRisk'] is None or abs(row['proxyRisk'] - r) > 1e-9): invalid(); return
        found.add(key(row['assignments']))
    if any(key(item['assignments']) not in found for item in value['candidates']): invalid()

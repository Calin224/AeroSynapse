"""Integer staffing proposals. Capacity is conservatively rounded to milli-passengers/minute."""
import math
import time

from ortools.sat.python import cp_model

OBJECTIVE = 'min sum(unmetDemandMilli + changeCost * abs(staffCount - currentStaff))'
SCALE = 1000


def validate_request(value, path, errors, error):
    """Cross-references after JSON Schema validation; impossible budgets remain solver infeasibility."""
    for name, key in [('checkpoints', 'checkpointId'), ('staffPools', 'terminalId')]:
        seen = set()
        for index, item in enumerate(value[name]):
            if item[key] in seen:
                errors.append(error(f'{path}/{name}/{index}/{key}', 'duplicate_id'))
            seen.add(item[key])
    terminals = {item['terminalId'] for item in value['staffPools']}
    for index, checkpoint in enumerate(value['checkpoints']):
        if checkpoint['terminalId'] not in terminals:
            errors.append(error(f'{path}/checkpoints/{index}/terminalId', 'invalid_value'))
    if value['availableStaff'] > sum(pool['availableStaff'] for pool in value['staffPools']):
        errors.append(error(path + '/availableStaff', 'out_of_range'))


def validate_result(value, path, errors, error):
    request = value['constraints']
    validate_request(request, path + '/constraints', errors, error)
    if errors: return
    def invalid(): errors.append(error(path, 'invalid_result'))
    state = {'OPTIMAL': 'optimal', 'FEASIBLE': 'feasible', 'INFEASIBLE': 'infeasible',
             'UNKNOWN': 'timeout', 'MODEL_INVALID': 'invalid_model'}[value['solverStatus']]
    if value['status'] != state or value['timedOut'] != (state in ('feasible', 'timeout')):
        invalid(); return
    if state not in ('optimal', 'feasible'):
        if value['assignments'] or any(value[key] is not None for key in ('score', 'bestBound', 'usedStaff', 'unassignedStaff')):
            invalid()
        return
    score, bound = value['score'], value['bestBound']
    if score is None or bound is None or bound > score or state == 'optimal' and score != bound:
        invalid(); return
    constraints = {item['checkpointId']: item for item in request['checkpoints']}
    assigned = {item['checkpointId'] for item in value['assignments']}
    if len(value['assignments']) != len(constraints) or assigned != constraints.keys():
        invalid(); return
    used, actual_score = 0, 0
    pools = {pool['terminalId']: 0 for pool in request['staffPools']}
    for assignment in value['assignments']:
        item = constraints[assignment['checkpointId']]; count = assignment['staffCount']
        capacity = count * item['capacityMilli']
        shortage = max(0, item['demandMilli'] - capacity)
        change = abs(count - item['currentStaff']) * item['changeCost']
        if (count > min(item['maximumStaff'], item['physicalLimit']) or item['closed'] and count != 0
            or item['requiredOpen'] and count < max(1, item['minimumStaff']) or 0 < count < item['minimumStaff']
            or assignment['estimatedCapacityMilli'] != capacity or assignment['unmetDemandMilli'] != shortage
            or assignment['changePenalty'] != change):
            invalid(); return
        used += count; actual_score += shortage + change; pools[item['terminalId']] += count
    if (actual_score != score or used > request['availableStaff'] or used != value['usedStaff']
        or value['unassignedStaff'] != request['availableStaff'] - used
        or any(pools[pool['terminalId']] > pool['availableStaff'] for pool in request['staffPools'])): invalid()


def build_model(request):
    model = cp_model.CpModel()
    variables, shortages, changes = {}, {}, {}
    checkpoints = sorted(request['checkpoints'], key=lambda item: item['checkpointId'])
    for checkpoint in checkpoints:
        key = checkpoint['checkpointId']
        staff = variables[key] = model.new_int_var(0, checkpoint['physicalLimit'], 'staff_' + key)
        opened = model.new_bool_var('open_' + key)
        model.add(staff <= checkpoint['maximumStaff'] * opened)
        model.add(staff >= max(1, checkpoint['minimumStaff']) * opened)
        if checkpoint['closed']: model.add(opened == 0)
        if checkpoint['requiredOpen']: model.add(opened == 1)
        shortage = shortages[key] = model.new_int_var(0, checkpoint['demandMilli'], 'shortage_' + key)
        model.add_max_equality(shortage, [0, checkpoint['demandMilli'] - checkpoint['capacityMilli'] * staff])
        change = changes[key] = model.new_int_var(0, 10000, 'change_' + key)
        model.add_abs_equality(change, staff - checkpoint['currentStaff'])
    model.add(sum(variables.values()) <= request['availableStaff'])
    for pool in request['staffPools']:
        model.add(sum(variables[item['checkpointId']] for item in checkpoints if item['terminalId'] == pool['terminalId'])
                  <= pool['availableStaff'])
    model.minimize(sum(shortages[item['checkpointId']] + item['changeCost'] * changes[item['checkpointId']]
                       for item in checkpoints))
    return model, variables, checkpoints


def configured_solver(seconds):
    solver = cp_model.CpSolver()
    solver.parameters.max_time_in_seconds = max(0, float(seconds))
    solver.parameters.num_search_workers = 1
    solver.parameters.random_seed = 0
    return solver


def assignments(solver, variables, checkpoints):
    result = []
    for checkpoint in checkpoints:
        count = solver.value(variables[checkpoint['checkpointId']])
        result.append({'checkpointId': checkpoint['checkpointId'], 'staffCount': count,
            'estimatedCapacityMilli': count * checkpoint['capacityMilli'],
            'unmetDemandMilli': max(0, checkpoint['demandMilli'] - count * checkpoint['capacityMilli']),
            'changePenalty': abs(count - checkpoint['currentStaff']) * checkpoint['changeCost']})
    return result


def solve(request):
    model, variables, checkpoints = build_model(request)
    solver = configured_solver(request['timeLimitSeconds'])
    status = solver.solve(model)
    name = solver.status_name(status)
    result = {'status': {'OPTIMAL': 'optimal', 'FEASIBLE': 'feasible', 'INFEASIBLE': 'infeasible',
                         'UNKNOWN': 'timeout', 'MODEL_INVALID': 'invalid_model'}[name],
              'solverStatus': name, 'timedOut': name in ('FEASIBLE', 'UNKNOWN'),
              'score': None, 'bestBound': None, 'usedStaff': None, 'unassignedStaff': None,
              'assignments': [], 'constraints': request, 'objective': OBJECTIVE,
              'capacityScale': SCALE, 'wallTimeSeconds': solver.wall_time}
    # Value() is legal only when CP-SAT actually found an incumbent.
    if status not in (cp_model.OPTIMAL, cp_model.FEASIBLE):
        return result
    result['assignments'] = assignments(solver, variables, checkpoints)
    result['usedStaff'] = sum(item['staffCount'] for item in result['assignments'])
    result['unassignedStaff'] = request['availableStaff'] - result['usedStaff']
    result['score'] = sum(item['unmetDemandMilli'] + item['changePenalty'] for item in result['assignments'])
    result['bestBound'] = math.ceil(solver.best_objective_bound)
    return result


def generate_candidates(request):
    """Distinct non-baseline allocations, under one wall-clock search budget.

    Each successive solve excludes earlier allocations. No global-optimality claim is
    made for this list: only the simulator can assess its operational consequences.
    """
    started = time.monotonic()
    constraints = request['constraints']
    deadline = started + float(constraints['timeLimitSeconds'])
    model, variables, checkpoints = build_model(constraints)
    ordered = [variables[item['checkpointId']] for item in checkpoints]
    model.add_forbidden_assignments(ordered, [[item['currentStaff'] for item in checkpoints]])
    candidates, state = [], 'complete'
    while len(candidates) < request['candidateCount']:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            state = 'timeout'; break
        solver = configured_solver(remaining)
        status = solver.solve(model)
        if status == cp_model.INFEASIBLE:
            state = 'exhausted'; break
        if status == cp_model.MODEL_INVALID:
            state = 'invalid_model'; break
        if status not in (cp_model.OPTIMAL, cp_model.FEASIBLE):
            state = 'timeout'; break
        allocation = assignments(solver, variables, checkpoints)
        candidates.append({'assignments': allocation,
            'score': sum(item['unmetDemandMilli'] + item['changePenalty'] for item in allocation)})
        model.add_forbidden_assignments(ordered, [[item['staffCount'] for item in allocation]])
    return {'request': request, 'status': state, 'candidates': candidates, 'wallTimeSeconds': time.monotonic() - started}


def validate_candidate_request(value, path, errors, error):
    validate_request(value['constraints'], path + '/constraints', errors, error)


def validate_candidate_result(value, path, errors, error):
    request = value['request']
    validate_candidate_request(request, path + '/request', errors, error)
    if errors: return
    candidates = value['candidates']
    if (len(candidates) > request['candidateCount']
        or (value['status'] == 'complete') != (len(candidates) == request['candidateCount'])):
        errors.append(error(path, 'invalid_result')); return
    seen = {tuple(sorted((item['checkpointId'], item['currentStaff']) for item in request['constraints']['checkpoints']))}
    for index, candidate in enumerate(candidates):
        counts = tuple(sorted((item['checkpointId'], item['staffCount']) for item in candidate['assignments']))
        if counts in seen:
            errors.append(error(path + f'/candidates/{index}', 'invalid_result')); continue
        seen.add(counts)
        used = sum(item['staffCount'] for item in candidate['assignments'])
        validate_result({'constraints': request['constraints'], 'status': 'feasible', 'solverStatus': 'FEASIBLE',
            'timedOut': True, 'score': candidate['score'], 'bestBound': 0, 'usedStaff': used,
            'unassignedStaff': request['constraints']['availableStaff'] - used, 'assignments': candidate['assignments']},
            path + f'/candidates/{index}', errors, error)

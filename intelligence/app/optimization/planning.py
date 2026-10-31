"""Anticipatory staffing plan for the live agent: a fluid-queue CP-SAT model over short blocks.

Each resource (the check-in desks, or one security control) has a queue now, the arrivals expected per block
and a service rate per server. The model picks the servers on duty per block so that the queue stays short at the
lowest staffing, with an optional pool shared by several resources (the operators of the controls).
"""
import math

from ortools.sat.python import cp_model

BLOCK_MINUTES = 5
SCALE = 1000
# One server on duty for one block costs as much as this many passengers waiting for one block.
SERVER_COST = 1.0
SWITCH_COST = 0.5


def block_arrivals(times, minute, blocks):
    """Arrivals per block from the minute after `minute`; block k starts at minute + 1 + k * BLOCK_MINUTES."""
    counts = [0] * blocks
    for time in times:
        if time <= minute:
            continue
        index = max(0, int((time - minute - 1) // BLOCK_MINUTES))
        if index < blocks:
            counts[index] += 1
    return counts


def blocks_left(minute, duration):
    return min(12, max(0, math.ceil((duration - minute - 1) / BLOCK_MINUTES)))


def solve(resources, total=None, locked=0, time_limit=2.0):
    """Servers per block for every resource: {key: [level per block]}, or None without a usable solution.

    A resource is {key, queue, arrivals[per block], rate (per server and minute), current, low, high}.
    The first `locked` blocks keep the current level (operators in transfer cannot serve yet).
    `total` caps the servers on duty in every block across all resources."""
    if not resources:
        return {}
    blocks = len(resources[0]['arrivals'])
    model = cp_model.CpModel()
    peak = model.NewIntVar(0, 10 ** 9, 'peak')
    objective = []
    levels = {}
    for resource in resources:
        key, current = resource['key'], resource['current']
        capacity = max(1, int(resource['rate'] * BLOCK_MINUTES * SCALE))
        bound = (resource['queue'] + sum(resource['arrivals'])) * SCALE
        low, high = min(resource['low'], current), max(resource['high'], current)
        servers = [model.NewIntVar(current if k < locked else low, current if k < locked else high, f'd{key}{k}') for k in range(blocks)]
        queue = [model.NewIntVar(0, bound, f'q{key}{k}') for k in range(blocks)]
        previous_queue, previous_servers = resource['queue'] * SCALE, current
        for k in range(blocks):
            model.Add(queue[k] >= previous_queue + resource['arrivals'][k] * SCALE - capacity * servers[k])
            model.Add(peak >= queue[k])
            change = model.NewIntVar(0, high, f'c{key}{k}')
            model.AddAbsEquality(change, servers[k] - previous_servers)
            objective += [queue[k], int(SERVER_COST * SCALE) * servers[k], int(SWITCH_COST * SCALE) * change]
            previous_queue, previous_servers = queue[k], servers[k]
        # A backlog left at the end of the plan is paid again: it will still be waiting afterwards.
        objective.append(blocks * queue[-1])
        levels[key] = servers
    if total is not None:
        for k in range(blocks):
            model.Add(sum(levels[resource['key']][k] for resource in resources) <= total)
    model.Minimize(sum(objective) + blocks * 4 * peak)
    solver = cp_model.CpSolver()
    solver.parameters.num_search_workers = 1
    solver.parameters.random_seed = 0
    solver.parameters.max_time_in_seconds = time_limit
    if solver.Solve(model) not in (cp_model.OPTIMAL, cp_model.FEASIBLE):
        return None
    return {key: [solver.Value(level) for level in servers] for key, servers in levels.items()}


def desk_steps(levels, minute, current):
    """The plan as absolute (startMinute, desks) steps from the minute after the decision, repeated levels merged."""
    steps, last = [], current
    for k, level in enumerate(levels):
        if level != last:
            steps.append({'startMinute': minute + 1 + k * BLOCK_MINUTES, 'staffCount': level})
            last = level
    return steps


def recommend(minute, duration, limits, controls, desks):
    """What to change after `minute`, or None when there is nothing to plan.

    limits: {deskLimit, reserve, transferMinutes, frozen[ids]}; controls: [{key, queue, times, rate, current}] of the
    controls that may be staffed; desks: {queue, times, rate, current} or None. Arrival times are the simulated ones."""
    blocks = blocks_left(minute, duration)
    if blocks == 0:
        return None
    result = {'blockMinutes': BLOCK_MINUTES, 'blocks': blocks, 'desks': None, 'staff': None, 'reserveUsed': 0}
    if desks is not None:
        arrivals = block_arrivals(desks['times'], minute, blocks)
        if desks['queue'] + sum(arrivals):
            current = desks['current']
            resource = {'key': 'desks', 'queue': desks['queue'], 'arrivals': arrivals, 'rate': desks['rate'], 'current': current,
                        'low': 1, 'high': max(current, limits.get('deskLimit', current))}
            plan = solve([resource])
            if plan is not None and (steps := desk_steps(plan['desks'], minute, current)):
                result['desks'] = steps
    frozen = set(limits.get('frozen', []))
    pool = [control for control in controls if control['key'] not in frozen]
    if len(pool) > 0:
        reserve = max(0, limits.get('reserve', 0))
        transfer = math.ceil(limits.get('transferMinutes', 0) / BLOCK_MINUTES)
        total = sum(control['current'] for control in pool) + reserve
        resources = [{'key': control['key'], 'queue': control['queue'], 'arrivals': block_arrivals(control['times'], minute, blocks),
                      'rate': control['rate'], 'current': control['current'], 'low': 1,
                      'high': max(control['current'], total - (len(pool) - 1))} for control in pool]
        if any(item['queue'] + sum(item['arrivals']) for item in resources):
            plan = solve(resources, total, min(transfer, blocks - 1))
            if plan is not None:
                # The level once the transfer is over is the target; later blocks are re-planned at the next decision.
                index = min(transfer, blocks - 1)
                targets = [{'checkpointId': control['key'], 'current': control['current'], 'target': plan[control['key']][index]}
                           for control in pool]
                if any(item['target'] != item['current'] for item in targets):
                    result['staff'] = targets
                    result['reserveUsed'] = max(0, sum(item['target'] - item['current'] for item in targets))
    return result if result['desks'] or result['staff'] else None

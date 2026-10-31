"""Rolling gate allocation. Times are integer seconds; intervals are half-open.

Started turnarounds retain their gate and arrival. Departed flights are immutable.
The objective first minimizes propagated delay, then gate changes, then gate order.
"""
from ortools.sat.python import cp_model

from app.simulation.events import ordered


class GatePlanningError(ValueError):
    def __init__(self, message, code='gate_plan_infeasible'):
        super().__init__(message)
        self.code = code


def initial(flights):
    return {f['flightId']: {'flightId': f['flightId'], 'gateId': f['gateId'],
            **{key: f[key] + f['delayMinutes'] for key in
               ('arrivalMinute', 'boardingOpenMinute', 'boardingCloseMinute', 'departureMinute')},
            'delayMinutes': f['delayMinutes'], 'isCancelled': f['isCancelled']} for f in flights}


def solve(flights, current, minute, event=None):
    model = cp_model.CpModel()
    seconds = lambda value: round(value * 60)
    limit = 30240 * 60
    ordered = sorted(flights, key=lambda f: f['flightId'])
    gates = sorted({g for f in flights for g in f['eligibleGateIds']})
    intervals = {g: [] for g in gates}
    variables, delay_terms, move_terms, tie_terms = {}, [], [], []
    for f in ordered:
        key = f['flightId']
        old = current[key]
        if f['isCancelled']:
            continue
        target = old['departureMinute'] + (event['delayMinutes'] if event and event['flightId'] == key else 0)
        if event and event['flightId'] == key and old['departureMinute'] < minute:
            raise GatePlanningError('A delay cannot change a flight that has already departed.', 'event_conflict')
        departed = old['departureMinute'] < minute
        arrived = old['arrivalMinute'] < minute
        end = model.new_int_var(seconds(target), seconds(old['departureMinute']) if departed else limit, key + '/end')
        delay = model.new_int_var(0, limit, key + '/delay')
        model.add(delay == end - seconds(f['departureMinute']))
        values = {'departureMinute': end}
        for name in ('arrivalMinute', 'boardingOpenMinute', 'boardingCloseMinute'):
            fixed = departed or old[name] < minute
            v = model.new_int_var(-10080 * 60, limit, key + '/' + name)
            model.add(v == seconds(old[name]) if fixed else v == seconds(f[name]) + delay)
            values[name] = v
        model.add(values['arrivalMinute'] <= values['boardingOpenMinute'])
        model.add(values['boardingOpenMinute'] < values['boardingCloseMinute'])
        model.add(values['boardingCloseMinute'] < end)
        size = model.new_int_var(1, limit + 10080 * 60, key + '/occupancy')
        model.add(size == end - values['arrivalMinute'])
        choices = {}
        for gate in sorted(f['eligibleGateIds']):
            chosen = model.new_bool_var(key + '/' + gate)
            choices[gate] = chosen
            if arrived or departed:
                model.add(chosen == int(gate == old['gateId']))
            intervals[gate].append(model.new_optional_interval_var(values['arrivalMinute'], size, end, chosen, key + '/' + gate))
            if gate != old['gateId']:
                move_terms.append(chosen)
            tie_terms.append(chosen * gates.index(gate))
        model.add_exactly_one(choices.values())
        variables[key] = (values, choices, delay)
        delay_terms.append(delay)
    for row in intervals.values():
        model.add_no_overlap(row)
    for f in ordered:
        previous = f['previousFlightId']
        if previous and f['flightId'] in variables and previous in variables:
            model.add(variables[f['flightId']][0]['arrivalMinute'] >=
                      variables[previous][0]['departureMinute'] + seconds(f['rotationMinutes']))
    # Each second of delay dominates all possible gate changes and deterministic tie costs.
    tie_bound = len(flights) * max(1, len(gates)) + 1
    delay_weight = (len(flights) + 1) * tie_bound
    model.minimize(sum(delay_terms) * delay_weight + sum(move_terms) * tie_bound + sum(tie_terms))
    solver = cp_model.CpSolver()
    solver.parameters.num_search_workers = 1
    solver.parameters.random_seed = 0
    solver.parameters.max_deterministic_time = 5
    status = solver.solve(model)
    if status not in (cp_model.OPTIMAL, cp_model.FEASIBLE):
        if status == cp_model.UNKNOWN:
            raise GatePlanningError('The solver budget ended without a feasible gate plan.', 'gate_plan_timeout')
        if status == cp_model.MODEL_INVALID:
            raise GatePlanningError('The gate planning model is invalid.', 'gate_plan_invalid')
        raise GatePlanningError('No feasible gate plan exists within the bounded planning horizon.')
    result = {key: dict(value) for key, value in current.items()}
    for key, (values, choices, delay) in variables.items():
        result[key] = result[key] | {name: solver.value(var) / 60 for name, var in values.items()} | {
            'gateId': next(g for g, selected in choices.items() if solver.value(selected)),
            'delayMinutes': solver.value(delay) / 60}
    return result, 'optimal' if status == cp_model.OPTIMAL else 'feasible'


def advise(request):
    """Recommend the plan that absorbs a delay reported at request['minute'].

    The plan known at that moment is rebuilt as the journey run builds it: the
    initial plan, then every scheduled delay up to and including that minute.
    The recommendation is the same rolling solve the run would apply. The
    comparison keeps every flight at the gate it holds, so the operator sees
    what the moves save; None when no plan without moves exists.
    """
    flights, minute = request['flights'], request['minute']
    plan, _ = solve(flights, initial(flights), 0)
    scheduled = [e | {'type': 'flight_change'} for e in request['events'] if e['minute'] <= minute]
    for event in ordered(scheduled):
        plan, _ = solve(flights, plan, event['minute'], event)
    recommended, status = solve(flights, plan, minute, request['delay'])
    pinned = [f | {'eligibleGateIds': [plan[f['flightId']]['gateId']]} for f in flights]
    try:
        unmoved, _ = solve(pinned, plan, minute, request['delay'])
    except GatePlanningError:
        unmoved = None
    return {'adviceVersion': '1.0', 'minute': minute, 'solverStatus': status, 'before': list(plan.values()),
            'recommended': list(recommended.values()), 'withoutMoves': list(unmoved.values()) if unmoved else None}

"""Advisory fixed-window gate/handling planning. Never writes operational state."""
import math
import time
from collections import defaultdict

from ortools.sat.python import cp_model

from app.optimization.staffing import configured_solver


def covers(windows, start, end):
    cursor, end = float(start), float(end)
    for window in sorted(windows, key=lambda item: float(item['startMinute'])):
        if float(window['endMinute']) <= cursor:
            continue
        if float(window['startMinute']) > cursor:
            return False
        cursor = float(window['endMinute'])
        if cursor >= end:
            return True
    return False


def validate_config(value, path, errors, error):
    def invalid(field, code='invalid_value'):
        errors.append(error(path + field, code))
    def unique(values, field):
        if len(values) != len(set(values)):
            invalid(field, 'duplicate_id')
    for name, key in [('gates', 'gateId'), ('workers', 'staffMemberId'), ('teams', 'teamId'), ('requirements', 'flightId')]:
        unique([item[key] for item in value[name]], '/' + name)
    gates = {item['gateId'] for item in value['gates']}
    workers = {item['staffMemberId'] for item in value['workers']}
    for gate in value['gates']:
        for window in gate['availability']:
            if float(window['startMinute']) >= float(window['endMinute']):
                invalid('/gates/availability')
    for worker in value['workers']:
        unique(worker['skills'], '/workers/skills')
        for window in worker['shifts']:
            if float(window['startMinute']) >= float(window['endMinute']):
                invalid('/workers/shifts')
    for team in value['teams']:
        unique(team['memberIds'], '/teams/memberIds')
        unique(team['gateIds'], '/teams/gateIds')
        if not set(team['memberIds']) <= workers or not set(team['gateIds']) <= gates:
            invalid('/teams')
    for requirement in value['requirements']:
        unique(requirement['compatibleGateIds'], '/requirements/compatibleGateIds')
        unique(requirement['requiredSkills'], '/requirements/requiredSkills')
        if not set(requirement['compatibleGateIds']) <= gates:
            invalid('/requirements/compatibleGateIds')


def validate_request(value, path, errors, error):
    # The wire uses binary64 times in both runtimes; validate after the same numeric conversion.
    validate_config(value['resources'], path + '/resources', errors, error)
    ids = [item['flightId'] for item in value['flights']]
    if len(ids) != len(set(ids)):
        errors.append(error(path + '/flights', 'duplicate_id'))
    if set(ids) != {item['flightId'] for item in value['resources']['requirements']}:
        errors.append(error(path + '/resources/requirements', 'invalid_value'))
    windows = [w for gate in value['resources']['gates'] for w in gate['availability']]
    windows += [w for worker in value['resources']['workers'] for w in worker['shifts']]
    if (any(float(f['startMinute']) >= float(f['endMinute']) or float(f['endMinute']) > value['durationMinutes'] for f in value['flights'])
            or any(float(w['endMinute']) > value['durationMinutes'] for w in windows)):
        errors.append(error(path, 'out_of_range'))


def choices(request, flight):
    config = request['resources']
    requirement = next(r for r in config['requirements'] if r['flightId'] == flight['flightId'])
    workers = {w['staffMemberId']: w for w in config['workers']}
    gates, teams, exclusions = [], [], []
    def excluded(code, resource, detail):
        exclusions.append({'code': code, 'flightId': flight['flightId'], 'resourceId': resource,
            'message': f"Flight {flight['flightId']} [{flight['startMinute']}, {flight['endMinute']}): {detail}"})
    for gate in config['gates']:
        if gate['gateId'] not in requirement['compatibleGateIds']:
            excluded('gate_compatibility', gate['gateId'], 'gate is outside the declared compatible gates.')
        elif not covers(gate['availability'], flight['startMinute'], flight['endMinute']):
            excluded('gate_availability', gate['gateId'], 'gate availability does not cover the full interval.')
        else:
            gates.append(gate['gateId'])
    for team in config['teams']:
        members = [workers[key] for key in team['memberIds']]
        skills = {skill for member in members for skill in member['skills']}
        missing = set(requirement['requiredSkills']) - skills
        off_shift = [m['staffMemberId'] for m in members if not covers(m['shifts'], flight['startMinute'], flight['endMinute'])]
        if len(members) < requirement['minimumTeamSize']:
            excluded('team_size', team['teamId'], f"requires {requirement['minimumTeamSize']} members; team has {len(members)}.")
        elif missing:
            excluded('team_skills', team['teamId'], 'missing skills: ' + ', '.join(sorted(missing)[:10]) + (f' (and {len(missing)-10} more)' if len(missing) > 10 else '') + '.')
        elif off_shift:
            excluded('team_shift', team['teamId'], 'members outside their shifts: ' + ', '.join(off_shift[:8]) + (f' (and {len(off_shift)-8} more)' if len(off_shift) > 8 else '') + '.')
        elif not set(team['gateIds']).intersection(gates):
            excluded('team_gate_compatibility', team['teamId'], 'team has no compatible available gate for this flight.')
        else:
            teams.append(team['teamId'])
    return gates, teams, exclusions


def solve(request):
    model = cp_model.CpModel()
    config = request['resources']
    flights = sorted(request['flights'], key=lambda item: item['flightId'])
    gates, teams = {}, {}
    assumptions, exclusions = {}, []
    def rule(code, flight=None, resource=None, message=''):
        literal = model.new_bool_var(f'{code}_{flight}_{resource}')
        model.add_assumption(literal)
        assumptions[literal.index] = {'code': code, 'flightId': flight, 'resourceId': resource, 'message': message}
        return literal
    for flight in flights:
        fid = flight['flightId']
        eligible_gates, eligible_teams, excluded = choices(request, flight)
        exclusions.extend(excluded)
        for gid in eligible_gates:
            gates[fid, gid] = model.new_bool_var(f'gate_{fid}_{gid}')
        for tid in eligible_teams:
            teams[fid, tid] = model.new_bool_var(f'team_{fid}_{tid}')
            team = next(t for t in config['teams'] if t['teamId'] == tid)
            model.add(teams[fid, tid] <= sum(gates[fid, gid] for gid in team['gateIds'] if (fid, gid) in gates))
        model.add(sum(gates[fid, gid] for gid in eligible_gates) == 1).only_enforce_if(
            rule('gate_coverage', fid, message=f'Flight {fid} requires exactly one compatible gate available for its full interval.'))
        model.add(sum(teams[fid, tid] for tid in eligible_teams) == 1).only_enforce_if(
            rule('handling_coverage', fid, message=f'Flight {fid} requires exactly one compatible handling team with sufficient skills, size and shifts.'))
    # Capacity changes only at flight starts. Half-open intervals permit immediate resource reuse.
    starts = sorted({f['startMinute'] for f in flights})
    def capacity(code, resource, variables, message):
        if len(variables) < 2:
            return
        literal = rule(code, resource=resource, message=message)
        seen = set()
        for start in starts:
            active = tuple(v for f, v in variables if f['startMinute'] <= start < f['endMinute'])
            indices = tuple(v.index for v in active)
            if len(active) > 1 and indices not in seen:
                model.add(sum(active) <= 1).only_enforce_if(literal)
                seen.add(indices)
    gate_variables, team_variables, member_variables = defaultdict(list), defaultdict(list), defaultdict(list)
    by_flight = {f['flightId']: f for f in flights}
    by_team = {t['teamId']: t for t in config['teams']}
    for (fid, gid), variable in gates.items():
        gate_variables[gid].append((by_flight[fid], variable))
    for (fid, tid), variable in teams.items():
        team_variables[tid].append((by_flight[fid], variable))
        for member in by_team[tid]['memberIds']:
            member_variables[member].append((by_flight[fid], variable))
    for gid, variables in sorted(gate_variables.items()):
        capacity('gate_capacity', gid, variables, f'Gate {gid} can serve at most one flight at any time.')
    for tid, variables in sorted(team_variables.items()):
        capacity('team_capacity', tid, variables, f'Handling team {tid} can serve at most one flight at any time.')
    for member, variables in sorted(member_variables.items()):
        capacity('staff_capacity', member, variables, f'Staff member {member} can serve at most one flight at any time, including across different teams.')
    model.minimize(sum(variable for (fid, gid), variable in gates.items() if gid != by_flight[fid]['currentGateId']))
    deadline = time.monotonic() + float(request['timeLimitSeconds'])
    solver = configured_solver(max(0, deadline - time.monotonic()))
    status = solver.solve(model)
    name = solver.status_name(status)
    result = {'status': {'OPTIMAL': 'optimal', 'FEASIBLE': 'feasible', 'INFEASIBLE': 'infeasible',
              'UNKNOWN': 'timeout', 'MODEL_INVALID': 'invalid_model'}[name], 'solverStatus': name,
              'timedOut': name in ('FEASIBLE', 'UNKNOWN'), 'score': None, 'bestBound': None,
              'assignments': [], 'rules': [], 'constraints': request, 'advisoryOnly': True, 'wallTimeSeconds': solver.wall_time}
    if status in (cp_model.OPTIMAL, cp_model.FEASIBLE):
        for flight in flights:
            fid = flight['flightId']
            result['assignments'].append({'flightId': fid,
                'gateId': next(gid for (f, gid), var in gates.items() if f == fid and solver.value(var)),
                'teamId': next(tid for (f, tid), var in teams.items() if f == fid and solver.value(var))})
        result['score'] = sum(a['gateId'] != by_flight[a['flightId']]['currentGateId'] for a in result['assignments'])
        result['bestBound'] = math.ceil(solver.best_objective_bound)
    elif status == cp_model.INFEASIBLE:
        core = solver.sufficient_assumptions_for_infeasibility()
        # Optimization can return all assumptions. A feasibility pass extracts a more useful conflict set
        # within the original search budget; the proven original core remains valid if time runs out.
        remaining = deadline - time.monotonic()
        if remaining > 0:
            model.clear_objective()
            diagnostic = configured_solver(remaining)
            diagnostic_status = diagnostic.solve(model)
            result['wallTimeSeconds'] += diagnostic.wall_time
            if diagnostic_status == cp_model.INFEASIBLE:
                core = diagnostic.sufficient_assumptions_for_infeasibility()
        result['rules'] = [assumptions[index] for index in core]
        gate_flights = {r['flightId'] for r in result['rules'] if r['code'] == 'gate_coverage'}
        handling_flights = {r['flightId'] for r in result['rules'] if r['code'] == 'handling_coverage'}
        team_reasons = [r for r in exclusions if r['flightId'] in handling_flights and r['code'].startswith('team_')]
        # Gate exclusions explain handling only when teams were blocked by gate compatibility/availability.
        gate_flights.update(r['flightId'] for r in team_reasons if r['code'] == 'team_gate_compatibility')
        result['rules'].extend(r for r in exclusions if r['flightId'] in gate_flights and r['code'].startswith('gate_'))
        result['rules'].extend(team_reasons)
    return result


def validate_result(value, path, errors, error):
    request = value['constraints']
    validate_request(request, path + '/constraints', errors, error)
    if errors:
        return
    def invalid(): errors.append(error(path, 'invalid_result'))
    state = {'OPTIMAL': 'optimal', 'FEASIBLE': 'feasible', 'INFEASIBLE': 'infeasible', 'UNKNOWN': 'timeout', 'MODEL_INVALID': 'invalid_model'}[value['solverStatus']]
    if value['status'] != state or value['timedOut'] != (state in ('feasible', 'timeout')):
        invalid(); return
    flights = {f['flightId']: f for f in request['flights']}
    gates = {g['gateId'] for g in request['resources']['gates']}
    teams = {t['teamId']: t for t in request['resources']['teams']}
    workers = {w['staffMemberId'] for w in request['resources']['workers']}
    for rule in value['rules']:
        code, fid, resource = rule['code'], rule['flightId'], rule['resourceId']
        if code in ('gate_coverage', 'handling_coverage'):
            valid = fid in flights and resource is None
        elif code in ('gate_capacity', 'team_capacity', 'staff_capacity'):
            valid = fid is None and resource in {'gate_capacity': gates, 'team_capacity': teams, 'staff_capacity': workers}[code]
        else:
            valid = fid in flights and resource in (gates if code in ('gate_availability', 'gate_compatibility') else teams)
        if not valid:
            invalid(); return
    if state not in ('optimal', 'feasible'):
        if (value['assignments'] or value['score'] is not None or value['bestBound'] is not None
                or (bool(value['rules']) != (state == 'infeasible'))):
            invalid()
        return
    assignments = value['assignments']
    if (value['rules'] or len(assignments) != len(flights) or {a['flightId'] for a in assignments} != set(flights)):
        invalid(); return
    for i, a in enumerate(assignments):
        flight = flights[a['flightId']]
        gates, eligible_teams, _ = choices(request, flight)
        if a['gateId'] not in gates or a['teamId'] not in eligible_teams or a['gateId'] not in teams[a['teamId']]['gateIds']:
            invalid(); return
        for b in assignments[i+1:]:
            other = flights[b['flightId']]
            if float(flight['startMinute']) >= float(other['endMinute']) or float(other['startMinute']) >= float(flight['endMinute']):
                continue
            if (b['teamId'] not in teams or a['gateId'] == b['gateId'] or a['teamId'] == b['teamId']
                    or set(teams[a['teamId']]['memberIds']).intersection(teams[b['teamId']]['memberIds'])):
                invalid(); return
    score = sum(a['gateId'] != flights[a['flightId']]['currentGateId'] for a in assignments)
    if (value['score'] != score or value['bestBound'] is None or value['bestBound'] > score
            or state == 'optimal' and value['bestBound'] != score):
        invalid()

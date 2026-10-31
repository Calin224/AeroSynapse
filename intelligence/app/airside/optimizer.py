"""CP-SAT advisory scheduling over the exact seeded synthetic population."""

from collections import defaultdict

from ortools.sat.python import cp_model

from app.airside.engine import Plan, eligible, population, replay
from app.airside.models import AirsideRequest


def optimize(request: AirsideRequest):
    flights, fingerprint = population(request)
    model = cp_model.CpModel()
    horizon = request.durationMinutes
    intervals = defaultdict(list)
    decisions = {}
    late_variables = []
    ends = []
    stands = {stand.id: stand for stand in request.stands}

    def stage_options(flight, skill, kind, start, end, duration):
        teams, vehicles = eligible(request, flight, skill, kind)
        options = {"team": [], "vehicle": []}
        for category, resources in (("team", teams), ("vehicle", vehicles)):
            for resource in resources:
                for index, window in enumerate(resource.available):
                    if window.endMinute - window.startMinute < duration:
                        continue
                    selected = model.new_bool_var(f"{flight['id']}_{skill}_{category}_{resource.id}_{index}")
                    model.add(start >= window.startMinute).only_enforce_if(selected)
                    model.add(end <= window.endMinute).only_enforce_if(selected)
                    interval = model.new_optional_interval_var(start, duration, end, selected, f"interval_{selected.name}")
                    intervals[(category, resource.id)].append(interval)
                    if category == "team":
                        for member in resource.memberIds:
                            intervals[("member", member)].append(interval)
                    options[category].append((selected, resource))
            # An empty set deliberately makes the complete schedule infeasible.
            model.add_exactly_one([choice for choice, _ in options[category]])
        return options

    for flight in flights:
        flight_id = flight["id"]
        handling = model.new_int_var(flight["arrivalMinute"], horizon, f"handling_{flight_id}")
        handling_end = model.new_int_var(0, horizon, f"handling_end_{flight_id}")
        pushback = model.new_int_var(0, horizon, f"pushback_{flight_id}")
        end = model.new_int_var(0, horizon, f"end_{flight_id}")
        model.add(handling_end == handling + flight["handlingMinutes"])
        model.add(pushback >= handling_end)
        model.add(end == pushback + flight["pushbackMinutes"])
        # Admission is just in time for handling. Occupancy remains exclusive until departure.
        occupancy = model.new_int_var(1, horizon, f"occupancy_{flight_id}")
        model.add(occupancy == end - handling)
        stand_interval = model.new_interval_var(handling, occupancy, end, f"stand_{flight_id}")
        intervals[("stand", flight["standId"])].append(stand_interval)
        stand_choices = []
        for index, window in enumerate(stands[flight["standId"]].available):
            selected = model.new_bool_var(f"stand_window_{flight_id}_{index}")
            model.add(handling >= window.startMinute).only_enforce_if(selected)
            model.add(end <= window.endMinute).only_enforce_if(selected)
            stand_choices.append(selected)
        model.add_exactly_one(stand_choices)
        handling_options = stage_options(flight, "handling", "service", handling, handling_end, flight["handlingMinutes"])
        pushback_options = stage_options(flight, "pushback", "tug", pushback, end, flight["pushbackMinutes"])
        late = model.new_int_var(0, horizon, f"late_{flight_id}")
        model.add_max_equality(late, [0, pushback - flight["scheduledPushbackMinute"]])
        late_variables.append(late)
        ends.append(end)
        decisions[flight_id] = (handling, pushback, end, handling_options, pushback_options)
    for resource_intervals in intervals.values():
        model.add_no_overlap(resource_intervals)
    # Total readiness delay is the primary objective, total departure time breaks ties.
    # 30 flights * 240 minutes < 10,000, so the weighting is lexicographic.
    model.minimize(10000 * sum(late_variables) + sum(ends))
    solver = cp_model.CpSolver()
    solver.parameters.max_time_in_seconds = request.timeLimitSeconds
    solver.parameters.num_search_workers = 1
    solver.parameters.random_seed = request.seed
    status = solver.solve(model)
    if status not in (cp_model.OPTIMAL, cp_model.FEASIBLE):
        name = "infeasible" if status == cp_model.INFEASIBLE else "timeout"
        return {"status": name, "method": "cp-sat-airside-1.0", "objective": None, "result": None,
                "diagnostics": ["No complete schedule fits all fixed stands, skills, shared members, vehicles, availability windows and observation horizon."
                                if name == "infeasible" else "The time limit expired before a feasible complete schedule was found.",
                                "The independent FIFO simulation remains available for partial backlog analysis."]}

    def selected(options, category):
        return next(resource for choice, resource in options[category] if solver.boolean_value(choice))

    plans = {}
    for flight in flights:
        handling, pushback, end, handling_options, pushback_options = decisions[flight["id"]]
        plans[flight["id"]] = Plan(flight, solver.value(handling), solver.value(handling), solver.value(pushback), solver.value(end),
                                    selected(handling_options, "team"), selected(handling_options, "vehicle"),
                                    selected(pushback_options, "team"), selected(pushback_options, "vehicle"))
    result = replay(request, flights, fingerprint, plans, optimized=True)
    return {"status": "optimal" if status == cp_model.OPTIMAL else "feasible", "method": "cp-sat-airside-1.0",
            "objective": int(round(solver.objective_value)), "result": result,
            "diagnostics": ["Advisory synthetic schedule. Minimize 10,000 × total readiness delay + total departure minutes.",
                            "Every allocated team, shared member, vehicle and stand was checked and replayed through SimPy."]}

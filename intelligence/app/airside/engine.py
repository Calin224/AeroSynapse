"""Synthetic reservation-based FIFO scheduling, replayed on a SimPy clock.

Intervals are half open. Entire services and stand occupancy must fit availability.
The planner reserves a complete feasible path before admitting a flight to a stand.
Future reservations are never exposed as completed work beyond the observation horizon.
"""

from collections import defaultdict
from dataclasses import dataclass
import hashlib
import json
import random

import simpy

from app.airside.models import AirsideRequest


@dataclass
class Plan:
    flight: dict
    stand_start: int
    handling_start: int
    pushback_start: int
    end: int
    handling_team: object
    handling_vehicle: object
    pushback_team: object
    tug: object


def population(request):
    rng = random.Random(request.seed)
    flights = []
    for original in request.turnarounds:
        flight = original.model_dump()
        for field in ("handlingMinutes", "pushbackMinutes"):
            flight[field] = max(1, int(flight[field] * rng.uniform(1 - request.serviceVariation, 1 + request.serviceVariation) + 0.5))
        flights.append(flight)
    encoded = json.dumps({"seed": request.seed, "turnarounds": flights}, sort_keys=True, separators=(",", ":"))
    return flights, hashlib.sha256(encoded.encode()).hexdigest()


def stage_keys(team, vehicle):
    return [("team", team.id), ("vehicle", vehicle.id), *[("member", member) for member in team.memberIds]]


def plan_allocations(plan):
    flight = plan.flight
    allocations = [("stand", flight["standId"], "stand", plan.stand_start, plan.end)]
    for stage, start, end, team, vehicle in (
        ("handling", plan.handling_start, plan.handling_start + flight["handlingMinutes"], plan.handling_team, plan.handling_vehicle),
        ("pushback", plan.pushback_start, plan.end, plan.pushback_team, plan.tug),
    ):
        allocations.extend((kind, resource_id, stage, start, end) for kind, resource_id in stage_keys(team, vehicle))
    return allocations


def fits(resource, start, end):
    return any(window.startMinute <= start and end <= window.endMinute for window in resource.available)


def available(bookings, keys, start, end):
    return all(all(end <= other_start or start >= other_end for other_start, other_end in bookings[key]) for key in keys)


def eligible(request, flight, skill, kind):
    return (
        sorted((team for team in request.teams if skill in team.skills and flight["standId"] in team.standIds), key=lambda item: item.id),
        sorted((vehicle for vehicle in request.vehicles if vehicle.kind == kind and flight["standId"] in vehicle.standIds), key=lambda item: item.id),
    )


def first_stage(teams, vehicles, bookings, start, last_end, duration):
    for minute in range(start, last_end - duration + 1):
        end = minute + duration
        for team in teams:
            if not fits(team, minute, end):
                continue
            for vehicle in vehicles:
                if fits(vehicle, minute, end) and available(bookings, stage_keys(team, vehicle), minute, end):
                    return minute, team, vehicle
    return None


def fifo_plan(request, flights):
    bookings = defaultdict(list)
    plans = {}
    stands = {stand.id: stand for stand in request.stands}
    for flight in sorted(flights, key=lambda value: (value["arrivalMinute"], value["id"])):
        handling_teams, service_vehicles = eligible(request, flight, "handling", "service")
        pushback_teams, tugs = eligible(request, flight, "pushback", "tug")
        stand = stands[flight["standId"]]
        # Each gap in each stand window is considered chronologically. Reservations
        # include both services, so an admitted aircraft cannot overrun its stand window.
        gaps = []
        for window in stand.available:
            cursor = window.startMinute
            for start, end in sorted(bookings[("stand", stand.id)]):
                if end <= cursor or start >= window.endMinute:
                    continue
                if cursor < start:
                    gaps.append((cursor, start))
                cursor = max(cursor, end)
            if cursor < window.endMinute:
                gaps.append((cursor, window.endMinute))
        for gap_start, gap_end in gaps:
            enter = max(flight["arrivalMinute"], gap_start)
            handling = first_stage(handling_teams, service_vehicles, bookings, enter, gap_end, flight["handlingMinutes"])
            if handling is None:
                continue
            handling_start, handling_team, handling_vehicle = handling
            pushback = first_stage(pushback_teams, tugs, bookings, handling_start + flight["handlingMinutes"], gap_end, flight["pushbackMinutes"])
            if pushback is None:
                continue
            pushback_start, pushback_team, tug = pushback
            plan = Plan(flight, enter, handling_start, pushback_start, pushback_start + flight["pushbackMinutes"], handling_team, handling_vehicle, pushback_team, tug)
            plans[flight["id"]] = plan
            for kind, resource_id, _, start, end in plan_allocations(plan):
                bookings[(kind, resource_id)].append((start, end))
            break
    return plans


def validate_plan(request, plans):
    resources = {("stand", stand.id): stand for stand in request.stands}
    resources.update({("team", team.id): team for team in request.teams})
    resources.update({("vehicle", vehicle.id): vehicle for vehicle in request.vehicles})
    bookings = defaultdict(list)
    for plan in plans.values():
        flight = plan.flight
        assert flight["arrivalMinute"] <= plan.stand_start <= plan.handling_start
        assert plan.handling_start + flight["handlingMinutes"] <= plan.pushback_start
        assert plan.end == plan.pushback_start + flight["pushbackMinutes"]
        assert "handling" in plan.handling_team.skills and "pushback" in plan.pushback_team.skills
        assert plan.handling_vehicle.kind == "service" and plan.tug.kind == "tug"
        assert all(flight["standId"] in item.standIds for item in (plan.handling_team, plan.pushback_team, plan.handling_vehicle, plan.tug))
        for kind, resource_id, _, start, end in plan_allocations(plan):
            assert start < end
            assert available(bookings, [(kind, resource_id)], start, end), "Incompatible resource allocations"
            if kind != "member":
                assert fits(resources[(kind, resource_id)], start, end), "Service exceeds availability"
            bookings[(kind, resource_id)].append((start, end))


def replay(request, flights, fingerprint, plans, optimized=False):
    validate_plan(request, plans)
    horizon = request.durationMinutes
    env = simpy.Environment()
    locks = defaultdict(lambda: simpy.Resource(env, capacity=1))
    observed = {flight["id"]: {
        **flight, "standEnteredMinute": None, "handlingStartedMinute": None, "handlingCompletedMinute": None,
        "pushbackReadyMinute": None, "departedMinute": None, "handlingTeamId": None, "handlingVehicleId": None,
        "pushbackTeamId": None, "tugId": None,
    } for flight in flights}

    def execute(plan):
        row = observed[plan.flight["id"]]
        yield env.timeout(plan.stand_start)
        stand_lock = locks[("stand", plan.flight["standId"])].request()
        yield stand_lock
        assert env.now == plan.stand_start
        row["standEnteredMinute"] = int(env.now)
        yield env.timeout(plan.handling_start - env.now)
        for stage, start, end, team, vehicle in (
            ("handling", plan.handling_start, plan.handling_start + plan.flight["handlingMinutes"], plan.handling_team, plan.handling_vehicle),
            ("pushback", plan.pushback_start, plan.end, plan.pushback_team, plan.tug),
        ):
            yield env.timeout(start - env.now)
            acquired = [(locks[key], locks[key].request()) for key in stage_keys(team, vehicle)]
            yield env.all_of([event for _, event in acquired])
            assert env.now == start, "SimPy replay found an allocation conflict"
            if stage == "handling":
                row.update(handlingStartedMinute=int(env.now), handlingTeamId=team.id, handlingVehicleId=vehicle.id)
            else:
                row.update(pushbackReadyMinute=int(env.now), pushbackTeamId=team.id, tugId=vehicle.id)
            yield env.timeout(end - start)
            for resource, event in acquired:
                resource.release(event)
            row["handlingCompletedMinute" if stage == "handling" else "departedMinute"] = int(env.now)
        locks[("stand", plan.flight["standId"])].release(stand_lock)

    for plan in plans.values():
        env.process(execute(plan))
    # Numeric env.run(until=horizon) would omit completions at exactly the horizon.
    while env.peek() <= horizon:
        env.step()

    allocations = []
    conflicts = []
    for flight in flights:
        row = observed[flight["id"]]
        plan = plans.get(flight["id"])
        arrival = flight["arrivalMinute"]
        handling_end = row["handlingCompletedMinute"]
        row["waitingMinutes"] = (row["handlingStartedMinute"] if row["handlingStartedMinute"] is not None else horizon) - arrival
        if handling_end is not None:
            row["waitingMinutes"] += (row["pushbackReadyMinute"] if row["pushbackReadyMinute"] is not None else horizon) - handling_end
        ready_observed = row["pushbackReadyMinute"] if row["pushbackReadyMinute"] is not None else horizon
        row["readinessDelayMinutes"] = max(0, ready_observed - flight["scheduledPushbackMinute"])
        row["status"] = ("departed" if row["departedMinute"] is not None else "pushback" if row["pushbackReadyMinute"] is not None else "ready" if handling_end is not None else "handling" if row["handlingStartedMinute"] is not None else "waiting")
        if plan is None:
            conflicts.append({"turnaroundId": flight["id"], "stage": "stand", "minute": arrival,
                              "reason": "no_complete_available_path", "resourceIds": [flight["standId"]]})
            continue
        for kind, resource_id, stage, start, end in plan_allocations(plan):
            if start < horizon:
                allocations.append({"resourceType": kind, "resourceId": resource_id, "turnaroundId": flight["id"], "stage": stage,
                                    "startMinute": start, "endMinute": min(end, horizon)})
        for stage, required, actual, resource_ids in (
            ("stand", arrival, plan.stand_start, [flight["standId"]]),
            ("handling", plan.stand_start, plan.handling_start, [plan.handling_team.id, plan.handling_vehicle.id]),
            ("pushback", plan.handling_start + flight["handlingMinutes"], plan.pushback_start, [plan.pushback_team.id, plan.tug.id]),
        ):
            if actual > required and required < horizon:
                conflicts.append({"turnaroundId": flight["id"], "stage": stage, "minute": required,
                                  "reason": "stand_occupied_or_unavailable" if stage == "stand" else "team_vehicle_or_member_unavailable",
                                  "resourceIds": resource_ids})

    rows = list(observed.values())
    departed = [row for row in rows if row["departedMinute"] is not None]
    mean = lambda values: round(sum(values) / len(values), 6) if values else 0.0
    snapshots = []
    for minute in range(horizon + 1):
        count = sum(row["departedMinute"] is not None and row["departedMinute"] <= minute for row in rows)
        ready = sum(row["handlingCompletedMinute"] is not None and row["handlingCompletedMinute"] <= minute
                    and (row["pushbackReadyMinute"] is None or row["pushbackReadyMinute"] > minute) for row in rows)
        snapshots.append({"minute": minute, "departedCount": count, "readyCount": ready,
                          "backlogCount": sum(row["arrivalMinute"] <= minute for row in rows) - count})
    return {
        "contractVersion": "1.0", "engine": "simpy-airside-cp-sat-replay-1.0" if optimized else "simpy-airside-fifo-1.0",
        "synthetic": True, "populationFingerprint": fingerprint, "request": request.model_dump(),
        "kpis": {"turnaroundCount": len(rows), "departedCount": len(departed), "backlogCount": len(rows) - len(departed),
                 "readyCount": snapshots[-1]["readyCount"],
                 "meanTurnaroundMinutes": mean([row["departedMinute"] - row["arrivalMinute"] for row in departed]),
                 "meanWaitingMinutes": mean([row["waitingMinutes"] for row in rows]),
                 "meanReadinessDelayMinutes": mean([row["readinessDelayMinutes"] for row in rows]),
                 "lateCount": sum(row["readinessDelayMinutes"] > 0 for row in rows), "conflictCount": len(conflicts)},
        "turnarounds": rows, "allocations": allocations, "conflicts": conflicts, "snapshots": snapshots,
    }


def simulate(request: AirsideRequest):
    flights, fingerprint = population(request)
    return replay(request, flights, fingerprint, fifo_plan(request, flights))

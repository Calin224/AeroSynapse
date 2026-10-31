"""FIFO SimPy resources, virtual minutes, no storage or external baggage systems."""
import hashlib
import json
import random

import simpy

from app.baggage.models import BaggageRequest

RESOURCE_IDS = ("checkIn", "beltA", "beltB", "sorting", "loading")


def simulate(request: BaggageRequest) -> dict:
    env = simpy.Environment()
    horizon = request.durationMinutes
    configs = request.resources.model_dump()
    resources = {key: simpy.Resource(env, capacity=configs[key]["capacity"]) for key in RESOURCE_IDS}
    stats = {key: {"queued": 0, "inService": 0, "completed": 0, "peakQueue": 0, "peakBusy": 0}
             for key in RESOURCE_IDS}
    incident = request.incident
    arrival_rng = random.Random(request.seed)
    # Streams are independent of routing and resource scheduling: common random inputs across scenarios.
    population = []
    for index in range(request.unitCount):
        rng = random.Random(f"baggage-1.0:{request.seed}:{index}")
        population.append({
            "id": f"synthetic-bag-{index + 1:05d}",
            "arrivalMinute": arrival_rng.uniform(0, request.arrivalWindowMinutes),
            "nominalBelt": "beltA" if index % 2 == 0 else "beltB",
            "serviceFactors": [rng.uniform(1-request.serviceVariation, 1+request.serviceVariation) for _ in range(4)],
        })
    fingerprint = hashlib.sha256(json.dumps(population, sort_keys=True, separators=(",", ":")).encode()).hexdigest()
    units = [{"id": person["id"], "arrivalMinute": person["arrivalMinute"],
              "nominalBelt": person["nominalBelt"], "belt": None, "diverted": False,
              "loadedMinute": None, "stages": []} for person in population]

    def stopped(key, minute):
        return bool(incident and key == incident.belt and
                    incident.startMinute <= minute < incident.startMinute + incident.durationMinutes)

    def visit(unit, key, duration):
        state = stats[key]
        trace = {"resourceId": key, "enteredMinute": env.now, "startedMinute": None,
                 "completedMinute": None, "serviceMinutes": duration, "workSegments": []}
        unit["stages"].append(trace)
        state["queued"] += 1
        with resources[key].request() as slot:
            # An immediately granted request is not a queue observation.
            if not slot.triggered:
                state["peakQueue"] = max(state["peakQueue"], len(resources[key].queue))
            yield slot
            state["queued"] -= 1
            state["inService"] += 1
            state["peakBusy"] = max(state["peakBusy"], state["inService"])
            trace["startedMinute"] = env.now
            remaining = duration
            while remaining > 1e-10:
                if stopped(key, env.now):
                    # A failed belt retains its occupied slots and resumes the remaining work.
                    yield env.timeout(incident.startMinute + incident.durationMinutes - env.now)
                    continue
                work = remaining
                if incident and key == incident.belt and env.now < incident.startMinute:
                    work = min(work, incident.startMinute - env.now)
                trace["workSegments"].append({"startMinute": env.now, "endMinute": min(horizon, env.now + work)})
                yield env.timeout(work)
                remaining = max(0, remaining-work)
            trace["completedMinute"] = env.now
            state["inService"] -= 1
            state["completed"] += 1

    def journey(person, unit):
        yield env.timeout(person["arrivalMinute"])
        yield env.process(visit(unit, "checkIn", configs["checkIn"]["serviceMinutes"] * person["serviceFactors"][0]))
        belt = person["nominalBelt"]
        if stopped(belt, env.now) and incident.divert:
            belt = "beltB" if belt == "beltA" else "beltA"
            unit["diverted"] = True
        unit["belt"] = belt
        for key, factor in zip((belt, "sorting", "loading"), person["serviceFactors"][1:]):
            yield env.process(visit(unit, key, configs[key]["serviceMinutes"] * factor))
        unit["loadedMinute"] = env.now

    for person, unit in zip(population, units):
        env.process(journey(person, unit))

    snapshots = []
    for minute in range(horizon + 1):
        # Drain ALL events at the boundary, including service completions at the horizon.
        while env.peek() <= minute:
            env.step()
        snapshots.append({"minute": minute, "loadedCount": stats["loading"]["completed"],
                          "resources": [{"resourceId": key, "capacity": configs[key]["capacity"],
                                         "queued": stats[key]["queued"], "inService": stats[key]["inService"],
                                         "completed": stats[key]["completed"], "stopped": stopped(key, minute)}
                                        for key in RESOURCE_IDS]})

    reports = []
    for key in RESOURCE_IDS:
        traces = [stage for unit in units for stage in unit["stages"] if stage["resourceId"] == key]
        waiting = sum((trace["startedMinute"] if trace["startedMinute"] is not None else horizon)-trace["enteredMinute"]
                      for trace in traces)
        worked = sum(segment["endMinute"]-segment["startMinute"] for trace in traces for segment in trace["workSegments"])
        reports.append({"resourceId": key, **configs[key], **stats[key], "stopped": stopped(key, horizon),
                        "totalWaitingMinutes": waiting,
                        "meanWaitingMinutes": waiting / len(traces) if traces else 0,
                        "utilization": worked / (horizon * configs[key]["capacity"])})
    loaded = [unit for unit in units if unit["loadedMinute"] is not None]
    delays = [max(0, (unit["loadedMinute"] if unit["loadedMinute"] is not None else horizon)
                  - request.loadingDeadlineMinute) for unit in units]
    waiting = sum(item["totalWaitingMinutes"] for item in reports)
    return {"contractVersion": "1.0", "engine": "simpy-baggage-1.0", "synthetic": True,
            "populationFingerprint": fingerprint, "request": request.model_dump(),
            "kpis": {"unitCount": len(units), "loadedCount": len(loaded), "backlogCount": len(units)-len(loaded),
                     "throughputPerMinute": len(loaded)/horizon,
                     "totalWaitingMinutes": waiting, "meanWaitingMinutes": waiting/len(units) if units else 0,
                     "totalDelayMinutes": sum(delays), "meanDelayMinutes": sum(delays)/len(units) if units else 0,
                     "lateCount": sum(delay > 0 for delay in delays),
                     "divertedCount": sum(unit["diverted"] for unit in units)},
            "resources": reports, "snapshots": snapshots, "units": units}

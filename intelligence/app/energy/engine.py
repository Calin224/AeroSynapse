"""Constant-power accounting with SimPy traffic, never a live measurement."""
import hashlib
import json
import math
from decimal import Decimal, localcontext
from uuid import NAMESPACE_URL, uuid5

import simpy

from app.contracts.models import SimulationRequest
from app.energy.models import EnergyRequest
from app.simulation.metrics import wait_statistics
from app.simulation.processes import simulate


DEFAULTS = {"checkpoint": 1.5, "lighting": 0.05, "resource": 0.4}
FORMULAS = {
    "energy": "E [kWh] = sum(units * power [kW/unit] * activeMinutes [min] / 60).",
    "cost": "Cost [RON] = E [kWh] * tariff [RON/kWh]; absent tariff yields no monetary estimate.",
    "sensitivity": "One coefficient at a time at base * (1 +/- sensitivityPercent/100); other coefficients, schedules and traffic held fixed.",
}
ASSUMPTIONS = [
    "Exclusively synthetic demonstration; estimates do not represent the airport's actual consumption or tariff.",
    "Every coefficient and any supplied tariff is an assumption, never a measurement or telemetry.",
    "Missing power uses a default assumption: checkpoint 1.5 kW/unit, lighting 0.05 kW/unit, resource 0.4 kW/unit.",
    "Active checkpoint units draw constant power for the entire horizon; zero units consume zero energy.",
    "Lighting and resource loads draw constant power from minute 0 until their configured active duration; no standby, startup or traffic-dependent demand is modeled.",
    "Same seed, checkpoint IDs, arrival rate and service rates produce the same synthetic arrivals and service inputs across compared capacities. Demand is assigned round-robin by checkpoint ID; no cross-checkpoint rerouting is modeled.",
    "Traffic uses the existing simpy-kpi-1.0 engine. Wait mean and nearest-rank p95 include completed service only, and remain null if none complete. Unprocessed demand includes people who have not arrived by the horizon.",
    "Sensitivity is a one-at-a-time arithmetic estimate on the candidate with traffic held fixed, not a forecast or a probabilistic confidence interval.",
    "kWh and the hard budget use decimal arithmetic from configured decimal values before JSON number conversion; no tolerance permits an over-budget assignment.",
]


def coefficients(request: EnergyRequest):
    return [{"id": "checkpointPowerKw", "kind": "checkpoint", "powerKw": DEFAULTS["checkpoint"] if request.checkpointPowerKw is None else request.checkpointPowerKw,
             "source": "default-assumption" if request.checkpointPowerKw is None else "configured-assumption"},
            *[{"id": load.id, "kind": load.kind, "powerKw": DEFAULTS[load.kind] if load.powerKw is None else load.powerKw,
               "source": "default-assumption" if load.powerKw is None else "configured-assumption"} for load in request.loads]]


def energy(request, assignment, variant):
    effective = {item["id"]: item for item in coefficients(request)}
    items = []
    for checkpoint, units in zip(sorted(request.checkpoints, key=lambda cp: cp.id), assignment):
        coefficient = effective["checkpointPowerKw"]
        items.append({**coefficient, "id": checkpoint.id, "units": units, "activeMinutes": float(request.durationMinutes),
                      "kwh": units * coefficient["powerKw"] * request.durationMinutes / 60})
    for load in request.loads:
        coefficient = effective[load.id]
        minutes = load.baselineActiveMinutes if variant == "baseline" else load.candidateActiveMinutes
        items.append({**coefficient, "units": load.units, "activeMinutes": minutes,
                      "kwh": load.units * coefficient["powerKw"] * minutes / 60})
    with localcontext() as context:
        # Sum kW-minutes before division. Recurring individual fractions such as
        # 3 * (0.1 kW * 40 min / 60) must equal a 0.2 kWh hard boundary.
        # Includes the full finite JSON float exponent range (including subnormals)
        # for products and sums, so a tiny positive load is never lost to a budget.
        context.prec = 1024
        numerators = [Decimal(item["units"]) * Decimal(str(item["powerKw"])) * Decimal(str(item["activeMinutes"])) for item in items]
        for item, value in zip(items, numerators):
            item["kwh"] = float(value / Decimal(60))
        numerator = sum(numerators, Decimal(0))
        total = numerator / Decimal(60)
        within = None if request.energyBudgetKwh is None else numerator <= Decimal(str(request.energyBudgetKwh)) * Decimal(60)
        cost = None if request.tariffRonPerKwh is None else float(numerator * Decimal(str(request.tariffRonPerKwh)) / Decimal(60))
    return {"totalKwh": float(total), "costRon": cost, "withinBudget": within, "items": items}


def simulate_scenario(request, assignment, variant, cache=None):
    assignment = tuple(assignment)
    accounting = energy(request, assignment, variant)
    if cache is not None and assignment in cache:
        traffic_result, fingerprint, run_id, snapshots = cache[assignment]
    else:
        # IDs and clock identify a frozen synthetic experiment; no database scenario is created.
        frozen = {"seed": request.seed, "duration": request.durationMinutes, "count": request.passengerCount,
                  "arrival": request.arrivalRatePerMinute,
                  "checkpoints": [(cp.id, cp.serviceRatePerMinute) for cp in sorted(request.checkpoints, key=lambda cp: cp.id)]}
        experiment = json.dumps(frozen, sort_keys=True, separators=(",", ":"))
        run_id = str(uuid5(NAMESPACE_URL, "energy-simpy:" + experiment + repr(assignment)))
        simulation = SimulationRequest.model_validate({
            "contractVersion": "1.0", "runId": run_id, "scenarioId": str(uuid5(NAMESPACE_URL, "synthetic-energy-scenario:" + experiment)),
            "scenarioRevision": 1, "layoutVersionId": str(uuid5(NAMESPACE_URL, "synthetic-energy-layout")),
            "startsAtUtc": "2026-01-01T00:00:00Z", "timeZone": "UTC", "durationMinutes": request.durationMinutes,
            "seed": request.seed, "passengerCount": request.passengerCount, "arrivalRatePerMinute": request.arrivalRatePerMinute,
            "checkpoints": [{"checkpointId": cp.id, "serviceRatePerMinute": cp.serviceRatePerMinute, "staffCount": units}
                            for cp, units in zip(sorted(request.checkpoints, key=lambda cp: cp.id), assignment)],
            "engine": "simpy-kpi-1.0", "snapshotIntervalMinutes": max(1, math.ceil(request.durationMinutes / 60)),
        })
        result = simulate(simulation)
        if result.status != "completed":
            raise RuntimeError("Synthetic traffic simulation did not complete.")
        process = result.process_result
        # Fingerprint actual generated inputs, not assignment or post-service state.
        population = [(p["id"], p["checkpointId"], p["arrivalMinute"], p["serviceMinutes"]) for p in process["passengers"]]
        fingerprint = hashlib.sha256(json.dumps({"frozenExperiment": frozen, "arrivals": population},
                                                separators=(",", ":")).encode()).hexdigest()
        waits = [p["queueWaitMinutes"] for p in process["passengers"] if p["securityClearedMinute"] is not None]
        mean, _, p95 = wait_statistics(waits)
        processed = result.kpi_result["processedCount"]
        traffic_result = {"populationCount": request.passengerCount, "arrivedCount": process["arrivedCount"],
                          "notArrivedCount": process["notArrivedCount"], "processedCount": processed,
                          "unprocessedCount": request.passengerCount - processed, "queuedCount": process["queuedCount"],
                          "processingCount": process["processingCount"], "throughputPerMinute": processed / request.durationMinutes,
                          "meanWaitMinutes": mean, "p95WaitMinutes": p95}
        by_minute = {}
        for snap in result.kpi_result["snapshots"]:
            totals = by_minute.setdefault(snap["minute"], {"queuedCount": 0, "processedCount": 0})
            totals["queuedCount"] += snap["queuedPassengers"]
            totals["processedCount"] += snap["processedPassengers"]
        snapshots = [{"minute": minute, **values} for minute, values in sorted(by_minute.items())]
        if cache is not None:
            cache[assignment] = traffic_result, fingerprint, run_id, snapshots
    combined = [{**snap, "energyKwhCumulative": math.fsum(item["units"] * item["powerKw"] * min(snap["minute"], item["activeMinutes"]) / 60
                                                         for item in accounting["items"])} for snap in snapshots]
    return {"variant": variant,
            "assignment": [{"checkpointId": cp.id, "activeUnits": units} for cp, units in zip(sorted(request.checkpoints, key=lambda cp: cp.id), assignment)],
            "energy": accounting, "traffic": traffic_result, "populationFingerprint": fingerprint,
            "simulationRunId": run_id, "simulationEngine": "simpy-kpi-1.0", "simulationLibraryVersion": simpy.__version__, "snapshots": combined}


def sensitivity(request, candidate):
    accounting = candidate["energy"]
    total, tariff = accounting["totalKwh"], request.tariffRonPerKwh
    fraction = request.sensitivityPercent / 100
    rows = []
    for coefficient in coefficients(request):
        affected = math.fsum(item["kwh"] for item in accounting["items"]
                             if (item["kind"] == "checkpoint" if coefficient["kind"] == "checkpoint" else item["id"] == coefficient["id"]))
        lower, upper = max(0.0, total - affected * fraction), total + affected * fraction
        value = coefficient["powerKw"]
        rows.append({"coefficientId": coefficient["id"], "parameter": "powerKw",
                     "lowerValue": value * (1 - fraction), "baseValue": value, "upperValue": value * (1 + fraction),
                     "lowerTotalKwh": lower, "baseTotalKwh": total, "upperTotalKwh": upper,
                     "lowerCostRon": None if tariff is None else lower * tariff, "baseCostRon": accounting["costRon"],
                     "upperCostRon": None if tariff is None else upper * tariff})
    if tariff is not None:
        rows.append({"coefficientId": "tariffRonPerKwh", "parameter": "tariffRonPerKwh",
                     "lowerValue": tariff * (1 - fraction), "baseValue": tariff, "upperValue": tariff * (1 + fraction),
                     "lowerTotalKwh": total, "baseTotalKwh": total, "upperTotalKwh": total,
                     "lowerCostRon": total * tariff * (1 - fraction), "baseCostRon": accounting["costRon"],
                     "upperCostRon": total * tariff * (1 + fraction)})
    return rows


def compare(request):
    checkpoints = sorted(request.checkpoints, key=lambda cp: cp.id)
    cache = {}
    baseline = simulate_scenario(request, [cp.baselineUnits for cp in checkpoints], "baseline", cache)
    candidate = simulate_scenario(request, [cp.candidateUnits for cp in checkpoints], "candidate", cache)
    return {"contractVersion": "1.0", "engine": "energy-analytics-1.0", "synthetic": True,
            "request": request.model_dump(), "effectiveCoefficients": coefficients(request),
            "baseline": baseline, "candidate": candidate, "sensitivity": sensitivity(request, candidate),
            "assumptions": ASSUMPTIONS.copy(), "formulas": FORMULAS.copy()}

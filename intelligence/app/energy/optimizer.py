"""Exhaustive advisory search in a declared bounded integer capacity grid."""
import math
from itertools import product

from app.energy.engine import ASSUMPTIONS, FORMULAS, coefficients, energy, simulate_scenario


MAX_CONFIGURATIONS = 128
MAX_MODELED_WORK = 20000


class ConfigurationLimitError(ValueError):
    pass


def objective(result):
    traffic = result["traffic"]
    return (traffic["unprocessedCount"], math.inf if traffic["meanWaitMinutes"] is None else traffic["meanWaitMinutes"],
            result["energy"]["totalKwh"], tuple(item["activeUnits"] for item in result["assignment"]))


def optimize(request):
    checkpoints = sorted(request.checkpoints, key=lambda cp: cp.id)
    count = math.prod(cp.maxUnits - cp.minUnits + 1 for cp in checkpoints)
    if count > MAX_CONFIGURATIONS:
        raise ConfigurationLimitError(f"Declared capacity grid has {count} configurations; limit is {MAX_CONFIGURATIONS}. Narrow minUnits/maxUnits to optimize.")
    # Cheap budget accounting precedes both the work guard and every SimPy run.
    feasible_assignments = []
    for assignment in product(*(range(cp.minUnits, cp.maxUnits + 1) for cp in checkpoints)):
        accounting = energy(request, assignment, "optimized")
        if accounting["withinBudget"] is False:
            continue
        feasible_assignments.append(assignment)
    interval = math.ceil(request.durationMinutes / 60)
    snapshot_count = math.ceil(request.durationMinutes / interval) + 1
    work_per_configuration = request.passengerCount + len(checkpoints) * snapshot_count
    modeled_work = len(feasible_assignments) * work_per_configuration
    if modeled_work > MAX_MODELED_WORK:
        raise ConfigurationLimitError(
            f"Budget-feasible optimization requires modeled work {modeled_work}, exceeding limit {MAX_MODELED_WORK} "
            f"({len(feasible_assignments)} feasible configurations * ({request.passengerCount} configured passengers + "
            f"{len(checkpoints)} checkpoints * {snapshot_count} snapshots)). "
            "Reduce the capacity grid or passenger population, or tighten the simulated energy budget.")
    candidates, cache = [], {}
    for assignment in feasible_assignments:
        candidates.append(simulate_scenario(request, assignment, "optimized", cache))
    candidates.sort(key=objective)
    return {"contractVersion": "1.0", "synthetic": True,
            "status": "optimal" if candidates else "infeasible", "method": "bounded-enumeration-simpy-1.0",
            "request": request.model_dump(), "configurationCount": count,
            "effectiveCoefficients": coefficients(request), "assumptions": ASSUMPTIONS.copy(), "formulas": FORMULAS.copy(),
            "feasibleCount": len(candidates), "evaluatedCount": len(candidates),
            "best": candidates[0] if candidates else None, "candidates": candidates,
            "diagnostics": [
                "Exhaustive optimum only within the declared minUnits/maxUnits integer grid, with fixed candidate lighting/resource schedules.",
                "Hard simulated kWh budget filters assignments before running the existing SimPy traffic engine; no configuration is applied to the security MVP.",
                "Objective: minimize unprocessed population, then global completed-service mean wait (undefined means infinity), then kWh, then sorted checkpoint assignment.",
                f"Evaluated {len(candidates)} of {count} declared configurations; {count - len(candidates)} rejected by the simulated energy budget.",
                "All power and tariff values are assumptions. This report does not represent actual airport consumption.",
            ]}

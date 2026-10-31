import copy
import json
from itertools import product
from pathlib import Path

import pytest
import simpy
from fastapi import FastAPI
from fastapi.testclient import TestClient
from jsonschema import Draft202012Validator
from pydantic import ValidationError

from app.contracts.models import SimulationRequest
from app.energy import engine, optimizer
from app.energy.engine import compare, energy, simulate_scenario
from app.energy.models import EnergyComparison, EnergyRequest, OptimizeResult
from app.energy.optimizer import ConfigurationLimitError, objective, optimize
from app.energy.routes import router
from app.simulation.metrics import wait_statistics
from app.simulation.processes import simulate


def payload(**changes):
    return {
        "contractVersion": "1.0", "synthetic": True, "seed": 54, "durationMinutes": 60,
        "passengerCount": 200, "arrivalRatePerMinute": 8.0,
        "checkpointPowerKw": 1.5, "tariffRonPerKwh": 1.2, "energyBudgetKwh": 6.0,
        "sensitivityPercent": 20.0,
        "checkpoints": [{"id": "00000000-0000-0000-0000-000000000054", "label": "Synthetic checkpoint A",
                         "minUnits": 0, "maxUnits": 4, "baselineUnits": 1, "candidateUnits": 2, "serviceRatePerMinute": 2.0}],
        "loads": [{"id": "lights", "kind": "lighting", "units": 10, "powerKw": 0.05,
                   "baselineActiveMinutes": 60.0, "candidateActiveMinutes": 30.0},
                  {"id": "resource", "kind": "resource", "units": 1, "powerKw": 0.4,
                   "baselineActiveMinutes": 30.0, "candidateActiveMinutes": 15.0}],
        **changes,
    }


def run(body=None):
    result = compare(EnergyRequest.model_validate(body or payload()))
    EnergyComparison.model_validate(result)
    return result


def solve(body=None):
    result = optimize(EnergyRequest.model_validate(body or payload()))
    OptimizeResult.model_validate(result)
    return result


def test_analytical_energy_cost_and_simultaneous_traffic():
    result = run()
    baseline, candidate = result["baseline"], result["candidate"]
    assert baseline["energy"]["totalKwh"] == pytest.approx(1.5 + 0.5 + 0.2)
    assert candidate["energy"]["totalKwh"] == pytest.approx(3 + 0.25 + 0.1)
    assert baseline["energy"]["costRon"] == pytest.approx(2.2 * 1.2)
    assert candidate["energy"]["costRon"] == pytest.approx(3.35 * 1.2)
    assert candidate["traffic"]["processedCount"] > baseline["traffic"]["processedCount"]
    assert candidate["traffic"]["meanWaitMinutes"] < baseline["traffic"]["meanWaitMinutes"]
    assert "kWh" in result["formulas"]["energy"] and "RON/kWh" in result["formulas"]["cost"]
    assert all(item["source"] == "configured-assumption" for item in result["effectiveCoefficients"])


def test_missing_coefficients_use_explicit_default_assumptions_and_no_tariff_cost():
    body = payload(checkpointPowerKw=None, tariffRonPerKwh=None, energyBudgetKwh=None)
    for item in body["loads"]:
        item["powerKw"] = None
    result = run(body)
    assert [(c["powerKw"], c["source"]) for c in result["effectiveCoefficients"]] == [
        (1.5, "default-assumption"), (0.05, "default-assumption"), (0.4, "default-assumption")]
    for scenario in (result["baseline"], result["candidate"]):
        assert scenario["energy"]["costRon"] is None
        assert scenario["energy"]["withinBudget"] is None
        assert all(item["source"] == "default-assumption" for item in scenario["energy"]["items"])
    assert all(s["parameter"] == "powerKw" and s["baseCostRon"] is None for s in result["sensitivity"])
    assert any("never a measurement" in text for text in result["assumptions"])


def test_zero_power_tariff_and_budget_are_valid_configured_values():
    body = payload(checkpointPowerKw=0, tariffRonPerKwh=0, energyBudgetKwh=0)
    for item in body["loads"]:
        item["powerKw"] = 0
    result = run(body)
    for scenario in (result["baseline"], result["candidate"]):
        assert scenario["energy"]["totalKwh"] == scenario["energy"]["costRon"] == 0
        assert scenario["energy"]["withinBudget"] is True
        assert all(item["source"] == "configured-assumption" for item in scenario["energy"]["items"])
    assert len(result["sensitivity"]) == 4
    assert all(row["lowerValue"] == row["baseValue"] == row["upperValue"] == 0 for row in result["sensitivity"])
    assert solve(body)["feasibleCount"] == 5


def test_omitted_coefficients_tariff_and_budget_match_explicit_null_assumptions():
    body = payload()
    for field in ("checkpointPowerKw", "tariffRonPerKwh", "energyBudgetKwh"):
        del body[field]
    for item in body["loads"]:
        del item["powerKw"]
    result = run(body)
    assert all(item["source"] == "default-assumption" for item in result["effectiveCoefficients"])
    assert result["candidate"]["energy"]["costRon"] is None
    assert result["candidate"]["energy"]["withinBudget"] is None
    assert result["request"]["checkpointPowerKw"] is None


def test_shared_checkpoint_and_per_load_sensitivity_and_tariff_oat():
    body = payload()
    body["checkpoints"].append({**body["checkpoints"][0], "id": "00000000-0000-0000-0000-000000000055"})
    result = run(body)
    rows = {s["coefficientId"]: s for s in result["sensitivity"]}
    total = result["candidate"]["energy"]["totalKwh"]
    assert rows["checkpointPowerKw"]["lowerTotalKwh"] == pytest.approx(total - 6 * 0.2)
    assert rows["checkpointPowerKw"]["upperTotalKwh"] == pytest.approx(total + 6 * 0.2)
    assert rows["lights"]["lowerTotalKwh"] == pytest.approx(total - 0.25 * 0.2)
    assert rows["resource"]["upperTotalKwh"] == pytest.approx(total + 0.1 * 0.2)
    tariff = rows["tariffRonPerKwh"]
    assert tariff["lowerTotalKwh"] == tariff["baseTotalKwh"] == tariff["upperTotalKwh"] == total
    assert tariff["lowerCostRon"] == pytest.approx(total * 1.2 * 0.8)
    assert tariff["upperCostRon"] == pytest.approx(total * 1.2 * 1.2)


def test_same_actual_rng_population_service_inputs_and_engine_are_exposed():
    result = run()
    baseline, candidate = result["baseline"], result["candidate"]
    assert baseline["populationFingerprint"] == candidate["populationFingerprint"]
    assert len(baseline["populationFingerprint"]) == 64
    assert baseline["simulationRunId"] != candidate["simulationRunId"]
    assert baseline["simulationLibraryVersion"] == candidate["simulationLibraryVersion"] == simpy.__version__
    assert baseline["simulationEngine"] == "simpy-kpi-1.0"
    repeated = run()
    assert repeated == result
    altered = run(payload(seed=55))
    assert altered["baseline"]["populationFingerprint"] != baseline["populationFingerprint"]


def test_existing_simpy_engine_is_called_and_global_waits_match_completed_population(monkeypatch):
    captured = []
    original = engine.simulate

    def recording(request):
        assert isinstance(request, SimulationRequest)
        assert request.engine == "simpy-kpi-1.0"
        result = original(request)
        captured.append(result)
        return result

    monkeypatch.setattr(engine, "simulate", recording)
    body = payload()
    body["checkpoints"].append({**body["checkpoints"][0], "id": "00000000-0000-0000-0000-000000000055", "serviceRatePerMinute": 0.8})
    result = run(body)
    assert len(captured) == 2
    for variant, original_result in zip(("baseline", "candidate"), captured):
        waits = [p["queueWaitMinutes"] for p in original_result.process_result["passengers"] if p["securityClearedMinute"] is not None]
        mean, _, p95 = wait_statistics(waits)
        assert result[variant]["traffic"]["meanWaitMinutes"] == mean
        assert result[variant]["traffic"]["p95WaitMinutes"] == p95


def test_equal_capacity_reuses_traffic_with_distinct_load_schedules(monkeypatch):
    calls = []
    original = engine.simulate

    def recording(request):
        calls.append(request)
        return original(request)

    monkeypatch.setattr(engine, "simulate", recording)
    body = payload()
    body["checkpoints"][0]["candidateUnits"] = 1
    result = run(body)
    assert len(calls) == 1
    assert result["baseline"]["traffic"] == result["candidate"]["traffic"]
    assert result["baseline"]["energy"]["totalKwh"] != result["candidate"]["energy"]["totalKwh"]


def test_zero_capacity_null_wait_and_reconciled_demand():
    body = payload()
    body["checkpoints"][0]["baselineUnits"] = 0
    result = run(body)["baseline"]["traffic"]
    assert result["processedCount"] == result["processingCount"] == 0
    assert result["unprocessedCount"] == result["queuedCount"] == result["populationCount"] == 200
    assert result["meanWaitMinutes"] is result["p95WaitMinutes"] is None


def test_empty_demand_and_unarrived_demand_are_honest():
    empty = run(payload(passengerCount=0, arrivalRatePerMinute=0))["candidate"]["traffic"]
    assert all(empty[key] == 0 for key in ("populationCount", "processedCount", "unprocessedCount", "queuedCount", "processingCount", "throughputPerMinute"))
    assert empty["meanWaitMinutes"] is None
    traffic = run(payload(durationMinutes=60, arrivalRatePerMinute=0.001))["candidate"]["traffic"]
    assert traffic["notArrivedCount"] > 0
    assert traffic["populationCount"] == traffic["arrivedCount"] + traffic["notArrivedCount"]
    assert traffic["unprocessedCount"] == traffic["queuedCount"] + traffic["processingCount"] + traffic["notArrivedCount"]


def test_fingerprint_includes_frozen_inputs_when_no_one_arrives():
    first = run(payload(arrivalRatePerMinute=0.000001))
    second = run(payload(arrivalRatePerMinute=0.000001, seed=55))
    assert first["baseline"]["traffic"]["arrivedCount"] == second["baseline"]["traffic"]["arrivedCount"] == 0
    assert first["baseline"]["populationFingerprint"] == first["candidate"]["populationFingerprint"]
    assert first["baseline"]["populationFingerprint"] != second["baseline"]["populationFingerprint"]


def test_snapshots_show_cumulative_energy_with_load_stop_times():
    result = run()["candidate"]
    assert result["snapshots"][0] == {"minute": 0, "queuedCount": 0, "processedCount": 0, "energyKwhCumulative": 0}
    last = result["snapshots"][-1]
    assert last["minute"] == 60
    assert last["energyKwhCumulative"] == pytest.approx(result["energy"]["totalKwh"])
    assert last["queuedCount"] == result["traffic"]["queuedCount"]
    assert last["processedCount"] == result["traffic"]["processedCount"]
    at_30 = next(s for s in result["snapshots"] if s["minute"] == 30)
    assert at_30["energyKwhCumulative"] == pytest.approx(2 * 1.5 * 30 / 60 + 10 * 0.05 * 30 / 60 + 0.4 * 15 / 60)


def test_optimizer_all_feasible_candidates_match_independent_brute_force_objective():
    body = payload(energyBudgetKwh=5)
    body["checkpoints"].append({**body["checkpoints"][0], "id": "00000000-0000-0000-0000-000000000055", "maxUnits": 2, "serviceRatePerMinute": 0.5})
    request = EnergyRequest.model_validate(body)
    brute = []
    for assignment in product(range(5), range(3)):
        if energy(request, assignment, "optimized")["withinBudget"]:
            brute.append(simulate_scenario(request, assignment, "optimized"))
    result = solve(body)
    assert result["configurationCount"] == 15
    assert result["feasibleCount"] == result["evaluatedCount"] == len(brute)
    assert result["best"] == min(brute, key=objective)
    assert result["candidates"] == sorted(brute, key=objective)
    assert len({p["populationFingerprint"] for p in result["candidates"]}) == 1
    assert all(point["energy"]["withinBudget"] is True for point in result["candidates"])


def test_budget_boundary_is_inclusive_and_decimal_exact_before_simulation(monkeypatch):
    body = payload(checkpointPowerKw=0.1, energyBudgetKwh=0.3, loads=[])
    body["checkpoints"][0]["maxUnits"] = 4
    calls = []
    original = optimizer.simulate_scenario

    def recording(request, assignment, variant, cache):
        calls.append(assignment)
        return original(request, assignment, variant, cache)

    monkeypatch.setattr(optimizer, "simulate_scenario", recording)
    result = solve(body)
    assert result["feasibleCount"] == 4
    assert (3,) in calls and (4,) not in calls
    assert any(p["energy"]["totalKwh"] == 0.3 for p in result["candidates"])
    body["energyBudgetKwh"] = 0.29999999999999993
    assert solve(body)["feasibleCount"] == 3


def test_infeasible_budget_runs_no_traffic(monkeypatch):
    def forbidden(*args, **kwargs):
        raise AssertionError("Over-budget traffic must never be simulated.")

    monkeypatch.setattr(optimizer, "simulate_scenario", forbidden)
    result = solve(payload(energyBudgetKwh=0))
    assert result["status"] == "infeasible"
    assert result["best"] is None and result["candidates"] == []
    assert result["feasibleCount"] == result["evaluatedCount"] == 0
    assert result["effectiveCoefficients"] == engine.coefficients(EnergyRequest.model_validate(payload(energyBudgetKwh=0)))
    assert result["assumptions"] == engine.ASSUMPTIONS
    assert result["formulas"] == engine.FORMULAS


def test_recurring_per_item_fractions_do_not_reject_an_exact_budget_boundary():
    body = payload(checkpointPowerKw=0, energyBudgetKwh=0.2,
                   loads=[{"id": f"load{i}", "kind": "lighting", "units": 1, "powerKw": 0.1,
                           "baselineActiveMinutes": 40, "candidateActiveMinutes": 40} for i in range(3)])
    result = run(body)
    assert result["candidate"]["energy"]["totalKwh"] == 0.2
    assert result["candidate"]["energy"]["withinBudget"] is True
    assert solve(body)["feasibleCount"] == 5


def test_bounded_minimum_rates_remain_finite_and_underflow_rates_are_rejected():
    body = payload(arrivalRatePerMinute=0.000001)
    body["checkpoints"][0]["serviceRatePerMinute"] = 0.000001
    result = run(body)
    assert result["candidate"]["traffic"]["processedCount"] == 0
    body = payload()
    body["checkpoints"][0]["serviceRatePerMinute"] = 5e-324
    with pytest.raises(ValidationError):
        EnergyRequest.model_validate(body)
    with pytest.raises(ValidationError):
        EnergyRequest.model_validate(payload(arrivalRatePerMinute=5e-324))


def test_optimizer_ties_prefer_energy_then_assignment_with_no_completions():
    body = payload(passengerCount=0, arrivalRatePerMinute=0, loads=[], checkpointPowerKw=0)
    result = solve(body)
    assert result["best"]["assignment"][0]["activeUnits"] == 0
    assert result["best"]["traffic"]["meanWaitMinutes"] is None
    assert result["status"] == "optimal"


def test_grid_limit_only_restricts_optimizer_and_compare_remains_available():
    body = payload()
    body["checkpoints"] = [{**body["checkpoints"][0], "id": f"00000000-0000-0000-0000-{index:012d}", "maxUnits": 8} for index in range(1, 4)]
    request = EnergyRequest.model_validate(body)
    with pytest.raises(ConfigurationLimitError, match="729"):
        optimize(request)
    assert run(body)["synthetic"] is True


def test_expensive_bounded_grid_is_rejected_before_any_simulation(monkeypatch):
    def forbidden(*args, **kwargs):
        raise AssertionError("Work guard must reject before any traffic simulation.")

    monkeypatch.setattr(optimizer, "simulate_scenario", forbidden)
    body = payload(passengerCount=2000, loads=[], energyBudgetKwh=None)
    body["checkpoints"] = [{**body["checkpoints"][0], "id": f"00000000-0000-0000-0000-{index:012d}", "maxUnits": 3} for index in range(1, 4)]
    with pytest.raises(ConfigurationLimitError, match="modeled work 139712, exceeding limit 20000"):
        optimize(EnergyRequest.model_validate(body))


def test_work_guard_filters_budget_before_counting_feasible_configurations():
    body = payload(passengerCount=2000, loads=[], energyBudgetKwh=1.5)
    body["checkpoints"] = [{**body["checkpoints"][0], "id": f"00000000-0000-0000-0000-{index:012d}", "maxUnits": 3} for index in range(1, 4)]
    result = solve(body)
    assert result["configurationCount"] == 64
    assert result["feasibleCount"] == result["evaluatedCount"] == 4
    assert result["status"] == "optimal"


@pytest.mark.parametrize("changes", [
    {"synthetic": False}, {"synthetic": 1}, {"synthetic": "true"},
    {"seed": True}, {"seed": -1}, {"seed": 2147483648},
    {"durationMinutes": 0}, {"durationMinutes": 181}, {"durationMinutes": "60"},
    {"passengerCount": -1}, {"passengerCount": 2001}, {"passengerCount": 2.0},
    {"arrivalRatePerMinute": 0}, {"arrivalRatePerMinute": -1}, {"arrivalRatePerMinute": float("inf")},
    {"checkpointPowerKw": -0.01}, {"checkpointPowerKw": float("nan")}, {"checkpointPowerKw": 101},
    {"tariffRonPerKwh": -1}, {"tariffRonPerKwh": "1.2"}, {"energyBudgetKwh": -0.1},
    {"sensitivityPercent": 0}, {"sensitivityPercent": 51}, {"checkpoints": []}, {"unknown": 1},
])
def test_strict_top_level_validation(changes):
    with pytest.raises(ValidationError):
        EnergyRequest.model_validate(payload(**changes))


@pytest.mark.parametrize("changes", [
    {"id": "not-a-uuid"}, {"id": "00000000-0000-0000-0000-000000000000"},
    {"label": ""}, {"maxUnits": 9}, {"minUnits": 3}, {"baselineUnits": 1.5},
    {"candidateUnits": True}, {"serviceRatePerMinute": 0}, {"unknown": "x"},
])
def test_strict_checkpoint_validation(changes):
    body = payload()
    body["checkpoints"][0].update(changes)
    with pytest.raises(ValidationError):
        EnergyRequest.model_validate(body)


@pytest.mark.parametrize("changes", [
    {"id": "checkpointPowerKw"}, {"id": "tariffRonPerKwh"}, {"id": "lights with spaces"},
    {"kind": "meter"}, {"units": 1001}, {"units": True}, {"powerKw": -1},
    {"powerKw": float("nan")}, {"powerKw": 101}, {"candidateActiveMinutes": 61},
    {"baselineActiveMinutes": -1}, {"unknown": "x"},
])
def test_strict_load_validation(changes):
    body = payload()
    body["loads"][0].update(changes)
    with pytest.raises(ValidationError):
        EnergyRequest.model_validate(body)


def test_duplicate_and_excess_resource_validation():
    for category, limit in (("checkpoints", 6), ("loads", 16)):
        body = payload()
        body[category].append(copy.deepcopy(body[category][0]))
        with pytest.raises(ValidationError, match="unique"):
            EnergyRequest.model_validate(body)
        body[category] = [copy.deepcopy(body[category][0]) for _ in range(limit + 1)]
        with pytest.raises(ValidationError):
            EnergyRequest.model_validate(body)


def test_optional_routes_disabled_and_enabled_with_validation_and_grid_error(monkeypatch):
    app = FastAPI()
    app.include_router(router)
    client = TestClient(app)
    monkeypatch.delenv("ENERGY_ANALYTICS_ENABLED", raising=False)
    assert client.post("/energy/v1/compare", json=payload()).status_code == 404
    assert client.post("/energy/v1/optimize", json=payload()).status_code == 404
    monkeypatch.setenv("ENERGY_ANALYTICS_ENABLED", "true")
    assert client.post("/energy/v1/compare", json=payload()).status_code == 200
    assert client.post("/energy/v1/optimize", json=payload()).status_code == 200
    assert client.post("/energy/v1/compare", json=payload(synthetic=False)).status_code == 422
    body = payload()
    body["checkpoints"] = [{**body["checkpoints"][0], "id": f"00000000-0000-0000-0000-{index:012d}", "maxUnits": 8} for index in range(1, 4)]
    response = client.post("/energy/v1/optimize", json=body)
    assert response.status_code == 422
    assert response.json()["detail"]["code"] == "energy_configuration_limit"
    assert response.json()["errors"][0]["field"] == "optimization"
    assert "729" in response.json()["errors"][0]["message"]
    body["passengerCount"] = 2000
    body["energyBudgetKwh"] = None
    for checkpoint in body["checkpoints"]:
        checkpoint["maxUnits"] = 3
    response = client.post("/energy/v1/optimize", json=body)
    assert response.status_code == 422
    assert "modeled work" in response.json()["errors"][0]["message"]


def test_canonical_schema_and_examples():
    root = Path(__file__).resolve().parents[2] / "contracts/energy/v1"
    schema = json.loads((root / "contract.schema.json").read_text())
    Draft202012Validator.check_schema(schema)
    for name, definition in (("request", "EnergyRequest"), ("comparison", "EnergyComparison"), ("optimize-result", "OptimizeResult")):
        value = json.loads((root / "examples" / f"{name}.json").read_text())
        Draft202012Validator({**schema, "$ref": f"#/$defs/{definition}"}).validate(value)
        if name == "request":
            EnergyRequest.model_validate(value)

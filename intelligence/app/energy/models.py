from typing import Annotated, Literal
from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field, StringConstraints, field_validator, model_validator


Id = Annotated[str, StringConstraints(pattern=r"^[A-Za-z0-9][A-Za-z0-9_-]{0,39}$")]
CheckpointId = Annotated[str, StringConstraints(pattern=r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
Source = Literal["configured-assumption", "default-assumption"]
Kind = Literal["checkpoint", "lighting", "resource"]


class Input(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True, allow_inf_nan=False)


class Checkpoint(Input):
    id: CheckpointId
    label: str = Field(min_length=1, max_length=80)
    minUnits: int = Field(ge=0, le=8)
    maxUnits: int = Field(ge=0, le=8)
    baselineUnits: int = Field(ge=0, le=8)
    candidateUnits: int = Field(ge=0, le=8)
    serviceRatePerMinute: float = Field(ge=0.000001, le=1000)

    @model_validator(mode="after")
    def valid_bounds(self):
        if UUID(self.id).int == 0:
            raise ValueError("Checkpoint ID must be a nonempty canonical UUID.")
        if not self.minUnits <= self.baselineUnits <= self.maxUnits or not self.minUnits <= self.candidateUnits <= self.maxUnits:
            raise ValueError("Baseline and candidate units must lie in the declared min/max grid.")
        return self


class Load(Input):
    id: Id
    kind: Literal["lighting", "resource"]
    units: int = Field(ge=0, le=1000)
    powerKw: float | None = Field(default=None, ge=0, le=100)
    baselineActiveMinutes: float = Field(ge=0, le=180)
    candidateActiveMinutes: float = Field(ge=0, le=180)


class EnergyRequest(Input):
    contractVersion: Literal["1.0"] = "1.0"
    synthetic: Literal[True]
    seed: int = Field(ge=0, le=2147483647)
    durationMinutes: int = Field(ge=1, le=180)
    passengerCount: int = Field(ge=0, le=2000)
    arrivalRatePerMinute: float = Field(ge=0, le=1000, json_schema_extra={"anyOf": [{"const": 0}, {"type": "number", "minimum": 0.000001}]})
    checkpointPowerKw: float | None = Field(default=None, ge=0, le=100)
    tariffRonPerKwh: float | None = Field(default=None, ge=0, le=10000)
    energyBudgetKwh: float | None = Field(default=None, ge=0, le=1000000)
    sensitivityPercent: float = Field(ge=1, le=50, default=20)
    checkpoints: list[Checkpoint] = Field(min_length=1, max_length=6)
    loads: list[Load] = Field(max_length=16)

    @field_validator("synthetic", mode="before")
    @classmethod
    def synthetic_only(cls, value):
        if value is not True:
            raise ValueError("Only explicitly synthetic inputs are supported.")
        return value

    @model_validator(mode="after")
    def consistency(self):
        if self.passengerCount and self.arrivalRatePerMinute <= 0:
            raise ValueError("Nonzero synthetic demand requires a positive arrival rate.")
        if 0 < self.arrivalRatePerMinute < 0.000001:
            raise ValueError("A positive arrival rate must be at least 0.000001 per minute.")
        ids = [item.id for item in [*self.checkpoints, *self.loads]]
        if len(set(ids)) != len(ids):
            raise ValueError("Checkpoint and load IDs must be unique.")
        if any(item.id in ("checkpointPowerKw", "tariffRonPerKwh") for item in self.loads):
            raise ValueError("Load ID is reserved for a shared coefficient.")
        if any(max(item.baselineActiveMinutes, item.candidateActiveMinutes) > self.durationMinutes for item in self.loads):
            raise ValueError("Load active durations must not exceed the simulation horizon.")
        return self


class Coefficient(Input):
    id: str
    kind: Kind
    powerKw: float = Field(ge=0)
    source: Source


class EnergyItem(Coefficient):
    units: int = Field(ge=0)
    activeMinutes: float = Field(ge=0)
    kwh: float = Field(ge=0)


class Energy(Input):
    totalKwh: float = Field(ge=0)
    costRon: float | None
    withinBudget: bool | None
    items: list[EnergyItem]


class Traffic(Input):
    populationCount: int = Field(ge=0)
    arrivedCount: int = Field(ge=0)
    notArrivedCount: int = Field(ge=0)
    processedCount: int = Field(ge=0)
    unprocessedCount: int = Field(ge=0)
    queuedCount: int = Field(ge=0)
    processingCount: int = Field(ge=0)
    throughputPerMinute: float = Field(ge=0)
    meanWaitMinutes: float | None
    p95WaitMinutes: float | None


class Assignment(Input):
    checkpointId: str
    activeUnits: int = Field(ge=0, le=8)


class Snapshot(Input):
    minute: int = Field(ge=0, le=180)
    queuedCount: int = Field(ge=0)
    processedCount: int = Field(ge=0)
    energyKwhCumulative: float = Field(ge=0)


class EnergyScenario(Input):
    variant: Literal["baseline", "candidate", "optimized"]
    assignment: list[Assignment]
    energy: Energy
    traffic: Traffic
    populationFingerprint: str
    simulationRunId: str
    simulationEngine: Literal["simpy-kpi-1.0"]
    simulationLibraryVersion: str
    snapshots: list[Snapshot]


class Sensitivity(Input):
    coefficientId: str
    parameter: Literal["powerKw", "tariffRonPerKwh"]
    lowerValue: float
    baseValue: float
    upperValue: float
    lowerTotalKwh: float
    baseTotalKwh: float
    upperTotalKwh: float
    lowerCostRon: float | None
    baseCostRon: float | None
    upperCostRon: float | None


class Formulas(Input):
    energy: str
    cost: str
    sensitivity: str


class EnergyComparison(Input):
    contractVersion: Literal["1.0"] = "1.0"
    engine: Literal["energy-analytics-1.0"] = "energy-analytics-1.0"
    synthetic: Literal[True]
    request: EnergyRequest
    effectiveCoefficients: list[Coefficient]
    baseline: EnergyScenario
    candidate: EnergyScenario
    sensitivity: list[Sensitivity]
    assumptions: list[str]
    formulas: Formulas


class OptimizeResult(Input):
    contractVersion: Literal["1.0"] = "1.0"
    synthetic: Literal[True]
    status: Literal["optimal", "infeasible"]
    method: Literal["bounded-enumeration-simpy-1.0"] = "bounded-enumeration-simpy-1.0"
    request: EnergyRequest
    effectiveCoefficients: list[Coefficient]
    assumptions: list[str]
    formulas: Formulas
    configurationCount: int = Field(ge=1)
    feasibleCount: int = Field(ge=0)
    evaluatedCount: int = Field(ge=0)
    best: EnergyScenario | None
    candidates: list[EnergyScenario]
    diagnostics: list[str]

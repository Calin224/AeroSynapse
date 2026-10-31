from typing import Literal

from pydantic import BaseModel, ConfigDict, Field, field_validator, model_validator


class Input(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True, allow_inf_nan=False)


class Resource(Input):
    capacity: int = Field(ge=1, le=32)
    serviceMinutes: float = Field(ge=0.01, le=30)


class Resources(Input):
    checkIn: Resource
    beltA: Resource
    beltB: Resource
    sorting: Resource
    loading: Resource


class Incident(Input):
    belt: Literal["beltA", "beltB"]
    startMinute: float = Field(ge=0, le=240)
    durationMinutes: float = Field(ge=0.01, le=240)
    divert: bool = False


class BaggageRequest(Input):
    contractVersion: Literal["1.0"] = "1.0"
    synthetic: Literal[True]
    seed: int = Field(ge=0, le=2147483647)
    durationMinutes: int = Field(ge=1, le=240)
    unitCount: int = Field(ge=0, le=5000)
    arrivalWindowMinutes: float = Field(ge=0, le=240)
    loadingDeadlineMinute: float = Field(ge=0, le=240)
    serviceVariation: float = Field(ge=0, le=0.8, default=0.2)
    resources: Resources
    incident: Incident | None = None

    @field_validator("synthetic", mode="before")
    @classmethod
    def synthetic_only(cls, value):
        if value is not True:
            raise ValueError("Only explicitly synthetic inputs are supported.")
        return value

    @model_validator(mode="after")
    def validate_horizon(self):
        if self.arrivalWindowMinutes > self.durationMinutes:
            raise ValueError("Arrival window must fit the simulation horizon.")
        if self.loadingDeadlineMinute > self.durationMinutes:
            raise ValueError("Loading deadline must fit the simulation horizon.")
        if self.incident and self.incident.startMinute >= self.durationMinutes:
            raise ValueError("Incident must start before the simulation ends.")
        return self


ResourceId = Literal["checkIn", "beltA", "beltB", "sorting", "loading"]


class ResourceFrame(Input):
    resourceId: ResourceId
    capacity: int
    queued: int
    inService: int
    completed: int
    stopped: bool


class ResourceReport(ResourceFrame):
    serviceMinutes: float
    peakQueue: int
    peakBusy: int
    totalWaitingMinutes: float
    meanWaitingMinutes: float
    utilization: float


class Frame(Input):
    minute: int
    loadedCount: int
    resources: list[ResourceFrame]


class WorkSegment(Input):
    startMinute: float
    endMinute: float


class Stage(Input):
    resourceId: ResourceId
    enteredMinute: float
    startedMinute: float | None
    completedMinute: float | None
    serviceMinutes: float
    workSegments: list[WorkSegment]


class Unit(Input):
    id: str
    arrivalMinute: float
    nominalBelt: Literal["beltA", "beltB"]
    belt: Literal["beltA", "beltB"] | None
    diverted: bool
    loadedMinute: float | None
    stages: list[Stage]


class Kpis(Input):
    unitCount: int
    loadedCount: int
    backlogCount: int
    throughputPerMinute: float
    totalWaitingMinutes: float
    meanWaitingMinutes: float
    totalDelayMinutes: float
    meanDelayMinutes: float
    lateCount: int
    divertedCount: int


class BaggageResult(Input):
    contractVersion: Literal["1.0"]
    engine: Literal["simpy-baggage-1.0"]
    synthetic: Literal[True]
    populationFingerprint: str
    request: BaggageRequest
    kpis: Kpis
    resources: list[ResourceReport]
    snapshots: list[Frame]
    units: list[Unit]

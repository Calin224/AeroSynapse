from typing import Annotated, Literal

from pydantic import BaseModel, ConfigDict, Field, StringConstraints, field_validator, model_validator


Id = Annotated[str, StringConstraints(pattern=r"^[A-Za-z0-9][A-Za-z0-9_-]{0,39}$")]


class Input(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True, allow_inf_nan=False)


class Availability(Input):
    startMinute: int = Field(ge=0, le=240)
    endMinute: int = Field(ge=1, le=240)

    @model_validator(mode="after")
    def ordered(self):
        if self.startMinute >= self.endMinute:
            raise ValueError("Availability must have positive duration.")
        return self


class Stand(Input):
    id: Id
    available: list[Availability] = Field(max_length=12)

    @field_validator("available")
    @classmethod
    def disjoint_windows(cls, values):
        ordered = sorted(values, key=lambda window: window.startMinute)
        if any(a.endMinute >= b.startMinute for a, b in zip(ordered, ordered[1:])):
            raise ValueError("Availability windows must be disjoint and non-touching; merge adjacent windows.")
        return ordered


class Team(Stand):
    memberIds: list[Id] = Field(min_length=1, max_length=20)
    skills: list[Literal["handling", "pushback"]] = Field(min_length=1, max_length=2)
    standIds: list[Id] = Field(min_length=1, max_length=12)

    @model_validator(mode="after")
    def unique_values(self):
        if any(len(values) != len(set(values)) for values in (self.memberIds, self.skills, self.standIds)):
            raise ValueError("Team member IDs, skills and stand IDs must be unique within a team.")
        return self


class Vehicle(Stand):
    kind: Literal["service", "tug"]
    standIds: list[Id] = Field(min_length=1, max_length=12)

    @field_validator("standIds")
    @classmethod
    def unique_stands(cls, values):
        if len(values) != len(set(values)):
            raise ValueError("Vehicle stand IDs must be unique.")
        return values


class Turnaround(Input):
    id: Id
    standId: Id
    arrivalMinute: int = Field(ge=0, le=239)
    scheduledPushbackMinute: int = Field(ge=0, le=240)
    handlingMinutes: int = Field(ge=1, le=120)
    pushbackMinutes: int = Field(ge=1, le=60)

    @model_validator(mode="after")
    def target_after_arrival(self):
        if self.scheduledPushbackMinute < self.arrivalMinute:
            raise ValueError("Pushback readiness target must be at or after arrival.")
        return self


class AirsideRequest(Input):
    contractVersion: Literal["1.0"] = "1.0"
    synthetic: Literal[True]
    seed: int = Field(ge=0, le=2147483647)
    durationMinutes: int = Field(ge=1, le=240)
    serviceVariation: float = Field(ge=0, le=0.5, default=0)
    stands: list[Stand] = Field(min_length=1, max_length=12)
    teams: list[Team] = Field(max_length=20)
    vehicles: list[Vehicle] = Field(max_length=30)
    turnarounds: list[Turnaround] = Field(max_length=30)
    timeLimitSeconds: int = Field(ge=1, le=10, default=3)

    @field_validator("synthetic", mode="before")
    @classmethod
    def synthetic_only(cls, value):
        if value is not True:
            raise ValueError("Only explicitly synthetic inputs are supported.")
        return value

    @model_validator(mode="after")
    def references(self):
        for values in (self.stands, self.teams, self.vehicles, self.turnarounds):
            if len(values) != len({value.id for value in values}):
                raise ValueError("IDs must be unique within each resource category.")
        stands = {stand.id for stand in self.stands}
        if any(flight.standId not in stands for flight in self.turnarounds):
            raise ValueError("Turnarounds must reference an existing stand.")
        if any(set(resource.standIds) - stands for resource in [*self.teams, *self.vehicles]):
            raise ValueError("Resources must reference existing stands.")
        if any(flight.arrivalMinute >= self.durationMinutes for flight in self.turnarounds):
            raise ValueError("Turnarounds must arrive before the simulation horizon.")
        return self


class Allocation(Input):
    resourceType: Literal["stand", "team", "member", "vehicle"]
    resourceId: Id
    turnaroundId: Id
    stage: Literal["stand", "handling", "pushback"]
    startMinute: int = Field(ge=0, le=240)
    endMinute: int = Field(ge=0, le=240)


class Conflict(Input):
    turnaroundId: Id
    stage: Literal["stand", "handling", "pushback"]
    minute: int = Field(ge=0, le=240)
    reason: Literal["stand_occupied_or_unavailable", "team_vehicle_or_member_unavailable", "no_complete_available_path"]
    resourceIds: list[Id]


class TurnaroundResult(Turnaround):
    handlingMinutes: int = Field(ge=1, le=180)
    pushbackMinutes: int = Field(ge=1, le=90)
    standEnteredMinute: int | None
    handlingStartedMinute: int | None
    handlingCompletedMinute: int | None
    pushbackReadyMinute: int | None
    departedMinute: int | None
    waitingMinutes: int = Field(ge=0)
    readinessDelayMinutes: int = Field(ge=0)
    status: Literal["pending", "waiting", "handling", "ready", "pushback", "departed"]
    handlingTeamId: Id | None
    handlingVehicleId: Id | None
    pushbackTeamId: Id | None
    tugId: Id | None


class Kpis(Input):
    turnaroundCount: int
    departedCount: int
    backlogCount: int
    readyCount: int
    meanTurnaroundMinutes: float
    meanWaitingMinutes: float
    meanReadinessDelayMinutes: float
    lateCount: int
    conflictCount: int


class Snapshot(Input):
    minute: int
    departedCount: int
    readyCount: int
    backlogCount: int


class AirsideResult(Input):
    contractVersion: Literal["1.0"] = "1.0"
    engine: Literal["simpy-airside-fifo-1.0", "simpy-airside-cp-sat-replay-1.0"]
    synthetic: Literal[True]
    populationFingerprint: str
    request: AirsideRequest
    kpis: Kpis
    turnarounds: list[TurnaroundResult]
    allocations: list[Allocation]
    conflicts: list[Conflict]
    snapshots: list[Snapshot]


class OptimizeResult(Input):
    status: Literal["optimal", "feasible", "infeasible", "timeout"]
    method: Literal["cp-sat-airside-1.0"] = "cp-sat-airside-1.0"
    objective: int | None
    result: AirsideResult | None
    diagnostics: list[str]

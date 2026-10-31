from datetime import datetime
from decimal import Decimal
from typing import ClassVar
from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field, model_validator
from pydantic.alias_generators import to_camel

from app.contracts.validation import validate


class WireModel(BaseModel):
    model_config = ConfigDict(alias_generator=to_camel, extra="forbid")
    definition: ClassVar[str]

    @model_validator(mode="before")
    @classmethod
    def validate_wire(cls, value):
        definition = cls.definition
        if isinstance(value, dict) and definition in ('SimulationRequest','ScenarioSnapshot') and value.get('engine')=='simpy-kpi-1.0':
            definition='Kpi'+definition
        elif isinstance(value, dict) and definition=='SimulationResponse' and 'journeyResult' in value:
            definition='JourneySimulationResponse'
        elif isinstance(value, dict) and definition=='SimulationResponse' and 'kpiResult' in value:
            definition='KpiSimulationResponse'
        elif isinstance(value, dict) and definition in ('SimulationRequest', 'ScenarioSnapshot') and 'engine' in value:
            definition = 'SimPy' + definition
        elif isinstance(value, dict) and definition == 'SimulationResponse' and 'processResult' in value:
            definition = 'SimPySimulationResponse'
        elif isinstance(value, dict) and definition in ('SimulationRequest', 'ScenarioSnapshot') and 'navigation' in value:
            definition = 'Routed' + definition
        elif isinstance(value, dict) and definition == 'SimulationResponse' and 'navigationResult' in value:
            definition = 'RoutedSimulationResponse'
        elif isinstance(value, dict) and definition in ('SimulationRequest', 'ScenarioSnapshot') and 'passengerGeneration' in value:
            definition = 'Profiled' + definition
        if (isinstance(value, dict) and definition == 'OptimizationRequest'
                and isinstance(value.get('baseline'), dict) and 'passengerGeneration' in value['baseline']):
            definition = 'ProfiledOptimizationRequest'
        errors = validate(definition, value)
        if errors:
            raise ValueError("; ".join(f"{item['field']}: {item['code']}" for item in errors))
        return value


    @model_validator(mode='after')
    def numeric_journey(self):
        # The strict JSON parser supplies Decimal values; normalize the schema-validated
        # journey document before SimPy/CP-SAT and JSON serialization consume it.
        def numbers(value):
            if isinstance(value, Decimal): return int(value) if value == value.to_integral_value() else float(value)
            if isinstance(value, dict): return {key: numbers(child) for key, child in value.items()}
            if isinstance(value, list): return [numbers(child) for child in value]
            return value
        if getattr(self, 'journey', None) is not None: self.journey = numbers(self.journey)
        return self

class CheckpointInput(WireModel):
    definition = "Checkpoint"
    checkpoint_id: UUID
    service_rate_per_minute: float
    staff_count: int


class StaffStep(WireModel):
    definition = "StaffStep"
    start_minute: int
    staff_count: int


class StaffSchedule(WireModel):
    """People on duty over time at one checkpoint (KPI engine only); its staffCount is then the most at once."""
    definition = "StaffSchedule"
    checkpoint_id: UUID
    steps: list[StaffStep]


class CheckInInput(WireModel):
    """Check-in desks before security (KPI engine only)."""
    definition = 'CheckInInput'
    zone_id: UUID
    staff_count: int
    service_rate_per_minute: float
    hold_baggage_share: float
    walking_minutes: float
    desk_steps: list[StaffStep] | None = Field(default=None, exclude_if=lambda value: value is None)


class ScheduledEvent(WireModel):
    """Operational event; fields beyond eventId/type/minute depend on the type."""
    definition = 'ScheduledEvent'
    event_id: UUID
    type: str
    minute: int
    checkpoint_id: UUID | None = Field(default=None, exclude_if=lambda value: value is None)
    flight_id: UUID | None = Field(default=None, exclude_if=lambda value: value is None)
    zone_id: UUID | None = Field(default=None, exclude_if=lambda value: value is None)
    passenger_count: int | None = Field(default=None, exclude_if=lambda value: value is None)
    window_minutes: int | None = Field(default=None, exclude_if=lambda value: value is None)
    staff_count: int | None = Field(default=None, exclude_if=lambda value: value is None)
    delay_minutes: int | None = Field(default=None, exclude_if=lambda value: value is None)

    target_checkpoint_id: UUID | None = Field(default=None, exclude_if=lambda value: value is None)

    def wire(self):
        return self.model_dump(mode='json', by_alias=True)


class SimulationRequest(WireModel):
    definition = "SimulationRequest"
    contract_version: str
    run_id: UUID
    scenario_id: UUID
    scenario_revision: int
    layout_version_id: UUID
    starts_at_utc: datetime
    time_zone: str
    duration_minutes: int
    seed: int
    passenger_count: int
    arrival_rate_per_minute: float
    checkpoints: list[CheckpointInput]
    passenger_generation: 'PassengerGenerationRequest | None' = Field(default=None, exclude_if=lambda value: value is None)
    navigation: 'NavigationInput | None' = Field(default=None, exclude_if=lambda value: value is None)
    engine: str | None = Field(default=None, exclude_if=lambda value: value is None)
    snapshot_interval_minutes: int | None = Field(default=None, exclude_if=lambda value: value is None)
    events: list[ScheduledEvent] | None = Field(default=None, exclude_if=lambda value: value is None)
    check_in: CheckInInput | None = Field(default=None, exclude_if=lambda value: value is None)
    staff_schedules: list[StaffSchedule] | None = Field(default=None, exclude_if=lambda value: value is None)
    journey: dict | None = Field(default=None, exclude_if=lambda value: value is None)


class OptimizationRequest(WireModel):
    definition = "OptimizationRequest"
    contract_version: str
    optimization_id: UUID
    objective: str
    baseline: SimulationRequest
    candidates: list[SimulationRequest]


class SimulationMetric(WireModel):
    definition = "Metric"
    zone_id: UUID
    recorded_at_utc: datetime
    mean_wait_minutes: float
    throughput_per_minute: float
    processed_passengers: int


class SimulationSnapshot(WireModel):
    definition = "Snapshot"
    zone_id: UUID
    elapsed_minutes: float
    queued_passengers: int
    processed_passengers: int


class ZoneSnapshot(SimulationSnapshot):
    definition = "ZoneSnapshot"
    contract_version: str


class ScenarioSnapshot(WireModel):
    definition = "ScenarioSnapshot"
    contract_version: str
    scenario_id: UUID
    scenario_revision: int
    layout_version_id: UUID
    captured_at_utc: datetime
    starts_at_utc: datetime
    time_zone: str
    duration_minutes: int
    seed: int
    passenger_count: int
    arrival_rate_per_minute: float
    checkpoints: list[CheckpointInput]
    passenger_generation: 'PassengerGenerationRequest | None' = Field(default=None, exclude_if=lambda value: value is None)
    navigation: 'NavigationInput | None' = Field(default=None, exclude_if=lambda value: value is None)
    engine: str | None = Field(default=None, exclude_if=lambda value: value is None)
    snapshot_interval_minutes: int | None = Field(default=None, exclude_if=lambda value: value is None)
    events: list[ScheduledEvent] | None = Field(default=None, exclude_if=lambda value: value is None)
    check_in: CheckInInput | None = Field(default=None, exclude_if=lambda value: value is None)
    staff_schedules: list[StaffSchedule] | None = Field(default=None, exclude_if=lambda value: value is None)
    journey: dict | None = Field(default=None, exclude_if=lambda value: value is None)

    def to_simulation_request(self, run_id: UUID) -> SimulationRequest:
        payload = self.model_dump(mode="json", by_alias=True, exclude={"captured_at_utc"})
        return SimulationRequest.model_validate(payload | {"runId": str(run_id)})


class RunFailure(WireModel):
    definition = "RunFailure"
    code: str
    message: str


class SimulationResult(WireModel):
    definition = "SimulationResult"
    contract_version: str
    run_id: UUID
    status: str
    metrics: list[SimulationMetric]
    snapshots: list[SimulationSnapshot]
    failure: RunFailure | None


class CandidateScore(WireModel):
    definition = "CandidateScore"
    run_id: UUID
    score: float
    rank: int


class OptimizationResult(WireModel):
    definition = "OptimizationResult"
    contract_version: str
    optimization_id: UUID
    status: str
    objective: str
    baseline_run_id: UUID
    baseline_score: float | None
    candidates: list[CandidateScore]
    recommended_run_id: UUID | None
    failure: RunFailure | None


class SimulationResponse(SimulationResult):
    definition = "SimulationResponse"
    simulated_minutes: float
    snapshots: list[ZoneSnapshot]
    navigation_result: dict | None = Field(default=None, exclude_if=lambda value: value is None)
    process_result: dict | None = Field(default=None, exclude_if=lambda value: value is None)
    kpi_result: dict | None = Field(default=None, exclude_if=lambda value: value is None)
    check_in_result: dict | None = Field(default=None, exclude_if=lambda value: value is None)
    journey_result: dict | None = Field(default=None, exclude_if=lambda value: value is None)


class OptimizationResponse(OptimizationResult):
    definition = "OptimizationResponse"


class FieldError(WireModel):
    definition = "FieldError"
    field: str
    code: str
    message: str


class ValidationErrorResponse(WireModel):
    definition = "ValidationError"
    contract_version: str
    code: str
    message: str
    errors: list[FieldError]


class PassengerDistribution(WireModel):
    definition = 'PassengerDistribution'
    kind: str
    minimum: float
    maximum: float
    mean: float
    std_dev: float
    mode: float


class PassengerGroup(WireModel):
    definition = 'PassengerGroup'
    kind: str
    weight: float
    min_size: int
    max_size: int
    service_multiplier: float
    travel_multiplier: float


class PassengerSurge(WireModel):
    definition = 'PassengerSurge'
    starts_minute: float
    ends_minute: float
    share: float


class PassengerBatch(WireModel):
    definition = 'PassengerBatch'
    flight_id: UUID
    checkpoint_id: UUID
    count_mode: str
    modeled_count: int
    arrival: PassengerDistribution
    service: PassengerDistribution
    travel: PassengerDistribution
    groups: list[PassengerGroup]
    surges: list[PassengerSurge]
    checkpoint_mode: str | None = Field(default=None, exclude_if=lambda value: value is None)


class PassengerSegment(WireModel):
    definition = 'PassengerSegment'
    kind: str
    share: float
    hold_baggage: bool
    control: str
    service_multiplier: float
    travel_multiplier: float
    transfer_minutes: PassengerDistribution | None = Field(default=None, exclude_if=lambda value: value is None)


class PassengerStage(WireModel):
    definition = 'PassengerStage'
    kind: str
    staff_count: int
    service: PassengerDistribution


class PassengerStageClosure(WireModel):
    definition = 'PassengerStageClosure'
    stage: str
    starts_minute: float
    ends_minute: float


class PassengerFlow(WireModel):
    definition = 'PassengerFlow'
    segments: list[PassengerSegment]
    stages: list[PassengerStage]
    closures: list[PassengerStageClosure]


class PassengerCheckpointRoute(WireModel):
    definition = 'PassengerCheckpointRoute'
    checkpoint_id: UUID
    route_id: UUID
    route_travel_minutes: float


class PassengerFlightInput(WireModel):
    definition = 'PassengerFlightInput'
    flight_id: UUID
    passenger_capacity: int
    boarding_close_minute: float
    is_cancelled: bool
    checkpoint_id: UUID
    route_id: UUID
    route_travel_minutes: float
    checkpoint_routes: list[PassengerCheckpointRoute] | None = Field(default=None, exclude_if=lambda value: value is None)

    def travel_minutes(self, route_id):
        """Layout travel time of the route a passenger was assigned; spread flights have several."""
        for route in self.checkpoint_routes or []:
            if str(route.route_id) == str(route_id):
                return route.route_travel_minutes
        return self.route_travel_minutes


class PassengerGenerationRequest(WireModel):
    definition = 'PassengerGenerationRequest'
    generator_version: str
    seed: int
    duration_minutes: int
    checkpoints: list[CheckpointInput]
    flights: list[PassengerFlightInput]
    batches: list[PassengerBatch]
    is_synthetic: bool
    flow: PassengerFlow | None = Field(default=None, exclude_if=lambda value: value is None)


class NavigationConfig(WireModel):
    definition = 'NavigationConfig'
    navigation_version: str
    graph: dict
    checkpoint_bindings: list[dict]
    gate_bindings: list[dict]
    access_profiles: list[dict]
    closed_zone_ids: list[UUID]
    closed_edge_ids: list[UUID]

    @model_validator(mode='after')
    def numeric_graph(self):
        def numbers(value):
            if isinstance(value, Decimal): return float(value)
            if isinstance(value, dict): return {key: numbers(child) for key, child in value.items()}
            if isinstance(value, list): return [numbers(child) for child in value]
            return value
        self.graph = numbers(self.graph)
        self.access_profiles = numbers(self.access_profiles)
        return self


class NavigationInput(NavigationConfig):
    definition = 'NavigationInput'
    flight_gates: list[dict]


SimulationRequest.model_rebuild()
ScenarioSnapshot.model_rebuild()

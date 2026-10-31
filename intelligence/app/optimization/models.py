from app.contracts.models import WireModel


class StaffingPool(WireModel):
    definition = 'StaffingPool'
    terminal_id: str
    available_staff: int


class StaffingCheckpoint(WireModel):
    definition = 'StaffingCheckpoint'
    checkpoint_id: str
    terminal_id: str
    minimum_staff: int
    maximum_staff: int
    physical_limit: int
    required_open: bool
    closed: bool
    change_cost: int
    current_staff: int
    demand_milli: int
    capacity_milli: int


class StaffingSolveRequest(WireModel):
    definition = 'StaffingSolveRequest'
    contract_version: str
    scenario_id: str
    scenario_revision: int
    available_staff: int
    time_limit_seconds: float
    staff_pools: list[StaffingPool]
    checkpoints: list[StaffingCheckpoint]


class StaffingCandidateSearchRequest(WireModel):
    definition = 'StaffingCandidateSearchRequest'
    constraints: StaffingSolveRequest
    candidate_count: int


class ResourceWindow(WireModel):
    definition = 'ResourceWindow'
    start_minute: float
    end_minute: float


class ResourceGate(WireModel):
    definition = 'ResourceGate'
    gate_id: str
    availability: list[ResourceWindow]


class ResourceWorker(WireModel):
    definition = 'ResourceWorker'
    staff_member_id: str
    skills: list[str]
    shifts: list[ResourceWindow]


class HandlingTeam(WireModel):
    definition = 'HandlingTeam'
    team_id: str
    name: str
    member_ids: list[str]
    gate_ids: list[str]


class ResourceRequirement(WireModel):
    definition = 'ResourceRequirement'
    flight_id: str
    compatible_gate_ids: list[str]
    required_skills: list[str]
    minimum_team_size: int


class IntegratedResourceConfig(WireModel):
    definition = 'IntegratedResourceConfig'
    contract_version: str
    gates: list[ResourceGate]
    workers: list[ResourceWorker]
    teams: list[HandlingTeam]
    requirements: list[ResourceRequirement]


class ResourceFlight(WireModel):
    definition = 'ResourceFlight'
    flight_id: str
    current_gate_id: str
    start_minute: float
    end_minute: float


class ResourceSolveRequest(WireModel):
    definition = 'ResourceSolveRequest'
    contract_version: str
    scenario_id: str
    scenario_revision: int
    duration_minutes: int
    time_limit_seconds: float
    resources: IntegratedResourceConfig
    flights: list[ResourceFlight]


class ObjectiveWeights(WireModel):
    definition = 'ObjectiveWeights'
    time: int
    cost: int
    risk: int


class ObjectiveNormalization(WireModel):
    definition = 'ObjectiveNormalization'
    time_minutes: float
    cost_points: float
    risk_fraction: float


class ParetoOptions(WireModel):
    definition = 'ParetoOptions'
    weights: ObjectiveWeights
    normalization: ObjectiveNormalization
    operational_budget: int
    staff_minute_cost: int
    max_mean_wait_minutes: float
    max_p95_wait_minutes: float
    max_boarding_risk: float


class ParetoSearchRequest(WireModel):
    definition = 'ParetoSearchRequest'
    constraints: StaffingSolveRequest
    candidate_count: int
    duration_minutes: int
    options: ParetoOptions


class ShiftRules(WireModel):
    definition = 'ShiftRules'
    min_shift_slots: int
    max_shift_slots: int
    break_after_slots: int
    transfer_minutes: int
    max_moves_per_hour: int
    queue_weight: int
    staff_slot_cost: int
    move_cost: int
    target_utilization_percent: int


class ShiftStaffMember(WireModel):
    definition = 'ShiftStaffMember'
    staff_member_id: str
    terminal_id: str


class ShiftCheckpoint(WireModel):
    definition = 'ShiftCheckpoint'
    checkpoint_id: str
    terminal_id: str
    minimum_staff: int
    maximum_staff: int
    physical_limit: int
    required_open: bool
    closed: bool
    capacity_milli_per_minute: int
    arrivals_per_slot: list[int]


class ShiftPlanRequest(WireModel):
    definition = 'ShiftPlanRequest'
    contract_version: str
    scenario_id: str
    scenario_revision: int
    slot_minutes: int
    slot_count: int
    time_limit_seconds: float
    rules: ShiftRules
    staff: list[ShiftStaffMember]
    checkpoints: list[ShiftCheckpoint]

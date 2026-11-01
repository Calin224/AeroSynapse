export interface SceneSummary {
  id: string;
  name: string;
  code: string;
}
export interface SceneDetails {
  scene: SceneSummary;
  gates: { id: string; code: string; terminalId: string }[];
  routes: { id: string; toGateId: string }[];
  staff: { id: string; checkpointId: string | null }[];
  checkpoints: { id: string }[];
}
export interface ScenarioSummary {
  id: string;
  name: string;
  code: string;
  description: string;
  sceneId: string;
  layoutVersionId: string;
  revision: number;
  isSynthetic?: boolean;
  createdAtUtc?: string;
  updatedAtUtc?: string;
  deletedAtUtc?: string | null;
}
export interface ScenarioInputs {
  journey?: import('./journey.models').JourneyConfig | null;
  startsAtUtc: string;
  durationMinutes: number;
  seed: number;
  timeZone: string;
  flights: ScenarioFlight[];
  populations: { id: string; name: string; count: number }[];
  flows: {
    id: string;
    populationId: string;
    flightId: string;
    routeId: string;
    passengerCount: number;
    startsAtUtc: string;
    endsAtUtc: string;
  }[];
  staffAllocations: { staffMemberId: string; checkpointId: string }[];
  /** When present, these shifts staff the day instead of the fixed allocations. */
  staffShifts?: {
    slotMinutes: number;
    transferMinutes: number;
    shifts: import('./staffing-evaluation.models').StaffShift[];
  } | null;
  events: import('./events.models').ScenarioEvent[];
  passengerGeneration?: PassengerGenerationConfig | null;
  navigation?: import('./navigation.models').NavigationConfig | null;
  snapshotIntervalMinutes?: number | null;
  /** Minimum gap between any two scheduled departures, including their configured delays. */
  minimumDepartureSpacingMinutes?: number | null;
  integratedResources?: import('./resource.models').IntegratedResourceConfig | null;
  /** Staffed desks of a layout check-in area; hold-baggage passengers queue there before security. */
  checkIn?: { areaId: string; staffCount: number; holdBaggageShare: number } | null;
  /** People the user entered; security ids are layout staff slots placed by staffAllocations. */
  operators?: ScenarioOperator[] | null;
}
export type OperatorRole = 'security' | 'checkIn';
export interface ScenarioOperator {
  id: string;
  name: string;
  role: OperatorRole;
}
export interface PassengerDistribution {
  kind: string;
  minimum: number;
  maximum: number;
  mean: number;
  stdDev: number;
  mode: number;
}
export interface PassengerBatchConfig {
  flightId: string;
  checkpointId: string;
  countMode: string;
  modeledCount: number;
  arrival: PassengerDistribution;
  service: PassengerDistribution;
  travel: PassengerDistribution;
  groups: {
    kind: string;
    weight: number;
    minSize: number;
    maxSize: number;
    serviceMultiplier: number;
    travelMultiplier: number;
  }[];
  surges: { startsMinute: number; endsMinute: number; share: number }[];
  /** 'spread': groups use every staffed checkpoint routed to the gate; checkpointId stays the flight's own. Absent means fixed. */
  checkpointMode?: 'fixed' | 'spread';
}
// The type is a statistics label; routes and durations come only from these explicit synthetic fields.
export interface PassengerSegmentProfile {
  kind: string;
  share: number;
  holdBaggage: boolean;
  control: string;
  serviceMultiplier: number;
  travelMultiplier: number;
  transferMinutes?: PassengerDistribution;
}
export interface PassengerFlowConfig {
  segments: PassengerSegmentProfile[];
  stages: { kind: string; staffCount: number; service: PassengerDistribution }[];
  closures: { stage: string; startsMinute: number; endsMinute: number }[];
}
export interface PassengerGenerationConfig {
  generatorVersion: string;
  batches: PassengerBatchConfig[];
  flow?: PassengerFlowConfig | null;
}
export interface PassengerFlowReport {
  generatorVersion: string;
  seed: number;
  isSynthetic: boolean;
  fingerprint: string;
  durationMinutes: number;
  modeledCount: number;
  stages: {
    stage: string;
    arrivedPassengers: number;
    processedPassengers: number;
    queuedPassengers: number;
    meanWaitMinutes: number;
    maxWaitMinutes: number;
    blockedMinutes: number;
  }[];
  segments: {
    segment: string;
    passengers: number;
    reachedGatePassengers: number;
    missedBoardingPassengers: number;
    meanJourneyMinutes: number;
    maxJourneyMinutes: number;
    meanQueueMinutes: number;
    meanClosureDelayMinutes: number;
  }[];
}
export interface PassengerSettings {
  scenarioId: string;
  revision: number;
  seed: number;
  config: PassengerGenerationConfig | null;
  defaults: PassengerGenerationConfig;
  checkpoints: { id: string; name: string; capacityPerMinute: number }[];
  flights: {
    id: string;
    code: string;
    capacity: number;
    isCancelled: boolean;
    boardingCloseMinute: number;
    checkpointIds: string[];
  }[];
}
export interface PassengerGenerationResult {
  generatorVersion: string;
  seed: number;
  isSynthetic: boolean;
  rng: string;
  numpyVersion: string;
  fingerprint: string;
  configuredCount: number;
  modeledCount: number;
  hasSubset: boolean;
  offset: number;
  returnedCount: number;
  hasMore: boolean;
  batches: {
    flightId: string;
    configuredCount: number;
    modeledCount: number;
    unmodeledCount: number;
    countMode: string;
    groupCount: number;
    surgePassengerCount: number;
    isSynthetic: boolean;
  }[];
  checkpointDemand: {
    checkpointId: string;
    modeledCount: number;
    peakArrivalsPerMinute: number;
    meanServiceMinutes: number;
    staffCount: number;
    estimatedCapacityPerMinute: number;
    overloaded: boolean;
  }[];
  passengers: {
    id: string;
    flightId: string;
    checkpointId: string;
    routeId: string;
    groupId: string;
    groupKind: string;
    arrivalMinute: number;
    serviceMinutes: number;
    travelMinutes: number;
    inSurge: boolean;
    isSynthetic: boolean;
    segment?: string;
    transferMinutes?: number;
    stages?: { stage: string; serviceMinutes: number }[];
  }[];
}
export interface ScenarioFlight {
  id: string;
  code: string;
  gateId: string;
  departureAtUtc: string;
  passengerCapacity: number;
  boardingOpensAtUtc?: string | null;
  boardingClosesAtUtc?: string | null;
  delayMinutes?: number;
  status?: string;
  isSynthetic?: boolean;
}
export interface FlightSchedule {
  scenarioId: string;
  revision: number;
  startsAtUtc: string;
  durationMinutes: number;
  isSynthetic: boolean;
  flights: ScenarioFlight[];
  gates: { id: string; code: string; name: string; capacity: number; isSynthetic: boolean }[];
}
export interface ScenarioDetails {
  scenario: ScenarioSummary;
  inputs: ScenarioInputs;
}
export interface RunSummary {
  id: string;
  status: string;
  revision: number;
  seed: number;
  scenarioRevision: number;
  retryOfRunId: string | null;
  scenarioId?: string | null;
  finishedAtUtc?: string | null;
  resultsValid?: boolean;
}
export interface RunDetails extends RunSummary {
  provenance?: import('./comparison.models').RunProvenance | null;
  passengerMotion?: import('../digital-twin/passenger-model').PassengerMotion;
  inputSnapshotJson?: string | null;
  resultsValid: boolean;
  failureReason: string | null;
  metrics: {
    id: string;
    name: string;
    value: number;
    unit: string;
    zoneId: string;
    recordedAtUtc: string;
  }[];
  snapshots: { id: string; zoneId: string; recordedAtUtc: string; payloadJson: string }[];
  error: { code: string; message: string } | null;
}

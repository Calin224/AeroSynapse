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
}
export interface ScenarioInputs {
  startsAtUtc: string;
  durationMinutes: number;
  seed: number;
  timeZone: string;
  flights: {
    id: string;
    code: string;
    gateId: string;
    departureAtUtc: string;
    passengerCapacity: number;
  }[];
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
  events: { id: string; type: string; occurredAtUtc: string; payloadJson: string }[];
}
export interface ScenarioDetails {
  scenario: ScenarioSummary;
  inputs: ScenarioInputs;
}
export interface PublishedLayout {
  version: { id: string };
  layout: SceneDetails;
}
export interface RunSummary {
  id: string;
  status: string;
  revision: number;
  seed: number;
  scenarioRevision: number;
  retryOfRunId: string | null;
}
export interface RunDetails extends RunSummary {
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

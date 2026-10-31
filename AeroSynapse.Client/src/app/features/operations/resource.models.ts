import { ScenarioFlight } from './operations.models';

export interface ResourceWindow { startMinute: number; endMinute: number }
export interface IntegratedResourceConfig {
  contractVersion: '1.0';
  gates: { gateId: string; availability: ResourceWindow[] }[];
  workers: { staffMemberId: string; skills: string[]; shifts: ResourceWindow[] }[];
  teams: { teamId: string; name: string; memberIds: string[]; gateIds: string[] }[];
  requirements: { flightId: string; compatibleGateIds: string[]; requiredSkills: string[]; minimumTeamSize: number }[];
}
export interface ResourceSettings {
  scenarioId: string; revision: number; startsAtUtc: string; durationMinutes: number;
  compatible: boolean; incompatibilityReason: string | null; resources: IntegratedResourceConfig | null;
  flights: ScenarioFlight[];
  gates: { id: string; name: string; terminalId: string }[];
  staff: { id: string; name: string; terminalId: string; reservedForSecurity: boolean }[];
}
export interface ResourceResult {
  status: 'optimal' | 'feasible' | 'infeasible' | 'timeout' | 'invalid_model'; solverStatus: string; timedOut: boolean;
  score: number | null; bestBound: number | null; advisoryOnly: true; wallTimeSeconds: number;
  assignments: { flightId: string; gateId: string; teamId: string }[];
  rules: { code: string; flightId: string | null; resourceId: string | null; message: string }[];
  constraints: { scenarioId: string; scenarioRevision: number; resources: IntegratedResourceConfig;
    flights: { flightId: string; currentGateId: string; startMinute: number; endMinute: number }[] };
}

export interface AvailabilityWindow {
  startMinute: number;
  endMinute: number;
}
export type AirsideSkill = 'handling' | 'pushback';
export interface AirsideStand {
  id: string;
  available: AvailabilityWindow[];
}
export interface AirsideTeam extends AirsideStand {
  memberIds: string[];
  skills: AirsideSkill[];
  standIds: string[];
}
export interface AirsideVehicle extends AirsideStand {
  kind: 'service' | 'tug';
  standIds: string[];
}
export interface AirsideTurnaround {
  id: string;
  standId: string;
  arrivalMinute: number;
  scheduledPushbackMinute: number;
  handlingMinutes: number;
  pushbackMinutes: number;
}
export interface AirsideRequest {
  contractVersion: '1.0';
  synthetic: true;
  seed: number;
  durationMinutes: number;
  serviceVariation: number;
  stands: AirsideStand[];
  teams: AirsideTeam[];
  vehicles: AirsideVehicle[];
  turnarounds: AirsideTurnaround[];
  timeLimitSeconds: number;
}
export interface AirsideKpis {
  turnaroundCount: number;
  departedCount: number;
  backlogCount: number;
  readyCount: number;
  meanTurnaroundMinutes: number;
  meanWaitingMinutes: number;
  meanReadinessDelayMinutes: number;
  lateCount: number;
  conflictCount: number;
}
export interface AirsideTurnaroundResult extends AirsideTurnaround {
  standEnteredMinute: number | null;
  handlingStartedMinute: number | null;
  handlingCompletedMinute: number | null;
  pushbackReadyMinute: number | null;
  departedMinute: number | null;
  waitingMinutes: number;
  readinessDelayMinutes: number;
  status: 'pending' | 'waiting' | 'handling' | 'ready' | 'pushback' | 'departed';
  handlingTeamId: string | null;
  handlingVehicleId: string | null;
  pushbackTeamId: string | null;
  tugId: string | null;
}
export interface AirsideAllocation {
  resourceType: 'stand' | 'team' | 'member' | 'vehicle';
  resourceId: string;
  turnaroundId: string;
  stage: 'stand' | AirsideSkill;
  startMinute: number;
  endMinute: number;
}
export interface AirsideConflict {
  turnaroundId: string;
  stage: string;
  minute: number;
  reason: string;
  resourceIds: string[];
}
export interface AirsideResult {
  contractVersion: '1.0';
  engine: string;
  synthetic: true;
  populationFingerprint: string;
  request: AirsideRequest;
  kpis: AirsideKpis;
  turnarounds: AirsideTurnaroundResult[];
  allocations: AirsideAllocation[];
  conflicts: AirsideConflict[];
  snapshots: { minute: number; departedCount: number; readyCount: number; backlogCount: number }[];
}
export interface AirsideOptimization {
  status: 'optimal' | 'feasible' | 'infeasible' | 'timeout';
  method: 'cp-sat-airside-1.0';
  objective: number | null;
  result: AirsideResult | null;
  diagnostics: string[];
}
export function airsideDemoRequest(): AirsideRequest {
  const available = () => [{ startMinute: 0, endMinute: 90 }];
  const standIds = ['stand-1', 'stand-2'];
  return {
    contractVersion: '1.0',
    synthetic: true,
    seed: 53,
    durationMinutes: 90,
    serviceVariation: 0,
    timeLimitSeconds: 3,
    stands: standIds.map((id) => ({ id, available: available() })),
    teams: [
      {
        id: 'team-1',
        memberIds: ['member-1', 'member-2'],
        skills: ['handling'],
        standIds: [...standIds],
        available: available(),
      },
      {
        id: 'team-2',
        memberIds: ['member-3', 'member-4'],
        skills: ['handling'],
        standIds: [...standIds],
        available: [{ startMinute: 15, endMinute: 90 }],
      },
      {
        id: 'team-3',
        memberIds: ['member-5', 'member-6'],
        skills: ['pushback'],
        standIds: [...standIds],
        available: available(),
      },
    ],
    vehicles: [
      { id: 'vehicle-1', kind: 'service', standIds: [...standIds], available: available() },
      { id: 'vehicle-2', kind: 'service', standIds: [...standIds], available: available() },
      { id: 'vehicle-3', kind: 'tug', standIds: [...standIds], available: available() },
    ],
    turnarounds: [
      {
        id: 'turn-1',
        standId: 'stand-1',
        arrivalMinute: 0,
        scheduledPushbackMinute: 30,
        handlingMinutes: 20,
        pushbackMinutes: 5,
      },
      {
        id: 'turn-2',
        standId: 'stand-2',
        arrivalMinute: 0,
        scheduledPushbackMinute: 12,
        handlingMinutes: 5,
        pushbackMinutes: 5,
      },
      {
        id: 'turn-3',
        standId: 'stand-1',
        arrivalMinute: 30,
        scheduledPushbackMinute: 55,
        handlingMinutes: 20,
        pushbackMinutes: 5,
      },
      {
        id: 'turn-4',
        standId: 'stand-2',
        arrivalMinute: 35,
        scheduledPushbackMinute: 60,
        handlingMinutes: 20,
        pushbackMinutes: 5,
      },
    ],
  };
}
/** Validate the editable demo before sending; resource feasibility belongs to the simulation. */
export function validateAirside(request: AirsideRequest): string[] {
  const errors: string[] = [];
  const integer = (value: number, min: number, max: number) =>
    Number.isInteger(value) && value >= min && value <= max;
  if (!integer(request.seed, 0, 2147483647))
    errors.push('Seed: număr întreg între 0 și 2147483647.');
  if (!integer(request.durationMinutes, 1, 240)) errors.push('Orizont: 1–240 minute întregi.');
  if (
    !Number.isFinite(request.serviceVariation) ||
    request.serviceVariation < 0 ||
    request.serviceVariation > 0.5
  )
    errors.push('Variație serviciu: 0–0,5.');
  if (!integer(request.timeLimitSeconds, 1, 10))
    errors.push('Limită optimizer: 1–10 secunde întregi.');
  for (const [rows, max, label] of [
    [request.stands, 12, 'Standuri'],
    [request.teams, 20, 'Echipe'],
    [request.vehicles, 30, 'Vehicule'],
    [request.turnarounds, 30, 'Turnaround-uri'],
  ] as const) {
    if (rows.length < 1 || rows.length > max) errors.push(`${label}: 1–${max} înregistrări.`);
  }
  for (const resource of [...request.stands, ...request.teams, ...request.vehicles]) {
    if (
      resource.available.length !== 1 ||
      resource.available.some(
        (window) =>
          !integer(window.startMinute, 0, 240) ||
          !integer(window.endMinute, 1, 240) ||
          window.endMinute <= window.startMinute,
      )
    )
      errors.push(`${resource.id}: fereastra trebuie să aibă 0 ≤ început < sfârșit ≤ 240.`);
  }
  const stands = new Set(request.stands.map((stand) => stand.id));
  for (const team of request.teams) {
    if (!team.memberIds.length || !team.skills.length)
      errors.push(`${team.id}: selectează membri și cel puțin o competență.`);
  }
  for (const resource of [...request.teams, ...request.vehicles]) {
    if (!resource.standIds.length || resource.standIds.some((id) => !stands.has(id)))
      errors.push(`${resource.id}: selectează standuri compatibile.`);
  }
  for (const turn of request.turnarounds) {
    if (
      !stands.has(turn.standId) ||
      !integer(turn.arrivalMinute, 0, request.durationMinutes - 1) ||
      !integer(turn.scheduledPushbackMinute, turn.arrivalMinute, 240) ||
      !integer(turn.handlingMinutes, 1, 120) ||
      !integer(turn.pushbackMinutes, 1, 60)
    )
      errors.push(
        `${turn.id}: minute întregi, sosire înainte de orizont, sosire ≤ deadline ≤ 240, handling 1–120 și pushback 1–60.`,
      );
  }
  return errors;
}

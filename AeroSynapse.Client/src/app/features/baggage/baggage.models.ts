export const RESOURCE_IDS = ['checkIn', 'beltA', 'beltB', 'sorting', 'loading'] as const;
export type ResourceId = (typeof RESOURCE_IDS)[number];
export const RESOURCE_LABELS: Record<ResourceId, string> = {
  checkIn: 'Check-in',
  beltA: 'Bandă A',
  beltB: 'Bandă B',
  sorting: 'Sortare',
  loading: 'Încărcare',
};
export const POSITIONS: Record<ResourceId, [number, number]> = {
  checkIn: [-9, 0],
  beltA: [-3, -3],
  beltB: [-3, 3],
  sorting: [3, 0],
  loading: [9, 0],
};
export interface ResourceConfig {
  capacity: number;
  serviceMinutes: number;
}
export interface BaggageRequest {
  contractVersion: '1.0';
  synthetic: true;
  seed: number;
  durationMinutes: number;
  unitCount: number;
  arrivalWindowMinutes: number;
  loadingDeadlineMinute: number;
  serviceVariation: number;
  resources: Record<ResourceId, ResourceConfig>;
  incident: {
    belt: 'beltA' | 'beltB';
    startMinute: number;
    durationMinutes: number;
    divert: boolean;
  } | null;
}
export interface ResourceFrame {
  resourceId: ResourceId;
  capacity: number;
  queued: number;
  inService: number;
  completed: number;
  stopped: boolean;
}
export interface BaggageFrame {
  minute: number;
  loadedCount: number;
  resources: ResourceFrame[];
}
export interface BaggageStage {
  resourceId: ResourceId;
  enteredMinute: number;
  startedMinute: number | null;
  completedMinute: number | null;
  serviceMinutes: number;
  workSegments: { startMinute: number; endMinute: number }[];
}
export interface BaggageUnit {
  id: string;
  arrivalMinute: number;
  nominalBelt: 'beltA' | 'beltB';
  belt: 'beltA' | 'beltB' | null;
  diverted: boolean;
  loadedMinute: number | null;
  stages: BaggageStage[];
}
export interface BaggageKpis {
  unitCount: number;
  loadedCount: number;
  backlogCount: number;
  throughputPerMinute: number;
  totalWaitingMinutes: number;
  meanWaitingMinutes: number;
  totalDelayMinutes: number;
  meanDelayMinutes: number;
  lateCount: number;
  divertedCount: number;
}
export interface BaggageResult {
  contractVersion: '1.0';
  engine: 'simpy-baggage-1.0';
  synthetic: true;
  populationFingerprint: string;
  request: BaggageRequest;
  kpis: BaggageKpis;
  resources: (ResourceConfig &
    ResourceFrame & {
      peakQueue: number;
      peakBusy: number;
      totalWaitingMinutes: number;
      meanWaitingMinutes: number;
      utilization: number;
    })[];
  snapshots: BaggageFrame[];
  units: BaggageUnit[];
}
export function demoRequest(): BaggageRequest {
  return {
    contractVersion: '1.0',
    synthetic: true,
    seed: 52,
    durationMinutes: 60,
    unitCount: 240,
    arrivalWindowMinutes: 30,
    loadingDeadlineMinute: 45,
    serviceVariation: 0.2,
    resources: {
      checkIn: { capacity: 4, serviceMinutes: 0.3 },
      beltA: { capacity: 2, serviceMinutes: 0.4 },
      beltB: { capacity: 2, serviceMinutes: 0.4 },
      sorting: { capacity: 3, serviceMinutes: 0.3 },
      loading: { capacity: 4, serviceMinutes: 0.4 },
    },
    incident: { belt: 'beltA', startMinute: 10, durationMinutes: 20, divert: false },
  };
}
/** Fixed scale across scenarios: queue per simultaneous resource slot, with text values alongside. */
export function heatColor(frame: ResourceFrame): string {
  return frame.stopped
    ? '#9b4b62'
    : frame.queued >= frame.capacity * 3
      ? '#bc563b'
      : frame.queued >= frame.capacity
        ? '#b88729'
        : '#568168';
}
/** Service progress follows actual work segments, so a stopped belt freezes its bags. */
export function bagPosition(unit: BaggageUnit, minute: number): [number, number, number] | null {
  if (unit.arrivalMinute > minute || (unit.loadedMinute !== null && unit.loadedMinute <= minute))
    return null;
  const stage = unit.stages.find(
    (row) =>
      row.enteredMinute <= minute && (row.completedMinute === null || row.completedMinute > minute),
  );
  if (!stage) return null;
  const [x, z] = POSITIONS[stage.resourceId];
  const worked = stage.workSegments.reduce(
    (total, part) => total + Math.max(0, Math.min(minute, part.endMinute) - part.startMinute),
    0,
  );
  const progress = Math.min(1, worked / stage.serviceMinutes);
  return [x - 1.3 + progress * 2.6, 0.9, z];
}

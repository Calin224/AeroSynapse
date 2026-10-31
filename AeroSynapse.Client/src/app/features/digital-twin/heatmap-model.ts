import type { TwinSelection } from './twin-model';
import type { PassengerMotion } from './passenger-model';

export interface ZoneSnapshot {
  zoneId: string;
  elapsedMinutes: number;
  queuedPassengers: number;
  processedPassengers: number;
  /** Operators serving at this minute when the run recorded it; changes when staffing moves during the run. */
  staffOnDuty?: number;
}
export interface HeatmapCapacity {
  zoneId: string;
  label: string;
  serviceRatePerMinute: number;
  staffCount: number;
  /** Check-in desks before security; absent for a checkpoint. */
  zoneType?: 'checkIn';
}
export interface HeatmapRun {
  runId: string;
  scenarioId: string;
  layoutId: string;
  source: 'simulation-engine' | 'stored-run' | 'live-preview';
  status: string;
  capacities: HeatmapCapacity[];
  snapshots: ZoneSnapshot[];
  snapshotIntervalMinutes?: number;
  passengerMotion?: PassengerMotion;
  /** Modeled passengers per gate (flows of each flight at that gate), from the captured inputs. */
  gateDemand?: { gateId: string; passengers: number }[];
}
export interface HeatmapDataset {
  baseline: HeatmapRun;
  optimized?: HeatmapRun;
}
export type CongestionBand = 'low' | 'warning' | 'critical' | 'closed' | 'unavailable';
export const CONGESTION_BANDS: Record<
  CongestionBand,
  { label: string; color: string; range: string }
> = {
  low: { label: 'Redus', color: '#41d3a2', range: 'Coada < 1 minut de capacitate' },
  warning: { label: 'Ridicat', color: '#ffc45b', range: 'Coada ≥ 1 și < 3 minute de capacitate' },
  critical: {
    label: 'Critic',
    color: '#fa665c',
    range: 'Coada ≥ 3 minute sau capacitate zero cu pasageri',
  },
  closed: { label: 'Închis', color: '#9ba9b5', range: 'Capacitate zero, coadă zero' },
  unavailable: {
    label: 'Fără date',
    color: '#687787',
    range: 'Lipsește snapshotul sau capacitatea',
  },
};
export interface HeatmapZone extends ZoneSnapshot {
  runId: string;
  capacityPerMinute: number | null;
  staffCount: number | null;
  backlogMinutes: number | null;
  band: CongestionBand;
  label: string;
}
export interface HeatmapFrame {
  runId: string;
  layoutId: string;
  minute: number;
  variant: 'baseline' | 'optimized';
  source: HeatmapRun['source'];
  zones: HeatmapZone[];
  missingZones: {
    zoneId: string;
    label: string;
    capacityPerMinute: number | null;
    staffCount: number;
  }[];
  kpis: {
    queuedPassengers: number | null;
    processedPassengers: number | null;
    criticalZones: number | null;
    capacityPerMinute: number | null;
  };
}
export interface TwinHover {
  selection: TwinSelection;
  clientX: number;
  clientY: number;
}

export function comparable(dataset: HeatmapDataset): boolean {
  return (
    !!dataset.optimized &&
    dataset.baseline.layoutId === dataset.optimized.layoutId &&
    dataset.baseline.runId !== dataset.optimized.runId &&
    dataset.baseline.status === 'completed' &&
    dataset.optimized.status === 'completed'
  );
}
export function heatmapMinutes(dataset: HeatmapDataset): number[] {
  return [
    ...new Set(
      [dataset.baseline, ...(comparable(dataset) ? [dataset.optimized!] : [])].flatMap((run) =>
        run.snapshots.map((snapshot) => snapshot.elapsedMinutes),
      ),
    ),
  ]
    .filter((minute) => Number.isFinite(minute) && minute >= 0)
    .sort((a, b) => a - b);
}
export function congestion(queue: number, capacity: number | null): CongestionBand {
  if (capacity === null) return 'unavailable';
  if (capacity === 0) return queue > 0 ? 'critical' : 'closed';
  const backlog = queue / capacity;
  return backlog >= 3 ? 'critical' : backlog >= 1 ? 'warning' : 'low';
}
function effectiveCapacity(
  configured: HeatmapCapacity | undefined,
  staff = configured?.staffCount,
): number | null {
  if (
    !configured ||
    !Number.isFinite(configured.serviceRatePerMinute) ||
    configured.serviceRatePerMinute < 0 ||
    staff === undefined ||
    !Number.isInteger(staff) ||
    staff < 0
  )
    return null;
  const capacity = configured.serviceRatePerMinute * staff;
  return Number.isFinite(capacity) ? capacity : null;
}
export function buildHeatmapFrame(
  dataset: HeatmapDataset,
  variant: HeatmapFrame['variant'],
  minute: number,
): HeatmapFrame {
  const run =
    variant === 'optimized' && comparable(dataset) ? dataset.optimized! : dataset.baseline;
  const capacities = new Map(run.capacities.map((capacity) => [capacity.zoneId, capacity]));
  const selected = new Map<string, ZoneSnapshot>();
  for (const snapshot of run.snapshots) {
    if (snapshot.elapsedMinutes !== minute) continue;
    if (
      ![snapshot.elapsedMinutes, snapshot.queuedPassengers, snapshot.processedPassengers].every(
        (v) => Number.isFinite(v) && v >= 0,
      ) ||
      !Number.isInteger(snapshot.queuedPassengers) ||
      !Number.isInteger(snapshot.processedPassengers)
    ) {
      console.warn('ZoneSnapshot invalid', {
        runId: run.runId,
        minute,
        zoneId: snapshot.zoneId,
      });
      continue;
    }
    selected.set(snapshot.zoneId, snapshot);
  }
  const zones = [...selected.values()].map((snapshot) => {
    const configured = capacities.get(snapshot.zoneId);
    const staff = snapshot.staffOnDuty ?? configured?.staffCount;
    const capacity = effectiveCapacity(configured, staff);
    return {
      ...snapshot,
      runId: run.runId,
      capacityPerMinute: capacity,
      staffCount: staff ?? null,
      backlogMinutes:
        capacity !== null && capacity > 0 ? snapshot.queuedPassengers / capacity : null,
      band: congestion(snapshot.queuedPassengers, capacity),
      label: configured?.label ?? snapshot.zoneId,
    };
  });
  const missingZones = [...capacities.values()]
    .filter((capacity) => !selected.has(capacity.zoneId))
    .map((capacity) => ({
      zoneId: capacity.zoneId,
      label: capacity.label,
      capacityPerMinute: effectiveCapacity(capacity),
      staffCount: capacity.staffCount,
    }));
  const sum = (value: (zone: HeatmapZone) => number) =>
    zones.length ? zones.reduce((total, zone) => total + value(zone), 0) : null;
  return {
    runId: run.runId,
    layoutId: run.layoutId,
    minute,
    variant: run === dataset.baseline ? 'baseline' : 'optimized',
    source: run.source,
    zones,
    missingZones,
    kpis: {
      queuedPassengers: missingZones.length ? null : sum((z) => z.queuedPassengers),
      processedPassengers: missingZones.length ? null : sum((z) => z.processedPassengers),
      criticalZones:
        missingZones.length || zones.some((zone) => zone.capacityPerMinute === null)
          ? null
          : sum((z) => Number(z.band === 'critical')),
      capacityPerMinute:
        zones.length + missingZones.length &&
        [...zones, ...missingZones].every((z) => z.capacityPerMinute !== null)
          ? [...zones, ...missingZones].reduce((total, z) => total + z.capacityPerMinute!, 0)
          : null,
    },
  };
}

/** The single longest queue in the baseline run (earliest minute on ties), or null if none formed. */
export function busiestMoment(dataset: HeatmapDataset): HeatmapZone | null {
  let peak: HeatmapZone | null = null;
  for (const minute of heatmapMinutes(dataset))
    for (const zone of buildHeatmapFrame(dataset, 'baseline', minute).zones)
      if (zone.queuedPassengers > (peak?.queuedPassengers ?? 0)) peak = zone;
  return peak;
}

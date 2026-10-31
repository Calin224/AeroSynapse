import type { OperationsRun } from '../../core/http/operations-stream';
import { capturedHeatmapRun } from '../digital-twin/heatmap-adapters';
import type { HeatmapRun } from '../digital-twin/heatmap-model';
import type { TwinScene } from '../digital-twin/twin-model';
import { eventLabel, priority } from '../operations/events.models';
import type { RunDetails, ScenarioDetails, ScenarioInputs } from '../operations/operations.models';
import type { StudioLayout } from '../scenario-studio/studio.models';

export interface TowerContext {
  scenario: ScenarioDetails;
  layout: StudioLayout;
}
export interface TowerPoint {
  minute: number;
  queued: number;
  processed: number;
}
export interface TowerIncident {
  id: string;
  label: string;
  target: string;
  minute: number;
  detail: string;
  phase: 'programat' | 'moment atins';
}
export const activeRun = (status: string) => ['pending', 'planned', 'running'].includes(status);
export const runStatus = (status: string) =>
  ({
    pending: 'În așteptare',
    planned: 'Planificat',
    running: 'În execuție',
    completed: 'Finalizat',
    failed: 'Eșuat',
    cancelled: 'Anulat',
  })[status] ?? status;
const finite = (value: unknown): value is number =>
  typeof value === 'number' && Number.isFinite(value);
const count = (value: unknown): value is number =>
  finite(value) && Number.isSafeInteger(value) && value >= 0;

/** Historical results are always interpreted with their immutable inputs, never today's scenario. */
export function capturedContext(run: RunDetails, scenarioId: string): TowerContext {
  const value = JSON.parse(run.inputSnapshotJson ?? 'null') as TowerContext | null;
  const scenario = value?.scenario,
    layout = value?.layout;
  if (
    run.scenarioId !== scenarioId ||
    scenario?.scenario?.id !== scenarioId ||
    scenario.scenario.revision !== run.scenarioRevision ||
    scenario.inputs?.seed !== run.seed ||
    layout?.version?.id !== scenario.scenario.layoutVersionId ||
    layout?.layout?.scene?.id !== scenario.scenario.sceneId ||
    !Array.isArray(layout.layout.checkpoints) ||
    !Array.isArray(layout.layout.staff) ||
    !Array.isArray(layout.layout.gates) ||
    !Array.isArray(scenario.inputs?.staffAllocations) ||
    !Array.isArray(scenario.inputs.events) ||
    !Array.isArray(scenario.inputs.flights) ||
    !Array.isArray(scenario.inputs.populations) ||
    !finite(scenario.inputs.durationMinutes) ||
    scenario.inputs.durationMinutes <= 0 ||
    !Number.isFinite(Date.parse(scenario.inputs.startsAtUtc))
  )
    throw new Error('Captura rulării nu corespunde scenariului sau layout-ului selectat.');
  return { scenario, layout };
}

/** Checkpoint zones of a run; check-in desks are a separate stage with their own metrics. */
function checkpointIds(heatmap: HeatmapRun): string[] {
  return heatmap.capacities.filter((row) => row.zoneType !== 'checkIn').map((row) => row.zoneId);
}

/** Only complete observations at a common minute may be summed across checkpoints. */
export function towerPoints(heatmap: HeatmapRun | null): TowerPoint[] {
  if (!heatmap || ['failed', 'invalid'].includes(heatmap.status)) return [];
  const ids = new Set(checkpointIds(heatmap));
  if (!ids.size) return [];
  const frames = new Map<number, Map<string, { queued: number; processed: number }>>();
  for (const row of heatmap.snapshots) {
    if (
      !ids.has(row.zoneId) ||
      !finite(row.elapsedMinutes) ||
      row.elapsedMinutes < 0 ||
      !count(row.queuedPassengers) ||
      !count(row.processedPassengers)
    )
      continue;
    const zones = frames.get(row.elapsedMinutes) ?? new Map();
    zones.set(row.zoneId, { queued: row.queuedPassengers, processed: row.processedPassengers });
    frames.set(row.elapsedMinutes, zones);
  }
  return [...frames]
    .sort(([a], [b]) => a - b)
    .flatMap(([minute, zones]) =>
      zones.size === ids.size
        ? [
            {
              minute,
              queued: [...zones.values()].reduce((sum, row) => sum + row.queued, 0),
              processed: [...zones.values()].reduce((sum, row) => sum + row.processed, 0),
            },
          ]
        : [],
    );
}

export function towerMetrics(run: RunDetails | null, heatmap: HeatmapRun | null) {
  const points = towerPoints(heatmap),
    last = points.at(-1);
  const valid = run?.status === 'completed' && run.resultsValid;
  const metrics = new Map<string, number>();
  if (valid)
    for (const row of run.metrics) {
      if (finite(row.value) && row.value >= 0) metrics.set(`${row.zoneId}/${row.name}`, row.value);
    }
  const ids = heatmap ? checkpointIds(heatmap) : [];
  let samples = 0,
    totalWait = 0,
    maxP95: number | null = null;
  let waitComplete = ids.length > 0;
  let p95Complete = ids.length > 0;
  for (const id of ids) {
    const n = metrics.get(`${id}/waitSampleCount`),
      mean = metrics.get(`${id}/meanWaitMinutes`);
    if (!count(n) || (n > 0 && mean === undefined)) waitComplete = false;
    if (n !== undefined && n > 0 && mean !== undefined) {
      samples += n;
      totalWait += n * mean;
    }
    const p95 = metrics.get(`${id}/p95WaitMinutes`);
    if (!count(n) || (n > 0 && p95 === undefined)) p95Complete = false;
    if (n !== undefined && n > 0 && p95 !== undefined) maxP95 = Math.max(maxP95 ?? 0, p95);
  }
  return {
    queued: last?.queued ?? null,
    processed: last?.processed ?? null,
    minute: last?.minute ?? null,
    meanWait: waitComplete && samples > 0 ? totalWait / samples : null,
    maxP95: p95Complete ? maxP95 : null,
    points,
  };
}

export function simulationMinute(
  run: RunDetails | null,
  progress: OperationsRun | null,
  heatmap: HeatmapRun | null,
): number | null {
  const minutes = heatmap?.snapshots.map((row) => row.elapsedMinutes).filter(finite) ?? [];
  if (progress?.runId === run?.id && finite(progress?.simulationMinute))
    minutes.push(progress.simulationMinute);
  return minutes.length ? Math.max(...minutes) : null;
}

export function virtualTime(context: TowerContext | null, minute: number | null): string | null {
  if (!context || minute === null) return null;
  const inputs = context.scenario.inputs;
  try {
    return new Intl.DateTimeFormat('ro-RO', {
      timeZone: inputs.timeZone,
      hour: '2-digit',
      minute: '2-digit',
      second: '2-digit',
      hourCycle: 'h23',
    }).format(new Date(Date.parse(inputs.startsAtUtc) + minute * 60000));
  } catch {
    return null;
  }
}

export function towerIncidents(
  context: TowerContext | null,
  minute: number | null,
): TowerIncident[] {
  if (!context) return [];
  const { inputs } = context.scenario,
    layout = context.layout.layout;
  return [...inputs.events]
    .sort(
      (a, b) =>
        a.occurredAtUtc.localeCompare(b.occurredAtUtc) ||
        priority(a.type) - priority(b.type) ||
        a.id.localeCompare(b.id),
    )
    .map((event) => {
      let payload: Record<string, unknown> = {};
      try {
        const value = JSON.parse(event.payloadJson);
        if (value && typeof value === 'object' && !Array.isArray(value)) payload = value;
      } catch {
        /* Keep the event visible without invented details. */
      }
      const at = (Date.parse(event.occurredAtUtc) - Date.parse(inputs.startsAtUtc)) / 60000;
      const target =
        layout.checkpoints.find((row) => row.id === event.checkpointId)?.code ??
        inputs.flights.find((row) => row.id === event.flightId)?.code ??
        layout.gates.find((row) => row.id === event.gateId)?.code ??
        (typeof payload['zoneId'] === 'string' ? payload['zoneId'] : 'Terminal');
      const surge = event.type === 'incident.passenger-surge';
      const detail = surge
        ? `${finite(payload['share']) ? Math.round(payload['share'] * 10000) / 100 + '%' : '—'} din populația zborului / ${payload['durationMinutes'] ?? '—'} min`
        : event.type === 'passenger_surge'
          ? `${payload['passengerCount'] ?? '—'} pasageri / ${payload['windowMinutes'] ?? '—'} min`
          : event.type === 'staff_reduction'
            ? `${payload['staffCount'] ?? '—'} operatori`
            : event.type === 'flight_change'
              ? `${payload['delayMinutes'] ?? '—'} min întârziere`
              : '';
      return {
        id: event.id,
        label: surge ? 'Vârf de sosire' : eventLabel(event.type),
        target,
        minute: at,
        detail,
        phase: minute !== null && at <= minute ? 'moment atins' : 'programat',
      };
    });
}

/** People staffing a checkpoint at a scenario minute: the shift in progress, else the fixed roster. */
export function staffAt(inputs: ScenarioInputs, checkpointId: string, minute: number | null) {
  const plan = inputs.staffShifts;
  if (!plan) return inputs.staffAllocations.filter((row) => row.checkpointId === checkpointId);
  const at = minute ?? 0;
  return plan.shifts
    .filter((shift) =>
      shift.segments.some(
        (segment) =>
          segment.checkpointId === checkpointId &&
          segment.startMinute <= at &&
          at < segment.endMinute,
      ),
    )
    .map((shift) => ({ staffMemberId: shift.staffMemberId, checkpointId }));
}

export function towerScene(
  context: TowerContext | null,
  heatmap: HeatmapRun | null,
  minute: number | null = null,
): TwinScene {
  if (!context) return { aircraft: [], checkpoints: [] };
  const { layout, scenario } = context;
  // Metrics are read from the heatmap; staffing is the captured roster, or the shift at `minute`.
  return {
    aircraft: [],
    scenarioId: scenario.scenario.id,
    gateCount: layout.layout.gates.length,
    checkpoints: layout.layout.checkpoints.map((cp) => {
      const rows = staffAt(scenario.inputs, cp.id, minute);
      return {
        zoneId: cp.id,
        code: cp.code,
        capacityPerMinute: cp.capacityPerMinute,
        staff: rows.flatMap((row) => {
          const person = layout.layout.staff.find((member) => member.id === row.staffMemberId);
          return person
            ? [{ id: person.id, name: person.displayName, role: 'Control securitate' }]
            : [];
        }),
        status: rows.length ? ('open' as const) : ('closed' as const),
        metrics: { queuedPassengers: null, processedPassengers: null, averageWaitMinutes: null },
      };
    }),
    heatmap: heatmap ? { baseline: heatmap } : undefined,
  };
}

export function runHeatmap(run: RunDetails): HeatmapRun | null {
  return capturedHeatmapRun(run);
}

import { OperationsRun } from '../../core/http/operations-stream';
import { RunDetails } from '../operations/operations.models';
import { HeatmapCapacity, HeatmapRun, ZoneSnapshot } from './heatmap-model';

function object(value: unknown): Record<string, unknown> | null {
  return value !== null && typeof value === 'object' && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : null;
}
function array(value: unknown): Record<string, unknown>[] {
  return Array.isArray(value)
    ? value.map(object).filter((entry): entry is Record<string, unknown> => entry !== null)
    : [];
}
function validSnapshot(
  zoneId: unknown,
  minute: unknown,
  queued: unknown,
  processed: unknown,
  staffOnDuty?: unknown,
): ZoneSnapshot | null {
  const staff =
    typeof staffOnDuty === 'number' && Number.isInteger(staffOnDuty) && staffOnDuty >= 0
      ? { staffOnDuty }
      : {};
  return typeof zoneId === 'string' &&
    typeof minute === 'number' &&
    Number.isFinite(minute) &&
    minute >= 0 &&
    typeof queued === 'number' &&
    Number.isInteger(queued) &&
    queued >= 0 &&
    typeof processed === 'number' &&
    Number.isInteger(processed) &&
    processed >= 0
    ? {
        zoneId,
        elapsedMinutes: minute,
        queuedPassengers: queued,
        processedPassengers: processed,
        ...staff,
      }
    : null;
}
/** Passengers each gate receives: the flows of every flight at that gate. */
function gateDemand(inputs: Record<string, unknown>): HeatmapRun['gateDemand'] {
  const gates = new Map(
    array(inputs['flights']).flatMap((flight) =>
      typeof flight['id'] === 'string' && typeof flight['gateId'] === 'string'
        ? [[flight['id'], flight['gateId']] as const]
        : [],
    ),
  );
  const demand = new Map<string, number>();
  for (const flow of array(inputs['flows'])) {
    const gate = gates.get(flow['flightId'] as string);
    const count = flow['passengerCount'];
    if (gate && typeof count === 'number' && Number.isFinite(count) && count > 0)
      demand.set(gate, (demand.get(gate) ?? 0) + count);
  }
  return [...demand].map(([gateId, passengers]) => ({ gateId, passengers }));
}
export function capturedHeatmapRun(run: RunDetails): HeatmapRun | null {
  try {
    const captured = object(JSON.parse(run.inputSnapshotJson ?? 'null'));
    const published = object(captured?.['layout']);
    const version = object(published?.['version']);
    const layout = object(published?.['layout']);
    const scenario = object(captured?.['scenario']);
    const summary = object(scenario?.['scenario']);
    const inputs = object(scenario?.['inputs']);
    if (typeof version?.['id'] !== 'string' || !layout || !inputs) return null;
    const allocations = array(inputs['staffAllocations']);
    const capacities: HeatmapCapacity[] = array(layout['checkpoints']).flatMap((checkpoint) => {
      if (
        typeof checkpoint['id'] !== 'string' ||
        typeof checkpoint['capacityPerMinute'] !== 'number'
      )
        return [];
      return [
        {
          zoneId: checkpoint['id'],
          label: typeof checkpoint['code'] === 'string' ? checkpoint['code'] : checkpoint['id'],
          serviceRatePerMinute: checkpoint['capacityPerMinute'],
          staffCount: allocations.filter((staff) => staff['checkpointId'] === checkpoint['id'])
            .length,
        },
      ];
    });
    const snapshots = run.resultsValid
      ? run.snapshots.flatMap((row) => {
          try {
            const payload = object(JSON.parse(row.payloadJson));
            if (
              !payload ||
              (payload['zoneId'] !== undefined && payload['zoneId'] !== row.zoneId) ||
              (payload['runId'] !== undefined && payload['runId'] !== run.id)
            )
              return [];
            // Check-in snapshots carry only the grid minute.
            const snapshot = validSnapshot(
              row.zoneId,
              payload['elapsedMinutes'] ?? payload['minute'],
              payload['queuedPassengers'],
              payload['processedPassengers'],
              // Servers on duty that minute (KPI snapshots); absent for older runs and check-in.
              payload['effectiveCapacity'],
            );
            return snapshot ? [snapshot] : [];
          } catch {
            return [];
          }
        })
      : [];
    // Check-in desks join the heatmap when the run reports them: older runs and live progress have none.
    const checkIn = object(inputs['checkIn']);
    const area = array(layout['checkInAreas']).find((row) => row['id'] === checkIn?.['areaId']);
    if (
      area &&
      typeof area['serviceSeconds'] === 'number' &&
      area['serviceSeconds'] > 0 &&
      typeof checkIn?.['staffCount'] === 'number' &&
      snapshots.some((snapshot) => snapshot.zoneId === area['id'])
    )
      capacities.push({
        zoneId: area['id'] as string,
        label: typeof area['code'] === 'string' ? area['code'] : (area['id'] as string),
        serviceRatePerMinute: 60 / area['serviceSeconds'],
        staffCount: checkIn['staffCount'],
        zoneType: 'checkIn',
      });
    return {
      runId: run.id,
      scenarioId: typeof summary?.['id'] === 'string' ? summary['id'] : '',
      layoutId: version['id'],
      source: 'stored-run',
      status: run.status === 'completed' && !run.resultsValid ? 'invalid' : run.status,
      capacities,
      snapshots,
      passengerMotion: run.resultsValid ? run.passengerMotion : undefined,
      gateDemand: gateDemand(inputs),
      snapshotIntervalMinutes:
        run.resultsValid &&
        captured?.['engine'] === 'simpy-kpi-1.0' &&
        !object(inputs['passengerGeneration'])?.['flow'] &&
        typeof captured['snapshotIntervalMinutes'] === 'number'
          ? captured['snapshotIntervalMinutes']
          : undefined,
    };
  } catch {
    return null;
  }
}
export function mergeHeatmapRuns(current: HeatmapRun | null, captured: HeatmapRun): HeatmapRun {
  // A completed KPI run carries the full persisted cadence, superseding temporary progress samples.
  if (captured.status === 'completed' && captured.snapshotIntervalMinutes !== undefined)
    return captured;
  if (
    !current ||
    current.runId !== captured.runId ||
    current.layoutId !== captured.layoutId ||
    captured.status === 'failed'
  )
    return captured;
  const rows = new Map(
    current.snapshots.map((snapshot) => [
      `${snapshot.zoneId}:${snapshot.elapsedMinutes}`,
      snapshot,
    ]),
  );
  for (const snapshot of captured.snapshots)
    rows.set(`${snapshot.zoneId}:${snapshot.elapsedMinutes}`, snapshot);
  return {
    ...captured,
    snapshots: [...rows.values()],
    passengerMotion: captured.passengerMotion ?? current.passengerMotion,
    source: current.source === 'live-preview' ? 'live-preview' : captured.source,
  };
}
export function appendProgress(current: HeatmapRun, progress: OperationsRun): HeatmapRun {
  if (
    current.runId !== progress.runId ||
    (current.status === 'completed' && current.snapshotIntervalMinutes !== undefined) ||
    current.status === 'failed' ||
    current.status === 'invalid' ||
    !progress.snapshot ||
    progress.simulationMinute === null ||
    progress.status === 'failed'
  )
    return current;
  const incoming = progress.snapshot.zones.flatMap((zone) => {
    const snapshot = validSnapshot(
      zone.zoneId,
      progress.simulationMinute,
      zone.queuedPassengers,
      zone.processedPassengers,
    );
    return snapshot ? [snapshot] : [];
  });
  if (
    !incoming.length ||
    incoming.every((snapshot) =>
      current.snapshots.some(
        (previous) =>
          previous.zoneId === snapshot.zoneId &&
          previous.elapsedMinutes === snapshot.elapsedMinutes &&
          previous.queuedPassengers === snapshot.queuedPassengers &&
          previous.processedPassengers === snapshot.processedPassengers,
      ),
    )
  )
    return current;
  const rows = new Map(
    current.snapshots.map((snapshot) => [
      `${snapshot.zoneId}:${snapshot.elapsedMinutes}`,
      snapshot,
    ]),
  );
  for (const snapshot of incoming)
    rows.set(`${snapshot.zoneId}:${snapshot.elapsedMinutes}`, snapshot);
  const minutes = [...new Set([...rows.values()].map((snapshot) => snapshot.elapsedMinutes))]
    .sort((a, b) => a - b)
    .slice(-60);
  return {
    ...current,
    source: 'live-preview',
    snapshots: [...rows.values()].filter((snapshot) => minutes.includes(snapshot.elapsedMinutes)),
  };
}

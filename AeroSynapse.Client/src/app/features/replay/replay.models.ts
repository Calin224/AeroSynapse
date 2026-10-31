import { KpiFrame } from '../operations/kpi.models';
import { ScenarioDetails } from '../operations/operations.models';
import { RunProvenance } from '../operations/comparison.models';
import { StudioLayout } from '../scenario-studio/studio.models';
import { HeatmapDataset } from '../digital-twin/heatmap-model';
import { PassengerReplay, preparePassengerReplay } from '../digital-twin/passenger-model';

export interface ReplayBookmark {
  id: string;
  runId: string;
  minute: number;
  label: string;
  zoneId: string | null;
  note: string;
  revision: number;
}
export interface ReplayEnvelope {
  contractVersion: '1.0';
  runId: string;
  contentHash: string;
  payloadJson: string;
  bookmarks: ReplayBookmark[];
}
export interface ReplaySample {
  zoneId: string;
  minute: number;
  recordedAtUtc: string;
  queuedPassengers: number;
  processedPassengers: number;
  kpi:
    | (KpiFrame & {
        kpiVersion: string;
        zoneType: string;
        elapsedMinutes: number;
        recordedAtUtc: string;
      })
    | null;
  /** Check-in desk snapshot; present exactly for samples of a captured check-in area. */
  checkIn?: {
    zoneId: string;
    zoneType: 'checkIn';
    minute: number;
    queuedPassengers: number;
    processedPassengers: number;
    maximumQueue: number;
    meanWaitMinutes: number | null;
    p95WaitMinutes: number | null;
    capacity: number;
    utilization: number | null;
  };
}
export interface ReplayData {
  run: {
    id: string;
    sceneId: string;
    scenarioId: string;
    scenarioRevision: number;
    seed: number;
    status: string;
    resultsValid: boolean;
    isSynthetic: boolean;
    provenance: RunProvenance;
  };
  captured: {
    schemaVersion: number;
    capturedAtUtc: string;
    scenario: ScenarioDetails;
    layout: StudioLayout;
    engine?: string;
    snapshotIntervalMinutes?: number;
  };
  samples: ReplaySample[];
  intervalMinutes: number | null;
}
export interface PreparedReplay {
  envelope: ReplayEnvelope;
  data: ReplayData;
  heatmap: HeatmapDataset;
  passengers: PassengerReplay;
  byMinute: Map<number, Map<string, ReplaySample>>;
  minutes: number[];
}
export const MAX_REPLAY_BYTES = 100 * 1024 * 1024;
const uuid = (value: unknown): value is string =>
  typeof value === 'string' &&
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value) &&
  !/^0{8}-0{4}-0{4}-0{4}-0{12}$/.test(value);
const count = (value: unknown) =>
  typeof value === 'number' && Number.isInteger(value) && value >= 0 && value <= 2147483647;
const utc = (value: unknown): value is string =>
  typeof value === 'string' && value.endsWith('Z') && Number.isFinite(Date.parse(value));
function invalid(): never {
  throw new Error('Pachetul de replay este invalid, incomplet sau nu corespunde runId-ului.');
}
export function validBookmark(value: ReplayBookmark, data: ReplayData): boolean {
  return (
    !!value &&
    uuid(value.id) &&
    value.runId === data.run.id &&
    Number.isFinite(value.minute) &&
    value.minute >= 0 &&
    value.minute <= data.captured.scenario.inputs.durationMinutes &&
    typeof value.label === 'string' &&
    !!value.label.trim() &&
    value.label.length <= 120 &&
    !value.label.includes('\0') &&
    typeof value.note === 'string' &&
    value.note.length <= 500 &&
    !value.note.includes('\0') &&
    count(value.revision) &&
    (value.zoneId === null ||
      data.captured.layout.layout.checkpoints.some((c) => c.id === value.zoneId) ||
      checkInDesks(data)?.id === value.zoneId)
  );
}
export async function replayHash(json: string): Promise<string> {
  if (!globalThis.crypto?.subtle)
    throw new Error('Verificarea replay-ului necesită o conexiune securizată sau localhost.');
  const hash = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(json));
  return [...new Uint8Array(hash)].map((value) => value.toString(16).padStart(2, '0')).join('');
}
export async function prepareReplay(
  raw: ReplayEnvelope,
  expectedRunId?: string,
): Promise<PreparedReplay> {
  if (
    !raw ||
    raw.contractVersion !== '1.0' ||
    !uuid(raw.runId) ||
    (expectedRunId && raw.runId !== expectedRunId) ||
    typeof raw.payloadJson !== 'string' ||
    raw.payloadJson.length > MAX_REPLAY_BYTES ||
    !/^[0-9a-f]{64}$/.test(raw.contentHash) ||
    !Array.isArray(raw.bookmarks) ||
    raw.bookmarks.length > 200
  )
    invalid();
  if ((await replayHash(raw.payloadJson)) !== raw.contentHash)
    throw new Error('Amprenta replay-ului nu corespunde datelor. Copia nu a fost încărcată.');
  let data: ReplayData;
  try {
    data = JSON.parse(raw.payloadJson);
  } catch {
    invalid();
  }
  if (!data || typeof data !== 'object') invalid();
  const run = data.run,
    captured = data.captured,
    inputs = captured?.scenario?.inputs,
    layout = captured?.layout?.layout;
  if (
    !run ||
    run.id !== raw.runId ||
    !uuid(run.sceneId) ||
    !uuid(run.scenarioId) ||
    !count(run.scenarioRevision) ||
    run.scenarioRevision < 1 ||
    !count(run.seed) ||
    run.status !== 'completed' ||
    run.resultsValid !== true ||
    typeof run.isSynthetic !== 'boolean' ||
    captured?.schemaVersion !== 1 ||
    !inputs ||
    !layout ||
    captured.scenario.scenario?.id !== run.scenarioId ||
    captured.scenario.scenario.revision !== run.scenarioRevision ||
    captured.scenario.scenario.sceneId !== run.sceneId ||
    layout.scene?.id !== run.sceneId ||
    inputs.seed !== run.seed ||
    !uuid(captured.layout.version?.id) ||
    captured.layout.version.id !== captured.scenario.scenario.layoutVersionId ||
    !utc(inputs.startsAtUtc) ||
    !count(inputs.durationMinutes) ||
    inputs.durationMinutes < 1 ||
    inputs.durationMinutes > 10080 ||
    !Array.isArray(layout.checkpoints) ||
    layout.checkpoints.length < 1 ||
    layout.checkpoints.length > 64 ||
    !Array.isArray(inputs.staffAllocations) ||
    !Array.isArray(inputs.events) ||
    !Array.isArray(data.samples) ||
    data.samples.length > 50000 ||
    (data.intervalMinutes !== null && ![1, 2, 3, 4, 5].includes(data.intervalMinutes))
  )
    invalid();
  const zones = new Set<string>();
  for (const cp of layout.checkpoints) {
    if (
      !cp ||
      !uuid(cp.id) ||
      zones.has(cp.id) ||
      typeof cp.code !== 'string' ||
      typeof cp.name !== 'string' ||
      !Number.isFinite(cp.capacityPerMinute) ||
      cp.capacityPerMinute <= 0
    )
      invalid();
    zones.add(cp.id);
  }
  const staff = new Set<string>();
  for (const member of inputs.staffAllocations) {
    if (
      !member ||
      !uuid(member.staffMemberId) ||
      staff.has(member.staffMemberId) ||
      !zones.has(member.checkpointId)
    )
      invalid();
    staff.add(member.staffMemberId);
  }
  // Check-in desks are a replay zone of their own: never staffed by controls, never a checkpoint KPI.
  const desks = checkInDesks(data);
  if (desks && (!uuid(desks.id) || zones.has(desks.id) || !(desks.serviceSeconds > 0))) invalid();
  const byMinute = new Map<number, Map<string, ReplaySample>>();
  for (const row of data.samples) {
    const desk = !!desks && row?.zoneId === desks.id;
    const sample = row?.checkIn;
    if (
      desk
        ? row.kpi !== null ||
          !sample ||
          sample.zoneType !== 'checkIn' ||
          sample.zoneId !== row.zoneId ||
          sample.minute !== row.minute ||
          sample.queuedPassengers !== row.queuedPassengers ||
          sample.processedPassengers !== row.processedPassengers
        : row?.checkIn !== undefined
    )
      invalid();
    if (
      !row ||
      !(zones.has(row.zoneId) || desk) ||
      !Number.isFinite(row.minute) ||
      row.minute < 0 ||
      row.minute > inputs.durationMinutes ||
      !utc(row.recordedAtUtc) ||
      Math.abs(
        Date.parse(row.recordedAtUtc) - Date.parse(inputs.startsAtUtc) - row.minute * 60000,
      ) > 1 ||
      !count(row.queuedPassengers) ||
      !count(row.processedPassengers)
    )
      invalid();
    if (row.kpi !== null) {
      const kpi = row.kpi;
      if (
        !kpi ||
        kpi.kpiVersion !== '1.0' ||
        kpi.runId !== run.id ||
        kpi.zoneId !== row.zoneId ||
        kpi.minute !== row.minute ||
        kpi.elapsedMinutes !== row.minute ||
        kpi.queuedPassengers !== row.queuedPassengers ||
        kpi.processedPassengers !== row.processedPassengers ||
        kpi.zoneType !== 'checkpoint' ||
        !utc(kpi.recordedAtUtc) ||
        Math.abs(Date.parse(kpi.recordedAtUtc) - Date.parse(row.recordedAtUtc)) > 1 ||
        ![
          'populationCount',
          'notArrivedPassengers',
          'processingPassengers',
          'waitSampleCount',
          'boardingAtRiskCount',
          'boardingRiskPopulationCount',
        ].every((key) => count((kpi as unknown as Record<string, unknown>)[key])) ||
        ![
          'meanWaitMinutes',
          'p95WaitMinutes',
          'throughputPerMinute',
          'utilization',
          'boardingLateRisk',
        ].every((key) => {
          const value = (kpi as unknown as Record<string, unknown>)[key];
          return (
            value === null || (typeof value === 'number' && Number.isFinite(value) && value >= 0)
          );
        })
      )
        invalid();
      if (
        (kpi.utilization !== null && kpi.utilization > 1) ||
        (kpi.boardingLateRisk !== null && kpi.boardingLateRisk > 1)
      )
        invalid();
    }
    const rows = byMinute.get(row.minute) ?? new Map<string, ReplaySample>();
    if (rows.has(row.zoneId)) invalid();
    rows.set(row.zoneId, row);
    byMinute.set(row.minute, rows);
  }
  const events = new Set<string>();
  for (const event of inputs.events) {
    const minute = (Date.parse(event?.occurredAtUtc) - Date.parse(inputs.startsAtUtc)) / 60000;
    if (
      !event ||
      !uuid(event.id) ||
      events.has(event.id) ||
      typeof event.type !== 'string' ||
      !event.type ||
      event.type.length > 64 ||
      !utc(event.occurredAtUtc) ||
      minute < 0 ||
      minute > inputs.durationMinutes ||
      typeof event.payloadJson !== 'string' ||
      (event.checkpointId != null && !zones.has(event.checkpointId))
    )
      invalid();
    try {
      const value = JSON.parse(event.payloadJson);
      if (!value || typeof value !== 'object' || Array.isArray(value)) invalid();
    } catch {
      invalid();
    }
    events.add(event.id);
  }
  const marks = new Set<string>();
  for (const bookmark of raw.bookmarks) {
    if (!validBookmark(bookmark, data) || marks.has(bookmark.id)) invalid();
    marks.add(bookmark.id);
  }
  const baseline = {
    runId: run.id,
    scenarioId: run.scenarioId,
    layoutId: captured.layout.version.id,
    source: 'stored-run' as const,
    status: run.status,
    capacities: [
      ...layout.checkpoints.map((cp) => ({
        zoneId: cp.id,
        label: cp.code,
        serviceRatePerMinute: cp.capacityPerMinute,
        staffCount: inputs.staffAllocations.filter((a) => a.checkpointId === cp.id).length,
      })),
      ...(desks && data.samples.some((row) => row.zoneId === desks.id)
        ? [
            {
              zoneId: desks.id,
              label: desks.code,
              serviceRatePerMinute: 60 / desks.serviceSeconds,
              staffCount: inputs.checkIn!.staffCount,
              zoneType: 'checkIn' as const,
            },
          ]
        : []),
    ],
    snapshots: data.samples.map((row) => ({
      zoneId: row.zoneId,
      elapsedMinutes: row.minute,
      queuedPassengers: row.queuedPassengers,
      processedPassengers: row.processedPassengers,
    })),
    snapshotIntervalMinutes: data.intervalMinutes ?? undefined,
  };
  // The payload is a value document; reading at T never updates it or executes a simulation.
  freeze(data);
  return {
    envelope: raw,
    data,
    heatmap: { baseline },
    passengers: preparePassengerReplay(baseline),
    byMinute,
    minutes: [...byMinute.keys()].sort((a, b) => a - b),
  };
}
function freeze(value: unknown): void {
  if (value && typeof value === 'object') {
    Object.freeze(value);
    for (const child of Object.values(value)) freeze(child);
  }
}
export function replayFrame(replay: PreparedReplay, minute: number) {
  const horizon = replay.data.captured.scenario.inputs.durationMinutes;
  const time = Number.isFinite(minute) ? Math.max(0, Math.min(minute, horizon)) : 0;
  const interval = replay.data.intervalMinutes;
  const anchor =
    interval === null || time === horizon ? time : Math.floor(time / interval) * interval;
  const samples = replay.byMinute.get(anchor);
  const checkpoints = replay.data.captured.layout.layout.checkpoints;
  const desks = checkInDesks(replay.data);
  return {
    time,
    anchor,
    zones: checkpoints.map((cp) => ({
      checkpoint: cp,
      sample: samples?.get(cp.id) ?? null,
    })),
    // Completeness concerns the controls; desks are reported only by runs that simulated them.
    complete: !!samples && checkpoints.every((cp) => samples.has(cp.id)),
    desks:
      desks && replay.data.samples.some((row) => row.zoneId === desks.id)
        ? { area: desks, sample: samples?.get(desks.id) ?? null }
        : null,
  };
}
/** The captured check-in area the scenario simulated, if any. */
export function checkInDesks(data: ReplayData) {
  const id = data.captured.scenario.inputs.checkIn?.areaId;
  return id
    ? (data.captured.layout.layout.checkInAreas?.find((area) => area.id === id) ?? null)
    : null;
}
export function replayEvents(replay: PreparedReplay) {
  const inputs = replay.data.captured.scenario.inputs;
  return inputs.events
    .map((event) => {
      let duration: number | null = null;
      try {
        const payload = JSON.parse(event.payloadJson);
        if (
          event.type === 'incident.passenger-surge' &&
          payload.contractVersion === '1.0' &&
          Number.isFinite(payload.durationMinutes) &&
          payload.durationMinutes > 0
        )
          duration = payload.durationMinutes;
      } catch {
        /* Validated while preparing the replay. */
      }
      return {
        ...event,
        minute: (Date.parse(event.occurredAtUtc) - Date.parse(inputs.startsAtUtc)) / 60000,
        duration,
      };
    })
    .sort((a, b) => a.minute - b.minute || a.id.localeCompare(b.id));
}

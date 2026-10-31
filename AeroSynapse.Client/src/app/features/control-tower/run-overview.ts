import { capturedHeatmapRun } from '../digital-twin/heatmap-adapters';
import { TwinCheckpoint, TwinScene } from '../digital-twin/twin-model';
import { RunDetails, ScenarioFlight } from '../operations/operations.models';

/** Everything an overview page shows, derived only from one stored run and its captured inputs. */
export interface RunOverview {
  runId: string;
  scenarioId: string;
  scenarioName: string;
  scenarioCode: string;
  finishedAtUtc: string | null;
  durationMinutes: number;
  seed: number;
  totals: {
    population: number;
    processed: number;
    /** Passengers still queued when the simulation ended; their waits are not in the mean. */
    queuedAtEnd: number;
    meanWaitMinutes: number | null;
    p95WaitMinutes: number | null;
    maximumQueue: number;
    maximumQueueCheckpoint: string | null;
    flights: number;
    seats: number;
    checkpoints: number;
    staff: number;
  };
  flights: FlightRow[];
  scene: TwinScene;
}
export interface FlightRow {
  id: string;
  code: string;
  gate: string;
  departure: string;
  boarding: string | null;
  seats: number;
  status: string;
  tone: 'gate' | 'boarding' | 'taxi' | 'neutral';
}

type Json = Record<string, unknown>;
const STATUS: Record<string, [string, FlightRow['tone']]> = {
  scheduled: ['Programat', 'gate'],
  delayed: ['Întârziat', 'boarding'],
  boarding: ['Îmbarcare', 'boarding'],
  closed: ['Îmbarcare închisă', 'taxi'],
  departed: ['Plecat', 'taxi'],
  cancelled: ['Anulat', 'neutral'],
};

export function runOverview(run: RunDetails): RunOverview | null {
  let captured: Json | null;
  try {
    captured = JSON.parse(run.inputSnapshotJson ?? 'null') as Json | null;
  } catch {
    return null;
  }
  const scenario = (captured?.['scenario'] ?? null) as { inputs?: Json; scenario?: Json } | null;
  const layout = ((captured?.['layout'] as Json | undefined)?.['layout'] ?? null) as Json | null;
  const inputs = scenario?.inputs;
  if (!run.resultsValid || !inputs || !layout) return null;

  const list = <T>(value: unknown) => (Array.isArray(value) ? (value as T[]) : []);
  const gates = new Map(
    list<{ id: string; code: string }>(layout['gates']).map((gate) => [gate.id, gate.code]),
  );
  const people = new Map(
    list<{ id: string; displayName: string }>(layout['staff']).map((person) => [person.id, person]),
  );
  // Names the user gave the operators win over the layout's slot names.
  const named = new Map(
    list<{ id: string; name: string }>(inputs['operators']).map((person) => [
      person.id,
      person.name,
    ]),
  );
  const allocations = list<{ checkpointId: string; staffMemberId: string }>(
    inputs['staffAllocations'],
  );
  const timeZone = typeof inputs['timeZone'] === 'string' ? inputs['timeZone'] : 'UTC';

  // Final value of each run metric per checkpoint, as written by the engine.
  const metric = new Map<string, number>();
  for (const row of run.metrics) metric.set(`${row.zoneId}/${row.name}`, row.value);
  const value = (zoneId: string, name: string) => metric.get(`${zoneId}/${name}`) ?? null;

  // The latest KPI snapshot per checkpoint carries the population and the queue left at the end.
  const minuteOf = (payload: Json) => Number(payload['elapsedMinutes'] ?? payload['minute']);
  const last = new Map<string, Json>();
  for (const row of run.snapshots) {
    try {
      const payload = JSON.parse(row.payloadJson) as Json;
      const current = last.get(row.zoneId);
      if (!current || minuteOf(payload) >= minuteOf(current)) last.set(row.zoneId, payload);
    } catch {
      // A malformed snapshot is skipped, never replaced by an invented value.
    }
  }

  const checkpoints: TwinCheckpoint[] = list<{
    id: string;
    code: string;
    capacityPerMinute: number;
  }>(layout['checkpoints']).map((checkpoint) => {
    const staff = allocations
      .filter((row) => row.checkpointId === checkpoint.id)
      .flatMap((row) => {
        const person = people.get(row.staffMemberId);
        return person
          ? [
              {
                id: person.id,
                name: named.get(person.id) ?? person.displayName,
                role: 'Control securitate',
              },
            ]
          : [];
      });
    const final = last.get(checkpoint.id);
    const queued = Number(final?.['queuedPassengers'] ?? 0);
    const utilization =
      value(checkpoint.id, 'cumulativeUtilization') ?? value(checkpoint.id, 'utilization');
    return {
      zoneId: checkpoint.id,
      code: checkpoint.code,
      capacityPerMinute: checkpoint.capacityPerMinute,
      staff,
      // Busy: passengers still queued at the end, or the lane served at least 85% of the time.
      status:
        staff.length === 0 ? 'closed' : queued > 0 || (utilization ?? 0) >= 0.85 ? 'busy' : 'open',
      metrics: {
        queuedPassengers: queued,
        processedPassengers:
          value(checkpoint.id, 'processedPassengers') ??
          Number(final?.['processedPassengers'] ?? 0),
        averageWaitMinutes: value(checkpoint.id, 'meanWaitMinutes'),
      },
    };
  });

  let samples = 0,
    weighted = 0,
    p95: number | null = null,
    maximumQueue = 0,
    maximumQueueCheckpoint: string | null = null;
  for (const checkpoint of checkpoints) {
    const count = value(checkpoint.zoneId, 'waitSampleCount') ?? 0,
      mean = value(checkpoint.zoneId, 'meanWaitMinutes');
    if (mean !== null && count > 0) {
      samples += count;
      weighted += mean * count;
    }
    const tail = value(checkpoint.zoneId, 'p95WaitMinutes');
    if (tail !== null) p95 = Math.max(p95 ?? 0, tail);
    const queue = value(checkpoint.zoneId, 'maximumQueue') ?? 0;
    if (queue > maximumQueue) {
      maximumQueue = queue;
      maximumQueueCheckpoint = checkpoint.code;
    }
  }

  const time = new Intl.DateTimeFormat('ro-RO', { timeZone, hour: '2-digit', minute: '2-digit' });
  const flights = list<ScenarioFlight>(inputs['flights'])
    .sort(
      (a, b) => a.departureAtUtc.localeCompare(b.departureAtUtc) || a.code.localeCompare(b.code),
    )
    .map((flight): FlightRow => {
      const [label, tone] = STATUS[flight.status ?? 'scheduled'] ?? [
        flight.status ?? '—',
        'neutral',
      ];
      const delay = flight.delayMinutes ?? 0;
      return {
        id: flight.id,
        code: flight.code,
        gate: gates.get(flight.gateId) ?? '—',
        departure: time.format(new Date(flight.departureAtUtc)),
        boarding:
          flight.boardingOpensAtUtc && flight.boardingClosesAtUtc
            ? `${time.format(new Date(flight.boardingOpensAtUtc))}–${time.format(new Date(flight.boardingClosesAtUtc))}`
            : null,
        seats: flight.passengerCapacity,
        status: delay > 0 && flight.status !== 'cancelled' ? `${label} · +${delay} min` : label,
        tone: delay > 0 && tone === 'gate' ? 'boarding' : tone,
      };
    });

  const summary = scenario?.scenario ?? {};
  const heatmap = capturedHeatmapRun(run);
  const name = String(summary['name'] ?? 'Scenariu');
  return {
    runId: run.id,
    scenarioId: run.scenarioId ?? String(summary['id'] ?? ''),
    scenarioName: name,
    scenarioCode: String(summary['code'] ?? ''),
    finishedAtUtc: run.finishedAtUtc ?? null,
    durationMinutes: Number(inputs['durationMinutes'] ?? 0),
    seed: run.seed,
    totals: {
      population: [...last.values()].reduce(
        (sum, row) => sum + Number(row['populationCount'] ?? 0),
        0,
      ),
      processed: checkpoints.reduce((sum, row) => sum + (row.metrics.processedPassengers ?? 0), 0),
      queuedAtEnd: checkpoints.reduce((sum, row) => sum + (row.metrics.queuedPassengers ?? 0), 0),
      meanWaitMinutes: samples > 0 ? weighted / samples : null,
      p95WaitMinutes: p95,
      maximumQueue,
      maximumQueueCheckpoint,
      flights: flights.length,
      seats: flights.reduce((sum, row) => sum + row.seats, 0),
      checkpoints: checkpoints.length,
      staff: checkpoints.reduce((sum, row) => sum + row.staff.length, 0),
    },
    flights,
    // Aircraft positions are not simulated, so no aircraft are placed in the scene.
    scene: {
      aircraft: [],
      scenarioId: name,
      checkpoints,
      heatmap: heatmap ? { baseline: heatmap } : undefined,
    },
  };
}

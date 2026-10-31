import { RunDetails } from '../operations/operations.models';

const CP = ['60000000-0000-0000-0000-000000000101', '60000000-0000-0000-0000-000000000102'];

/** A stored run shaped like the API's RunDetails, with captured inputs, final metrics and KPI snapshots. */
export function storedRun(overrides: Partial<RunDetails> = {}): RunDetails {
  const inputSnapshotJson = JSON.stringify({
    engine: 'simpy-kpi-1.0',
    snapshotIntervalMinutes: 1,
    layout: {
      version: { id: 'layout-1' },
      layout: {
        gates: [{ id: 'gate-1', code: 'G-1' }, { id: 'gate-2', code: 'G-2' }],
        checkpoints: [{ id: CP[0], code: 'CP-1', capacityPerMinute: 7 }, { id: CP[1], code: 'CP-2', capacityPerMinute: 8 }],
        staff: [{ id: 'staff-1', displayName: 'Operator fictiv 1' }, { id: 'staff-2', displayName: 'Operator fictiv 2' }],
      },
    },
    scenario: {
      scenario: { id: 'scenario-1', code: 'DEMO-1', name: 'Vârf de seară' },
      inputs: {
        timeZone: 'Europe/Bucharest',
        durationMinutes: 90,
        staffAllocations: [{ checkpointId: CP[0], staffMemberId: 'staff-1' }, { checkpointId: CP[0], staffMemberId: 'staff-2' }],
        flights: [
          { id: 'f-2', code: 'DEMO 102', gateId: 'gate-2', departureAtUtc: '2026-10-17T19:30:00Z', passengerCapacity: 150, status: 'scheduled', delayMinutes: 15 },
          { id: 'f-1', code: 'DEMO 101', gateId: 'gate-1', departureAtUtc: '2026-10-17T18:00:00Z', passengerCapacity: 120, status: 'boarding',
            boardingOpensAtUtc: '2026-10-17T17:40:00Z', boardingClosesAtUtc: '2026-10-17T17:50:00Z' },
        ],
      },
    },
  });
  const metric = (zoneId: string, name: string, value: number) => ({ id: `${zoneId}-${name}`, zoneId, name, value, unit: '', recordedAtUtc: '' });
  const snapshot = (zoneId: string, minute: number, queued: number, processed: number) => ({
    id: `${zoneId}-${minute}`, zoneId, recordedAtUtc: '',
    payloadJson: JSON.stringify({ zoneId, elapsedMinutes: minute, queuedPassengers: queued, processedPassengers: processed, populationCount: 100 }),
  });
  return {
    id: 'run-1', status: 'completed', revision: 3, seed: 42, scenarioRevision: 1, retryOfRunId: null,
    scenarioId: 'scenario-1', finishedAtUtc: '2026-10-17T20:00:00Z', resultsValid: true, failureReason: null, error: null,
    inputSnapshotJson,
    metrics: [
      metric(CP[0], 'processedPassengers', 90), metric(CP[0], 'meanWaitMinutes', 3), metric(CP[0], 'waitSampleCount', 90),
      metric(CP[0], 'p95WaitMinutes', 8), metric(CP[0], 'maximumQueue', 14), metric(CP[0], 'cumulativeUtilization', 0.5),
      metric(CP[1], 'processedPassengers', 0), metric(CP[1], 'waitSampleCount', 0), metric(CP[1], 'maximumQueue', 0),
    ],
    snapshots: [snapshot(CP[0], 1, 2, 1), snapshot(CP[0], 90, 4, 90), snapshot(CP[1], 1, 0, 0), snapshot(CP[1], 90, 0, 0)],
    ...overrides,
  };
}

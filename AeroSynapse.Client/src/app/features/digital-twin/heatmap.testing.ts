import { HeatmapDataset, HeatmapRun } from './heatmap-model';
import { RunDetails } from '../operations/operations.models';
import { TERMINAL_MAP_ZONES } from './terminal-zones';

export function datasetFixture(): HeatmapDataset {
  const zoneId = TERMINAL_MAP_ZONES[0].zoneId;
  const capacities = [{ zoneId, label: 'CP-1', serviceRatePerMinute: 7, staffCount: 1 }];
  return {
    baseline: {
      runId: 'base-run',
      scenarioId: 'scenario',
      layoutId: 'layout',
      source: 'simulation-engine',
      status: 'completed',
      capacities,
      snapshots: [
        { zoneId, elapsedMinutes: 1, queuedPassengers: 6, processedPassengers: 3 },
        { zoneId, elapsedMinutes: 2, queuedPassengers: 21, processedPassengers: 6 },
      ],
    },
    optimized: {
      runId: 'optimized-run',
      scenarioId: 'scenario',
      layoutId: 'layout',
      source: 'simulation-engine',
      status: 'completed',
      capacities: [{ ...capacities[0], staffCount: 2 }],
      snapshots: [
        { zoneId, elapsedMinutes: 1, queuedPassengers: 2, processedPassengers: 7 },
        { zoneId, elapsedMinutes: 2, queuedPassengers: 3, processedPassengers: 12 },
      ],
    },
  };
}

export function congestionRunFixture(): HeatmapRun {
  return {
    runId: 'run-1',
    scenarioId: 'scenario-1',
    layoutId: 'layout-1',
    source: 'stored-run',
    status: 'completed',
    capacities: ['cp-1', 'cp-2', 'cp-3'].map((zoneId) => ({
      zoneId, label: zoneId, serviceRatePerMinute: 10, staffCount: 1,
    })),
    snapshots: [0, 2, 5, 7].flatMap((minute) =>
      ['cp-1', 'cp-2', 'cp-3'].map((zoneId, index) => ({
        zoneId,
        elapsedMinutes: minute,
        processedPassengers: minute * 3,
        queuedPassengers:
          minute === 2 && index === 0 ? 30 :
          minute === 2 && index === 1 ? 10 :
          minute === 5 && index === 1 ? 15 : 0,
      })),
    ),
  };
}

export function storedFixture(run: HeatmapRun = datasetFixture().baseline): RunDetails {
  return {
    id: run.runId,
    status: run.status,
    revision: 1,
    seed: 42,
    scenarioRevision: 1,
    retryOfRunId: null,
    resultsValid: run.status === 'completed',
    failureReason: null,
    error: null,
    metrics: [],
    inputSnapshotJson: JSON.stringify({
      layout: {
        version: { id: run.layoutId },
        layout: {
          checkpoints: run.capacities.map((c) => ({
            id: c.zoneId,
            code: c.label,
            capacityPerMinute: c.serviceRatePerMinute,
          })),
        },
      },
      scenario: {
        scenario: { id: run.scenarioId },
        inputs: {
          staffAllocations: run.capacities.flatMap((c) =>
            Array.from({ length: c.staffCount }, (_, i) => ({
              staffMemberId: `${c.zoneId}:${i}`,
              checkpointId: c.zoneId,
            })),
          ),
        },
      },
    }),
    snapshots: run.snapshots.map((snapshot, i) => ({
      id: String(i),
      zoneId: snapshot.zoneId,
      recordedAtUtc: '2026-10-13T06:00:00Z',
      payloadJson: JSON.stringify(snapshot),
    })),
  };
}

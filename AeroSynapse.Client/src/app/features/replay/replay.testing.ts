import { ReplayData, ReplayEnvelope, replayHash } from './replay.models';

export const ids = {
  run: '10000000-0000-0000-0000-000000000001',
  otherRun: '10000000-0000-0000-0000-000000000002',
  scene: '20000000-0000-0000-0000-000000000001',
  scenario: '30000000-0000-0000-0000-000000000001',
  layout: '40000000-0000-0000-0000-000000000001',
  cp: '50000000-0000-0000-0000-000000000001',
  otherCp: '50000000-0000-0000-0000-000000000002',
  event: '60000000-0000-0000-0000-000000000001',
  mark: '70000000-0000-0000-0000-000000000001',
};
export function replayData(runId = ids.run): ReplayData {
  const at = (minute: number) =>
    new Date(Date.parse('2026-10-16T10:00:00Z') + minute * 60000).toISOString();
  return {
    run: {
      id: runId,
      sceneId: ids.scene,
      scenarioId: ids.scenario,
      scenarioRevision: 1,
      seed: 42,
      status: 'completed',
      resultsValid: true,
      isSynthetic: true,
      provenance: {
        hashVersion: '1.0',
        inputHash: 'input',
        resultHash: 'result',
        engineVersion: '1.0',
        engineBuild: 'test',
        engineMilliseconds: 20,
        executionMilliseconds: 30,
        correlationId: 'trace',
      },
    },
    captured: {
      schemaVersion: 1,
      capturedAtUtc: at(0),
      engine: 'simpy-kpi-1.0',
      snapshotIntervalMinutes: 2,
      scenario: {
        scenario: {
          id: ids.scenario,
          sceneId: ids.scene,
          layoutVersionId: ids.layout,
          revision: 1,
          code: 'demo',
          name: 'Replay demo',
          description: 'Synthetic',
        },
        inputs: {
          startsAtUtc: at(0),
          durationMinutes: 6,
          seed: 42,
          timeZone: 'Europe/Bucharest',
          snapshotIntervalMinutes: 2,
          flights: [],
          populations: [],
          flows: [],
          staffAllocations: [],
          events: [
            {
              id: ids.event,
              type: 'incident.passenger-surge',
              occurredAtUtc: at(2.5),
              checkpointId: ids.cp,
              payloadJson: JSON.stringify({
                contractVersion: '1.0',
                durationMinutes: 1,
                multiplier: 2,
              }),
            },
          ],
        },
      },
      layout: {
        version: { id: ids.layout, sceneId: ids.scene, version: 1, createdAtUtc: at(0) },
        layout: {
          scene: { id: ids.scene, code: 'demo', name: 'Airport demo', isSynthetic: true },
          gates: [],
          staff: [],
          routes: [],
          checkpoints: [
            {
              id: ids.cp,
              code: 'CP1',
              name: 'Security',
              capacityPerMinute: 10,
              terminalId: ids.scene,
            },
            {
              id: ids.otherCp,
              code: 'CP2',
              name: 'Border',
              capacityPerMinute: 10,
              terminalId: ids.scene,
            },
          ],
        },
      },
    },
    intervalMinutes: 2,
    samples: [0, 2, 4, 6].flatMap((minute) =>
      [ids.cp, ids.otherCp].map((zoneId) => ({
        zoneId,
        minute,
        recordedAtUtc: at(minute),
        queuedPassengers: 10 - minute,
        processedPassengers: minute,
        kpi: {
          kpiVersion: '1.0',
          zoneType: 'checkpoint',
          elapsedMinutes: minute,
          recordedAtUtc: at(minute),
          runId,
          zoneId,
          minute,
          windowStartMinute: Math.max(0, minute - 2),
          populationCount: 10,
          notArrivedPassengers: 0,
          queuedPassengers: 10 - minute,
          processingPassengers: 0,
          processedPassengers: minute,
          unreachablePassengers: 0,
          waitSampleCount: minute,
          meanWaitMinutes: minute ? 1.25 : null,
          medianWaitMinutes: minute ? 1 : null,
          p95WaitMinutes: minute ? 2 : null,
          throughputPerMinute: minute ? 1 : null,
          cumulativeThroughputPerMinute: minute ? 1 : null,
          maximumQueue: 10,
          firstQueueMinute: 0,
          utilization: minute ? 0.5 : null,
          cumulativeUtilization: minute ? 0.5 : null,
          boardingLateRisk: null,
          boardingAtRiskCount: 0,
          boardingRiskPopulationCount: 0,
        },
      })),
    ),
  };
}
export async function replayEnvelope(data = replayData()): Promise<ReplayEnvelope> {
  const payloadJson = JSON.stringify(data);
  return {
    contractVersion: '1.0',
    runId: data.run.id,
    payloadJson,
    contentHash: await replayHash(payloadJson),
    bookmarks: [],
  };
}

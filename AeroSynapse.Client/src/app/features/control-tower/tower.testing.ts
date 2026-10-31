import type { RunDetails } from '../operations/operations.models';
import { ids, replayData } from '../replay/replay.testing';

export { ids };
export function towerFixture(overrides: Partial<RunDetails> = {}) {
  const data = replayData();
  data.captured.scenario.scenario.isSynthetic = true;
  data.captured.scenario.inputs.populations = [{ id: ids.event, name: 'Populație API', count: 20 }];
  data.captured.scenario.inputs.events[0].payloadJson = JSON.stringify({
    contractVersion: '1.0',
    durationMinutes: 1,
    share: 0.4,
    isSynthetic: true,
  });
  data.captured.layout.layout.staff = [
    {
      id: ids.mark,
      code: 'OP-1',
      displayName: 'Operator API',
      terminalId: ids.scene,
      checkpointId: ids.cp,
    },
  ];
  data.captured.scenario.inputs.staffAllocations = [
    { staffMemberId: ids.mark, checkpointId: ids.cp },
  ];
  const run: RunDetails = {
    id: ids.run,
    scenarioId: ids.scenario,
    scenarioRevision: 1,
    revision: 3,
    seed: 42,
    status: 'completed',
    resultsValid: true,
    retryOfRunId: null,
    failureReason: null,
    error: null,
    inputSnapshotJson: JSON.stringify(data.captured),
    metrics: [ids.cp, ids.otherCp].flatMap((zoneId, index) =>
      [
        { name: 'waitSampleCount', value: index ? 3 : 6 },
        { name: 'meanWaitMinutes', value: index ? 4 : 1 },
        { name: 'p95WaitMinutes', value: index ? 7 : 2 },
      ].map((row) => ({
        id: zoneId + row.name,
        ...row,
        zoneId,
        unit: 'minutes',
        recordedAtUtc: '',
      })),
    ),
    snapshots: data.samples.map((row, i) => ({
      id: `snapshot-${i}`,
      zoneId: row.zoneId,
      recordedAtUtc: row.recordedAtUtc,
      payloadJson: JSON.stringify(row.kpi),
    })),
    ...overrides,
  };
  return { run, scenario: data.captured.scenario, layout: data.captured.layout };
}

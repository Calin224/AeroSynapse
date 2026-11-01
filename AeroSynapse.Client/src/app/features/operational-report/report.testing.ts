import { ids, replayData, replayEnvelope } from '../replay/replay.testing';
import { prepareReplay, PreparedReplay, ReplayData } from '../replay/replay.models';
import type { RunComparison } from '../operations/comparison.models';
import type { StudioLayout } from '../scenario-studio/studio.models';
export function comparisonFixture(): RunComparison {
  const provenance = replayData().run.provenance;
  return {
    baseline: {
      id: ids.run,
      status: 'completed',
      resultsValid: true,
      scenarioRevision: 1,
      provenance,
      failureReason: null,
    },
    candidate: {
      id: ids.otherRun,
      status: 'completed',
      resultsValid: true,
      scenarioRevision: 1,
      provenance,
      failureReason: null,
    },
    inputsMatch: false,
    versionsMatch: true,
    resultsMatch: false,
    metrics: [],
    metricsTruncated: false,
    comparability: {
      comparable: true,
      populationMatches: true,
      baselinePopulationHash: 'same',
      candidatePopulationHash: 'same',
      issues: [],
    },
  };
}
export function candidateData() {
  const data = replayData(ids.otherRun);
  for (const row of data.samples) {
    row.queuedPassengers /= 2;
    row.kpi!.queuedPassengers = row.queuedPassengers;
    row.kpi!.meanWaitMinutes = row.minute ? 0.625 : null;
    row.kpi!.cumulativeUtilization = row.minute ? 0.25 : null;
  }
  return data;
}
export async function prepared(data: ReplayData = replayData()): Promise<PreparedReplay> {
  return prepareReplay(await replayEnvelope(data));
}
export function bottleneckLayout(): StudioLayout {
  const layout = structuredClone(replayData().captured.layout);
  layout.layout.gates = Array.from({ length: 5 }, (_, i) => ({
    id: `80000000-0000-0000-0000-00000000000${i + 1}`,
    code: `G${i + 1}`,
    name: 'Demo gate',
    capacity: 150,
    terminalId: ids.scene,
  }));
  layout.layout.routes = layout.layout.gates.map((gate, i) => ({
    id: `90000000-0000-0000-0000-00000000000${i + 1}`,
    code: `CP1-G${i + 1}`,
    fromCheckpointId: ids.cp,
    toGateId: gate.id,
    terminalId: ids.scene,
  }));
  layout.layout.staff = [1, 2, 3].map((i) => ({
    id: `a0000000-0000-0000-0000-00000000000${i}`,
    code: `ST${i}`,
    displayName: 'Demo',
    terminalId: ids.scene,
    checkpointId: ids.cp,
  }));
  return layout;
}

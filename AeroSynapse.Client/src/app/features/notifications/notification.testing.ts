import { AlertActivity, AlertDetails, OperationalAlert } from './notification.models';
import { ids, towerFixture } from '../control-tower/tower.testing';
export const notificationIds = {
  ...ids,
  alert: 'a6000000-0000-0000-0000-000000000001',
  activity: 'a7000000-0000-0000-0000-000000000001',
};
export function alertFixture(): OperationalAlert {
  const { scenario, layout } = towerFixture();
  return {
    id: notificationIds.alert,
    scenarioId: ids.scenario,
    scenarioName: scenario.scenario.name,
    runId: ids.run,
    zoneId: layout.layout.checkpoints[0].id,
    zoneName: layout.layout.checkpoints[0].name,
    source: 'queue_forecast',
    warningThreshold: 10,
    criticalThreshold: 20,
    horizonMinutes: 15,
    level: 'critical',
    revision: 1,
    observations: 1,
    firstObservedAtUtc: '2026-10-20T10:00:00Z',
    lastObservedAtUtc: '2026-10-20T10:00:00Z',
    simulationAtUtc: '2026-10-16T10:05:00Z',
    evidence: {
      fromMinute: 0,
      toMinute: 15,
      peakQueuePassengers: 30,
      triggerMinute: 5,
      periodStartUtc: '2026-10-16T10:00:00Z',
      periodEndUtc: '2026-10-16T10:15:00Z',
      observationSource: 'captured_snapshot',
      formula: 'Q[t+1]=max(0,Q[t]+arrivals-capacity)',
    },
    assignee: '',
    acknowledgedAtUtc: null,
    acknowledgedBy: '',
    isSynthetic: true,
  };
}
export function activityFixture(): AlertActivity {
  const a = alertFixture();
  return {
    id: notificationIds.activity,
    alertId: a.id,
    scenarioId: a.scenarioId,
    runId: a.runId,
    zoneId: a.zoneId,
    kind: 'created',
    actor: 'Motor prognoză',
    text: '',
    recordedAtUtc: a.firstObservedAtUtc,
    alertRevision: 1,
  };
}
export function detailFixture(): AlertDetails {
  return { alert: alertFixture(), activity: [activityFixture()] };
}

export function notificationRunFixture() {
  const run = towerFixture().run;
  const captured = JSON.parse(run.inputSnapshotJson!);
  captured.scenario.inputs.durationMinutes = 60;
  return { ...run, inputSnapshotJson: JSON.stringify(captured) };
}

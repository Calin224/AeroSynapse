import {
  capturedContext,
  runHeatmap,
  simulationMinute,
  staffAt,
  towerIncidents,
  towerMetrics,
  towerPoints,
  towerScene,
  virtualTime,
} from './tower.models';
import { ids, towerFixture } from './tower.testing';

describe('Control Tower captured data and KPI invariants', () => {
  it('does not report a partial or invalid checkpoint percentile as the terminal maximum', () => {
    const { run } = towerFixture();
    run.metrics = run.metrics.filter(
      (row) => !(row.zoneId === ids.otherCp && row.name === 'p95WaitMinutes'),
    );
    expect(towerMetrics(run, runHeatmap(run)).maxP95).toBeNull();
    run.metrics.filter((row) => row.name === 'waitSampleCount').forEach((row) => (row.value = -1));
    expect(towerMetrics(run, runHeatmap(run))).toMatchObject({ meanWait: null, maxP95: null });
  });
  it('reconciles common-minute sums and weights means by the actual sample counts', () => {
    const { run } = towerFixture();
    const view = towerMetrics(run, runHeatmap(run));
    expect(view).toMatchObject({ queued: 8, processed: 12, minute: 6, meanWait: 2, maxP95: 7 });
    expect(view.points.map((row) => row.minute)).toEqual([0, 2, 4, 6]);
  });
  it('keeps the check-in desks out of the checkpoint queue totals and waits', () => {
    const { run } = towerFixture();
    const heatmap = runHeatmap(run)!;
    const before = towerMetrics(run, heatmap);
    heatmap.capacities.push({
      zoneId: 'desks',
      label: 'CHECK-IN',
      serviceRatePerMinute: 0.5,
      staffCount: 8,
      zoneType: 'checkIn',
    });
    for (const point of before.points)
      heatmap.snapshots.push({
        zoneId: 'desks',
        elapsedMinutes: point.minute,
        queuedPassengers: 50,
        processedPassengers: 5,
      });
    expect(towerMetrics(run, heatmap)).toEqual(before);
  });
  it('never sums checkpoints from different minutes or fills missing observations with zero', () => {
    const { run } = towerFixture();
    const heatmap = runHeatmap(run)!;
    heatmap.snapshots = heatmap.snapshots.filter(
      (row) => row.zoneId !== ids.otherCp || row.elapsedMinutes !== 6,
    );
    expect(towerPoints(heatmap).at(-1)?.minute).toBe(4);
    heatmap.snapshots = heatmap.snapshots.filter((row) => row.zoneId !== ids.otherCp);
    expect(towerMetrics(run, heatmap)).toMatchObject({ queued: null, processed: null, points: [] });
  });
  it('keeps unavailable means absent and distinguishes valid zero from missing data', () => {
    const { run } = towerFixture();
    run.metrics = run.metrics.filter(
      (row) => !(row.zoneId === ids.otherCp && row.name === 'meanWaitMinutes'),
    );
    expect(towerMetrics(run, runHeatmap(run)).meanWait).toBeNull();
    run.metrics.filter((row) => row.name === 'meanWaitMinutes').forEach((row) => (row.value = 0));
    run.metrics.push({ ...run.metrics[0], zoneId: ids.otherCp, name: 'meanWaitMinutes', value: 0 });
    expect(towerMetrics(run, runHeatmap(run)).meanWait).toBe(0);
  });
  it('rejects foreign scenario, revision, seed and layout captures', () => {
    const { run } = towerFixture();
    expect(() => capturedContext(run, ids.otherRun)).toThrow();
    expect(() => capturedContext({ ...run, scenarioRevision: 2 }, ids.scenario)).toThrow();
    expect(() => capturedContext({ ...run, seed: 1 }, ids.scenario)).toThrow();
    expect(() => capturedContext({ ...run, inputSnapshotJson: '{' }, ids.scenario)).toThrow();
    const captured = JSON.parse(run.inputSnapshotJson!);
    captured.layout.version.id = ids.otherRun;
    expect(() =>
      capturedContext({ ...run, inputSnapshotJson: JSON.stringify(captured) }, ids.scenario),
    ).toThrow();
  });
  it('excludes unvalidated results and foreign snapshot payloads', () => {
    const { run } = towerFixture({ resultsValid: false, status: 'failed' });
    expect(towerMetrics(run, runHeatmap(run))).toMatchObject({
      processed: null,
      queued: null,
      meanWait: null,
      maxP95: null,
    });
    const valid = towerFixture().run;
    valid.snapshots.forEach(
      (row) =>
        (row.payloadJson = JSON.stringify({ ...JSON.parse(row.payloadJson), runId: ids.otherRun })),
    );
    expect(towerMetrics(valid, runHeatmap(valid)).points).toEqual([]);
  });
  it('uses scenario timezone and the observed minute without inventing progress', () => {
    const { run } = towerFixture();
    const context = capturedContext(run, ids.scenario);
    expect(virtualTime(context, 6)).toBe('13:06:00');
    expect(virtualTime(context, null)).toBeNull();
    expect(simulationMinute(null, null, null)).toBeNull();
    expect(
      simulationMinute(
        run,
        {
          runId: run.id,
          status: 'running',
          simulationMinute: 2,
          revision: 1,
          resultsValid: false,
          snapshot: null,
        },
        runHeatmap(run),
      ),
    ).toBe(6);
  });
  it('lists captured incidents and roster without claiming event execution or invented observations', () => {
    const { run } = towerFixture();
    const context = capturedContext(run, ids.scenario);
    expect(towerIncidents(context, null)[0]).toMatchObject({
      label: 'Vârf de sosire',
      detail: '40% din populația zborului / 1 min',
      phase: 'programat',
    });
    expect(towerIncidents(context, 3)[0].phase).toBe('moment atins');
    const scene = towerScene(context, runHeatmap(run));
    expect(scene.scenarioId).toBe(ids.scenario);
    expect(scene.checkpoints?.[0].staff[0].name).toBe('Operator API');
    expect(scene.checkpoints?.[0].metrics.queuedPassengers).toBeNull();
  });
  it('staffs a checkpoint from the shift in progress when the scenario has a shift plan', () => {
    const context = capturedContext(towerFixture().run, ids.scenario);
    const person = context.scenario.inputs.staffAllocations[0].staffMemberId;
    context.scenario.inputs.staffShifts = {
      slotMinutes: 15,
      transferMinutes: 5,
      shifts: [
        {
          staffMemberId: person,
          segments: [{ checkpointId: ids.cp, startMinute: 30, endMinute: 60 }],
        },
      ],
    };
    expect(staffAt(context.scenario.inputs, ids.cp, 10)).toEqual([]);
    expect(staffAt(context.scenario.inputs, ids.cp, 45)).toEqual([
      { staffMemberId: person, checkpointId: ids.cp },
    ]);
    expect(towerScene(context, null, 10).checkpoints?.[0].status).toBe('closed');
    expect(towerScene(context, null, 45).checkpoints?.[0].staff[0].name).toBe('Operator API');
  });
});

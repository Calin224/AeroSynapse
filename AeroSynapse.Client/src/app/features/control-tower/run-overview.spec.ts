import { presentable, runOverview } from './run-overview';
import { storedRun } from './run-overview.testing';

describe('Run overview', () => {
  it('derives totals, flights and checkpoint state only from the stored run', () => {
    const view = runOverview(storedRun())!;
    expect(view.scenarioName).toBe('Vârf de seară');
    expect(view.totals).toEqual({
      population: 200,
      processed: 90,
      queuedAtEnd: 4,
      meanWaitMinutes: 3,
      p95WaitMinutes: 8,
      maximumQueue: 14,
      maximumQueueCheckpoint: 'CP-1',
      flights: 2,
      seats: 270,
      checkpoints: 2,
      staff: 2,
    });
    expect(
      view.flights.map((flight) => [
        flight.code,
        flight.gate,
        flight.departure,
        flight.status,
        flight.boarding,
      ]),
    ).toEqual([
      ['DEMO 101', 'G-1', '21:00', 'Îmbarcare', '20:40–20:50'],
      ['DEMO 102', 'G-2', '22:30', 'Programat · +15 min', null],
    ]);
    const [busy, closed] = view.scene.checkpoints!;
    expect(busy).toMatchObject({
      code: 'CP-1',
      status: 'busy',
      metrics: { queuedPassengers: 4, processedPassengers: 90, averageWaitMinutes: 3 },
    });
    expect(busy.staff.map((person) => person.name)).toEqual([
      'Operator fictiv 1',
      'Operator fictiv 2',
    ]);
    expect(closed).toMatchObject({
      code: 'CP-2',
      status: 'closed',
      metrics: { averageWaitMinutes: null },
    });
    expect(view.scene.aircraft).toEqual([]);
    expect(view.scene.heatmap?.baseline.snapshots).toHaveLength(4);
  });
  it('reports no wait instead of inventing one when nobody waited', () => {
    const run = storedRun();
    const view = runOverview({
      ...run,
      metrics: run.metrics.filter(
        (row) => row.name !== 'meanWaitMinutes' && row.name !== 'p95WaitMinutes',
      ),
    })!;
    expect(view.totals.meanWaitMinutes).toBeNull();
    expect(view.totals.p95WaitMinutes).toBeNull();
  });
  it('shows the names the user gave the operators', () => {
    const run = storedRun();
    const captured = JSON.parse(run.inputSnapshotJson!);
    captured.scenario.inputs.operators = [{ id: 'staff-1', name: 'Ana', role: 'security' }];
    const view = runOverview({ ...run, inputSnapshotJson: JSON.stringify(captured) })!;
    expect(view.scene.checkpoints![0].staff.map((person) => person.name)).toEqual([
      'Ana',
      'Operator fictiv 2',
    ]);
  });
  it('refuses runs without valid results or captured inputs', () => {
    expect(runOverview(storedRun({ resultsValid: false }))).toBeNull();
    expect(runOverview(storedRun({ inputSnapshotJson: '{' }))).toBeNull();
    expect(presentable(storedRun())).toBe(true);
    expect(presentable(storedRun({ status: 'failed' }))).toBe(false);
    expect(presentable(storedRun({ scenarioId: null }))).toBe(false);
  });
});

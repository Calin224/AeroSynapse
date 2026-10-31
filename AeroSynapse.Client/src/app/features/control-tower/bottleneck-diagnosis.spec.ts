import type { HeatmapRun, ZoneSnapshot } from '../digital-twin/heatmap-model';
import type { ScenarioInputs } from '../operations/operations.models';
import { diagnose } from './bottleneck-diagnosis';

/** A queue served at `capacity` passengers/min, sampled every minute, fed by `arrivals(minute)`. */
function series(zoneId: string, capacity: number, arrivals: (minute: number) => number) {
  const rows: ZoneSnapshot[] = [];
  let queued = 0,
    processed = 0;
  for (let minute = 0; minute <= 40; minute++) {
    if (minute > 0) {
      queued += arrivals(minute);
      const served = Math.min(queued, capacity);
      queued -= served;
      processed += served;
    }
    rows.push({
      zoneId,
      elapsedMinutes: minute,
      queuedPassengers: queued,
      processedPassengers: processed,
    });
  }
  return rows;
}
function run(snapshots: ZoneSnapshot[], staff: Record<string, number>): HeatmapRun {
  return {
    runId: 'run',
    scenarioId: 'scenario',
    layoutId: 'layout',
    source: 'stored-run',
    status: 'completed',
    capacities: Object.entries(staff).map(([zoneId, staffCount]) => ({
      zoneId,
      label: zoneId,
      serviceRatePerMinute: 2,
      staffCount,
    })),
    snapshots,
  };
}
const fixed = { staffShifts: null } as ScenarioInputs;
const surge = (minute: number) => (minute <= 10 ? 5 : 0);

describe('bottleneck diagnosis', () => {
  it('finds when a checkpoint falls behind and lends an idle operator from another one', () => {
    const report = diagnose(
      run([...series('CP-1', 2, surge), ...series('CP-2', 4, () => 1)], { 'CP-1': 1, 'CP-2': 2 }),
      fixed,
    );
    expect(report.bottlenecks).toHaveLength(1);
    const [item] = report.bottlenecks;
    expect(item).toMatchObject({
      label: 'CP-1',
      fromMinute: 0,
      toMinute: 21,
      peakMinute: 10,
      peakQueue: 30,
      peakWaitMinutes: 15,
      staff: 1,
      capacityPerMinute: 2,
    });
    expect(item.arrivalsPerMinute).toBeCloseTo(5);
    expect(item.fix).toMatchObject({
      extraStaff: 1,
      donors: [{ zoneId: 'CP-2', label: 'CP-2', count: 1 }],
    });
    expect(item.fix!.expectedPeakWaitMinutes).toBeLessThan(5);
    expect(report.spare.map((row) => row.label)).toEqual(['CP-2']);
  });

  it('asks for extra staff when no other checkpoint can spare anyone', () => {
    const report = diagnose(
      run([...series('CP-1', 2, surge), ...series('CP-2', 2, () => 2)], { 'CP-1': 1, 'CP-2': 1 }),
      fixed,
    );
    expect(report.bottlenecks[0].fix).toMatchObject({ extraStaff: 1, donors: [] });
    expect(report.spare).toEqual([]);
  });

  it('points at a closed checkpoint that could take part of the flow', () => {
    const report = diagnose(
      run([...series('CP-1', 2, surge), ...series('CP-2', 0, () => 0)], { 'CP-1': 1, 'CP-2': 0 }),
      fixed,
    );
    expect(report.bottlenecks[0]).toMatchObject({ closed: ['CP-2'], fix: { donors: [] } });
  });

  it('reads the staff on duty from the shift plan, so an unstaffed queue is a bottleneck', () => {
    const shifts = {
      staffShifts: {
        slotMinutes: 15,
        transferMinutes: 0,
        shifts: [
          {
            staffMemberId: 'a',
            segments: [{ checkpointId: 'CP-1', startMinute: 20, endMinute: 60 }],
          },
        ],
      },
    } as ScenarioInputs;
    const snapshots = series('CP-1', 0, (minute) => (minute <= 3 ? 2 : 0)).map((row) =>
      row.elapsedMinutes >= 20 ? { ...row, queuedPassengers: 0, processedPassengers: 6 } : row,
    );
    const [item] = diagnose(run(snapshots, { 'CP-1': 1 }), shifts).bottlenecks;
    expect(item).toMatchObject({ label: 'CP-1', staff: 0, peakWaitMinutes: Infinity });
    expect(item.fix?.extraStaff).toBe(1);
  });

  it('reports the longest wait when nothing crosses the limit', () => {
    const report = diagnose(
      run(
        series('CP-1', 4, () => 3),
        { 'CP-1': 2 },
      ),
      fixed,
    );
    expect(report.bottlenecks).toEqual([]);
    expect(report.worst).toEqual({ label: 'CP-1', waitMinutes: 0 });
  });
});

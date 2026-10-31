import {
  buildHeatmapFrame,
  busiestMoment,
  comparable,
  congestion,
  HeatmapDataset,
  heatmapMinutes,
} from './heatmap-model';
import { datasetFixture } from './heatmap.testing';

describe('Calculated congestion frames', () => {
  it('uses exact thresholds, effective staff capacity and explicit unavailable/closed states', () => {
    expect(congestion(6, 7)).toBe('low');
    expect(congestion(7, 7)).toBe('warning');
    expect(congestion(21, 7)).toBe('critical');
    expect(congestion(0, 0)).toBe('closed');
    expect(congestion(1, 0)).toBe('critical');
    expect(congestion(3, null)).toBe('unavailable');
    const dataset = datasetFixture();
    const frame = buildHeatmapFrame(dataset, 'optimized', 2);
    expect(frame.zones[0]).toMatchObject({
      runId: 'optimized-run',
      elapsedMinutes: 2,
      queuedPassengers: 3,
      capacityPerMinute: 14,
      band: 'low',
    });
    expect(dataset.baseline.capacities[0].staffCount).toBe(1);
    expect(frame.kpis.queuedPassengers).toBe(3);
    dataset.optimized!.capacities[0].serviceRatePerMinute = NaN;
    const unavailable = buildHeatmapFrame(dataset, 'optimized', 2);
    expect(unavailable.zones[0].band).toBe('unavailable');
    expect(unavailable.kpis.criticalZones).toBeNull();
  });
  it('follows the operators on duty at each minute when the run recorded them', () => {
    const dataset = datasetFixture();
    const snapshot = dataset.baseline.snapshots.find((row) => row.elapsedMinutes === 2)!;
    const configured = buildHeatmapFrame(dataset, 'baseline', 2).zones[0];
    snapshot.staffOnDuty = configured.staffCount! + 2;
    const staffed = buildHeatmapFrame(dataset, 'baseline', 2).zones[0];
    expect(staffed.staffCount).toBe(configured.staffCount! + 2);
    expect(staffed.capacityPerMinute).toBeGreaterThan(configured.capacityPerMinute!);
  });
  it('does not interpolate, replay old colors or invent zero queues at a missing minute', () => {
    const frame = buildHeatmapFrame(datasetFixture(), 'baseline', 1.5);
    expect(frame.zones).toHaveLength(0);
    expect(frame.missingZones).toHaveLength(1);
    expect(frame.kpis.queuedPassengers).toBeNull();
    expect(frame.kpis.criticalZones).toBeNull();
    expect(frame.kpis.capacityPerMinute).toBe(7);
  });
  it('keeps comparison on the same layout and keeps exact available minutes', () => {
    const dataset = datasetFixture();
    expect(comparable(dataset)).toBe(true);
    expect(heatmapMinutes(dataset)).toEqual([1, 2]);
    dataset.optimized!.layoutId = 'different-layout';
    expect(comparable(dataset)).toBe(false);
    expect(buildHeatmapFrame(dataset, 'optimized', 2).runId).toBe('base-run');
  });
  it('rejects malformed numerical counts without failing other zones', () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    const dataset = datasetFixture();
    dataset.baseline.snapshots[1].queuedPassengers = NaN;
    expect(buildHeatmapFrame(dataset, 'baseline', 2).kpis.queuedPassengers).toBeNull();
    expect(warn).toHaveBeenCalledWith(
      'ZoneSnapshot invalid',
      expect.objectContaining({ runId: 'base-run', minute: 2 }),
    );
  });
  it('finds the single longest baseline queue, earliest on ties, or null when nobody waited', () => {
    const dataset = datasetFixture();
    // Baseline CP-1 queues 6 at minute 1 and 21 at minute 2; the optimized run is ignored.
    expect(busiestMoment(dataset)).toMatchObject({ elapsedMinutes: 2, queuedPassengers: 21, label: 'CP-1' });
    const flat: HeatmapDataset = {
      baseline: {
        ...dataset.baseline,
        snapshots: dataset.baseline.snapshots.map((row) => ({ ...row, queuedPassengers: 4 })),
      },
    };
    expect(busiestMoment(flat)?.elapsedMinutes).toBe(1);
    const empty: HeatmapDataset = {
      baseline: {
        ...dataset.baseline,
        snapshots: dataset.baseline.snapshots.map((row) => ({ ...row, queuedPassengers: 0 })),
      },
    };
    expect(busiestMoment(empty)).toBeNull();
  });
});

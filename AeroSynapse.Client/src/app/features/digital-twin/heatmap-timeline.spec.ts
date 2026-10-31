import { CONGESTION_BANDS } from './heatmap-model';
import { congestionAt, congestionGradient, congestionTimeline } from './heatmap-timeline';
import { congestionRunFixture } from './heatmap.testing';

describe('Worst-zone congestion timeline', () => {
  it('uses a single critical zone instead of averaging it with clear or warning zones', () => {
    const run = congestionRunFixture();
    const original = JSON.stringify(run);
    expect(congestionTimeline(run, [0, 2, 5, 7])).toEqual([
      { minute: 0, band: 'low' },
      { minute: 2, band: 'critical' },
      { minute: 5, band: 'warning' },
      { minute: 7, band: 'low' },
    ]);
    expect(JSON.stringify(run)).toBe(original);
  });

  it('uses staffing in effect at each snapshot, including a closed control with waiting passengers', () => {
    const run = congestionRunFixture();
    run.snapshots = run.snapshots.map((row) => ({
      ...row,
      staffOnDuty: row.elapsedMinutes === 2 ? 10 : 1,
    }));
    expect(congestionTimeline(run, [2])[0].band).toBe('low');
    run.snapshots[3].staffOnDuty = 0;
    expect(congestionTimeline(run, [2])[0].band).toBe('critical');
  });

  it('distinguishes missing data and closed controls while preserving known warnings and critical states', () => {
    const run = congestionRunFixture();
    run.snapshots = run.snapshots.filter((row) => row.zoneId !== 'cp-3');
    expect(congestionTimeline(run, [0, 2, 5, 6]).map((row) => row.band)).toEqual([
      'unavailable',
      'critical',
      'warning',
      'unavailable',
    ]);
    run.capacities = run.capacities.map((capacity) => ({ ...capacity, staffCount: 0 }));
    run.snapshots = [0].flatMap((elapsedMinutes) =>
      run.capacities.map((capacity) => ({
        zoneId: capacity.zoneId,
        elapsedMinutes,
        queuedPassengers: 0,
        processedPassengers: 0,
      })),
    );
    expect(congestionTimeline(run, [0])[0].band).toBe('closed');
    expect(congestionTimeline({ ...run, capacities: [], snapshots: [] }, [0])[0].band).toBe(
      'unavailable',
    );
  });

  it('reserves the right amount of track for uneven time intervals and never blends severity colors', () => {
    const points = [
      { minute: 0, band: 'low' as const },
      { minute: 2, band: 'critical' as const },
      { minute: 5, band: 'warning' as const },
      { minute: 10, band: 'low' as const },
    ];
    expect(congestionGradient(points, 0, 10)).toBe(
      'linear-gradient(to right, #41d3a2 0%, #41d3a2 20%, #fa665c 20%, #fa665c 50%, #ffc45b 50%, #ffc45b 100%)',
    );
    expect(congestionAt(points, 1.99)).toBe('low');
    expect(congestionAt(points, 2)).toBe('critical');
    expect(congestionAt(points, 4.99)).toBe('critical');
    expect(congestionAt(points, 5)).toBe('warning');
    expect(congestionAt(points, 10)).toBe('low');
  });

  it('marks time before the first snapshot as unavailable and handles empty or single-moment timelines', () => {
    expect(congestionGradient([{ minute: 2, band: 'critical' }], 0, 4)).toBe(
      'linear-gradient(to right, #687787 0%, #687787 50%, #fa665c 50%, #fa665c 100%)',
    );
    expect(congestionAt([], 0)).toBe('unavailable');
    expect(congestionGradient([], 0, 5)).toBe(CONGESTION_BANDS.unavailable.color);
    expect(congestionGradient([{ minute: 2, band: 'critical' }], 2, 2)).toBe(
      CONGESTION_BANDS.critical.color,
    );
  });
});

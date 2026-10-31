import { ids, replayData } from '../replay/replay.testing';
import { candidateData, pairFixture, bottleneckLayout } from './report.testing';
import {
  REPORT_METRICS,
  compatible,
  difference,
  reportChart,
  summaryRows,
  temporalRows,
} from './report.models';
import { makeDraft, basicErrors } from '../scenario-studio/studio.models';

describe('Operational KPI sources, math and common selection', () => {
  beforeEach(async () =>
    vi.stubGlobal(
      'crypto',
      (await vi.importActual<{ webcrypto: Crypto }>('node:crypto')).webcrypto,
    ),
  );
  afterEach(() => vi.unstubAllGlobals());
  it.each([
    [100, 80, -20, -20],
    [80, 100, 20, 25],
    [0, 5, 5, null],
    [0, 0, 0, null],
    [-1, 3, 4, null],
    [100, 0, -100, -100],
  ])(
    'computes %s to %s without dividing by an invalid baseline',
    (before, after, delta, percent) => {
      expect(difference(before, after, true)).toMatchObject({ delta, percent });
    },
  );
  it.each([null, NaN, Infinity, -Infinity])('does not manufacture a value for %s', (value) => {
    expect(difference(value, 10, true).delta).toBeNull();
    expect(difference(10, value, true).percent).toBeNull();
  });
  it('guards overflow and blocks differences for incompatible runs', () => {
    expect(difference(-Number.MAX_VALUE, Number.MAX_VALUE, true).delta).toBeNull();
    expect(difference(Number.MIN_VALUE, 1, true).percent).toBeNull();
    expect(difference(5, 2, false).delta).toBeNull();
  });
  it('derives final summary, chart and rows from the same captured checkpoint and runIds', async () => {
    const pair = await pairFixture(),
      metric = REPORT_METRICS[0],
      original = JSON.stringify(pair.baseline.replay!.data);
    const summary = summaryRows(pair, ids.cp).find((row) => row.metric.key === metric.key)!;
    const rows = temporalRows(pair, ids.cp, metric),
      chart = reportChart(pair, ids.cp, metric);
    expect(summary).toMatchObject({ before: 4, after: 2, delta: -2, percent: -50 });
    expect(rows.at(-1)).toMatchObject({ minute: 6, before: 4, after: 2, percent: -50 });
    expect(chart.series.map((row) => row.runId)).toEqual([ids.run, ids.otherRun]);
    expect(chart.series.map((row) => row.observed)).toEqual([4, 4]);
    expect(JSON.stringify(pair.baseline.replay!.data)).toBe(original);
  });
  it('breaks a chart across missing observations and never backfills a missing final snapshot', async () => {
    const left = replayData(),
      right = candidateData();
    left.samples = left.samples.filter((row) => row.minute !== 2);
    right.samples = right.samples.filter((row) => row.minute !== 6);
    const pair = await pairFixture(left, right),
      metric = REPORT_METRICS[0];
    expect(summaryRows(pair, ids.cp)[0]).toMatchObject({
      before: 4,
      after: null,
      delta: null,
      percent: null,
    });
    expect(temporalRows(pair, ids.cp, metric).find((row) => row.minute === 2)).toMatchObject({
      before: null,
      after: 4,
      reason: 'Date lipsă',
    });
    expect(reportChart(pair, ids.cp, metric).series[0].path.match(/M/g)).toHaveLength(2);
    expect(reportChart(pair, ids.cp, metric).series[0].missing).toBe(1);
  });
  it('keeps null cohorts and unknown metrics missing, and expresses rate deltas as percentage points', async () => {
    const pair = await pairFixture(),
      summary = summaryRows(pair, ids.cp);
    expect(summary.find((row) => row.metric.key === 'boardingLateRisk')?.before).toBeNull();
    const rate = summary.find((row) => row.metric.key === 'cumulativeUtilization')!;
    expect(rate).toMatchObject({ before: 0.5, after: 0.25, delta: -0.25, percent: -50 });
    expect(rate.delta! * rate.metric.scale).toBe(-25);
  });
  it.each(['wrong-pair', 'unknown-build', 'population', 'failed'] as const)(
    'suppresses misleading comparisons with %s',
    async (reason) => {
      const pair = await pairFixture();
      if (reason === 'wrong-pair') pair.comparison!.candidate.id = ids.mark;
      if (reason === 'unknown-build') pair.comparison!.versionsMatch = null;
      if (reason === 'population') pair.comparison!.comparability!.populationMatches = false;
      if (reason === 'failed') {
        pair.candidate!.status = 'failed';
        pair.candidate!.resultsValid = false;
        pair.candidate!.replay = null;
        pair.comparison!.candidate.resultsValid = false;
      }
      expect(compatible(pair)).toBe(false);
      expect(
        summaryRows(pair, ids.cp).every((row) => row.delta === null && row.percent === null),
      ).toBe(true);
    },
  );
  it('refuses deltas between different actual observation windows even at the same minute', async () => {
    const right = candidateData();
    for (const row of right.samples) row.kpi!.windowStartMinute = 0;
    const pair = await pairFixture(replayData(), right);
    expect(summaryRows(pair, ids.cp)[0]).toMatchObject({
      delta: null,
      percent: null,
      reason: 'Ferestre de observație nealiniate',
    });
  });
});
describe('Bottleneck demo preset', () => {
  it('preserves layout resources and builds five flights, 600 passengers and a single staffed bottleneck', () => {
    const layout = bottleneckLayout(),
      original = JSON.stringify(layout),
      draft = makeDraft(layout, 'bottleneck', new Date('2026-10-17T08:00:00Z'));
    expect(draft.name).toBe('Bottleneck · DEMO');
    expect(draft.inputs.seed).toBe(42);
    expect(draft.inputs.durationMinutes).toBe(90);
    expect(draft.inputs.populations.reduce((sum, row) => sum + row.count, 0)).toBe(600);
    expect(draft.inputs.flights).toHaveLength(5);
    expect(draft.inputs.staffAllocations).toHaveLength(1);
    expect(
      draft.inputs.passengerGeneration!.batches.every(
        (row) =>
          row.checkpointId === ids.cp &&
          Math.abs(row.service.mean / layout.layout.checkpoints[0].capacityPerMinute - 1 / 3) <
            1e-12 &&
          row.arrival.maximum - row.arrival.minimum <= 35 &&
          row.arrival.maximum <= 75,
      ),
    ).toBe(true);
    expect(basicErrors(draft)).toEqual({});
    expect(JSON.stringify(layout)).toBe(original);
  });
  it('rejects insufficient staffing or a nonsynthetic layout instead of faking a valid demo', () => {
    const layout = bottleneckLayout();
    layout.layout.staff.pop();
    expect(() => makeDraft(layout, 'bottleneck')).toThrow('Bottleneck');
    const other = bottleneckLayout();
    other.layout.scene.isSynthetic = false;
    expect(() => makeDraft(other, 'bottleneck')).toThrow('Bottleneck');
  });
});

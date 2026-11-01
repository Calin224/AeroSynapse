import {
  BATCH_METRICS,
  RunMetricPage,
  duel,
  finalValues,
  formatValue,
  howOften,
  pastThreshold,
  spreadLanes,
  stability,
  valueKey,
} from './batch-insight';
import { BatchDetails, Statistics } from './batches.models';

const row = (id: string, value: number, recordedAtUtc: string, name = 'meanWaitMinutes') => ({
  id,
  zoneId: 'cp',
  name,
  unit: 'minutes',
  value,
  recordedAtUtc,
});
const page = (items: RunMetricPage['items'], isValid = true): RunMetricPage => ({
  runId: 'run',
  isValid,
  items,
});
const stats = (values: Partial<Statistics>): Statistics => ({
  count: 10,
  mean: 10,
  standardDeviation: 1,
  minimum: 8,
  maximum: 12,
  median: 10,
  p05: 9,
  p95: 11,
  ...values,
});
const wait = BATCH_METRICS[0];

function report(): BatchDetails {
  const replication = (variantId: string, seed: number, index: number, valid = true) => ({
    id: `${variantId}-${seed}`,
    variantId,
    seedIndex: index,
    seed,
    runId: `run-${variantId}-${seed}`,
    status: valid ? 'completed' : 'failed',
    runStatus: valid ? 'completed' : 'failed',
    resultsValid: valid,
    failureReason: null,
    remoteCancellationPending: false,
    provenance: {} as BatchDetails['replications'][number]['provenance'],
  });
  return {
    contractVersion: '1.0',
    isSynthetic: true,
    metricsTruncated: false,
    batch: {} as BatchDetails['batch'],
    seeds: [42, 43, 44],
    variants: [
      { id: 'a', position: 0, name: 'Start' },
      { id: 'b', position: 1, name: 'Plus' },
    ].map((variant) => ({
      ...variant,
      scenarioId: variant.id,
      scenarioRevision: 1,
      layoutVersionId: 'layout',
      engineVersion: 'flow-1.0',
    })),
    replications: [
      replication('a', 42, 0),
      replication('a', 43, 1),
      replication('a', 44, 2, false),
      replication('b', 42, 0),
      replication('b', 43, 1),
      replication('b', 44, 2),
    ],
    metrics: [
      {
        variantId: 'a',
        zoneId: 'cp',
        name: 'meanWaitMinutes',
        unit: 'minutes',
        values: stats({ count: 2 }),
        missingOrUnsuccessfulCount: 1,
      },
    ],
    comparisons: [
      {
        candidateVariantId: 'b',
        zoneId: 'cp',
        name: 'meanWaitMinutes',
        unit: 'minutes',
        differences: stats({ count: 2 }),
        includedSeeds: [42, 43],
        excludedSeeds: [{ seed: 44, reason: 'baseline_failed' }],
      },
    ],
  };
}

describe('batch insight', () => {
  it('keeps the latest recording of every metric, breaking ties by the smallest id', () => {
    const values = finalValues(
      page([
        row('b', 1, '2026-06-01T09:00:00Z'),
        row('c', 7, '2026-06-01T09:30:00.5Z'),
        row('a', 5, '2026-06-01T09:30:00.500Z'),
        row('d', 3, '2026-06-01T09:00:00Z', 'maximumQueue'),
      ]),
    );
    expect(values).toEqual({
      [valueKey('cp', 'meanWaitMinutes', 'minutes')]: 5,
      [valueKey('cp', 'maximumQueue', 'minutes')]: 3,
    });
  });

  it('refuses values from an invalid run or a page that may be cut short', () => {
    expect(finalValues(page([row('a', 1, '2026-06-01T09:00:00Z')], false))).toBeNull();
    const full = Array.from({ length: 100 }, (_, index) =>
      row(`r${index}`, index, '2026-06-01T09:00:00Z'),
    );
    expect(finalValues(page(full))).toBeNull();
    expect(finalValues(page(full.slice(1)))).not.toBeNull();
  });

  it('formats waits in minutes or seconds and counts as whole passengers', () => {
    expect(formatValue('minutes', 16.748)).toBe('16.75 min');
    expect(formatValue('minutes', 0.5)).toBe('30.00 s');
    expect(formatValue('passengers', 443.6)).toBe('444 pasageri');
    expect(formatValue('minutes', null)).toBe('—');
  });

  it('turns the realistic interval into a plain stability level', () => {
    expect(stability(null)).toBe('single');
    expect(stability(stats({ count: 1 }))).toBe('single');
    expect(stability(stats({ p05: 10, p95: 10 }))).toBe('stable');
    expect(stability(stats({ p05: 9.5, p95: 10.5 }))).toBe('stable');
    expect(stability(stats({ p05: 9, p95: 12 }))).toBe('moderate');
    expect(stability(stats({ p05: 5, p95: 15 }))).toBe('wide');
    expect(stability(stats({ median: 0, p05: 0, p95: 2 }))).toBe('wide');
  });

  it('says how often a threshold is crossed as a frequency a person can picture', () => {
    const points = [4, 8, 12, 16].map((value, index) => ({
      id: `${index}`,
      runId: `${index}`,
      day: index + 1,
      seed: index,
      value,
    }));
    expect(pastThreshold(points, 10, true)).toBe(2);
    expect(pastThreshold(points, 10, false)).toBe(2);
    expect(howOften(0, 0)).toBe('nicio zi simulată încă');
    expect(howOften(0, 10)).toBe('în niciuna din cele 10 zile');
    expect(howOften(10, 10)).toBe('în toate cele 10 zile');
    expect(howOften(3, 10)).toBe('în 3 din 10 zile · cam 1 zi din 3');
    expect(howOften(8, 10)).toBe('în 8 din 10 zile · cam 80% din zile');
  });

  it('places a dot only for valid runs whose value is known', () => {
    const key = valueKey('cp', 'meanWaitMinutes', 'minutes');
    const values = new Map([
      ['run-a-42', { [key]: 30 }],
      ['run-a-43', null],
      ['run-a-44', { [key]: 99 }],
      ['run-b-42', { [key]: 20 }],
    ]);
    const lanes = spreadLanes(report(), values, 'cp', 'meanWaitMinutes', 'minutes');
    expect(lanes.map((lane) => lane.points.map((point) => [point.day, point.value]))).toEqual([
      [[1, 30]],
      [[1, 20]],
    ]);
    expect(lanes[0].stats?.count).toBe(2);
    expect(lanes[1].stats).toBeNull();
  });

  it('compares day by day only on the seeds the server paired', () => {
    const key = valueKey('cp', 'meanWaitMinutes', 'minutes');
    const values = new Map([
      ['run-a-42', { [key]: 30 }],
      ['run-b-42', { [key]: 20 }],
      ['run-a-43', { [key]: 10 }],
      ['run-b-43', { [key]: 10 }],
      ['run-b-44', { [key]: 1 }],
    ]);
    expect(
      duel(report(), values, 'b', 'cp', wait, 'minutes').map((day) => [day.day, day.outcome]),
    ).toEqual([
      [1, 'better'],
      [2, 'same'],
      [3, 'missing'],
    ]);
    const processed = { ...wait, lowerIsBetter: false };
    expect(duel(report(), values, 'b', 'cp', processed, 'minutes')[0].outcome).toBe('worse');
  });
});

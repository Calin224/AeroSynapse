import { formatWait } from '../../shared/ui/outcome-cards';
import { BatchDetails, Statistics } from './batches.models';

/** One page of `GET runs/{id}/metrics`; the API returns at most this many rows. */
export const RUN_METRIC_LIMIT = 100;
export interface RunMetricPage {
  runId: string;
  isValid: boolean;
  items: {
    id: string;
    zoneId: string;
    name: string;
    unit: string;
    value: number;
    recordedAtUtc: string;
  }[];
}
/** Final value per zone, metric and unit; `null` when the run is invalid or the page may be cut short. */
export type RunValues = Record<string, number> | null;

export function valueKey(zoneId: string, name: string, unit: string) {
  return `${zoneId}|${name}|${unit}`;
}
/** The same "latest recording wins, then the smallest id" rule the batch summary applies. */
export function finalValues(page: RunMetricPage): RunValues {
  if (!page.isValid || page.items.length >= RUN_METRIC_LIMIT) return null;
  const latest = new Map<string, RunMetricPage['items'][number]>();
  for (const row of page.items) {
    const key = valueKey(row.zoneId, row.name, row.unit);
    const current = latest.get(key);
    const order = current ? Date.parse(row.recordedAtUtc) - Date.parse(current.recordedAtUtc) : 1;
    if (order > 0 || (order === 0 && row.id < current!.id)) latest.set(key, row);
  }
  return Object.fromEntries([...latest].map(([key, row]) => [key, row.value]));
}

export interface BatchMetric {
  name: string;
  label: string;
  /** Lower-case noun for sentences: "… reduce {noun} …". */
  noun: string;
  lowerIsBetter: boolean;
}
export const BATCH_METRICS: readonly BatchMetric[] = [
  {
    name: 'meanWaitMinutes',
    label: 'Așteptare medie',
    noun: 'așteptarea medie',
    lowerIsBetter: true,
  },
  {
    name: 'p95WaitMinutes',
    label: 'Cele mai lungi așteptări',
    noun: 'așteptarea celor mai ghinioniști 5 din 100 de pasageri',
    lowerIsBetter: true,
  },
  { name: 'maximumQueue', label: 'Coada maximă', noun: 'coada maximă', lowerIsBetter: true },
  {
    name: 'boardingAtRiskCount',
    label: 'Risc de zbor pierdut',
    noun: 'pasagerii care riscă să-și piardă zborul',
    lowerIsBetter: true,
  },
  {
    name: 'processedPassengers',
    label: 'Pasageri procesați',
    noun: 'pasagerii procesați',
    lowerIsBetter: false,
  },
];

export function formatValue(unit: string, value: number | null | undefined) {
  if (value == null || !Number.isFinite(value)) return '—';
  if (unit === 'minutes') return formatWait(value);
  if (unit === 'passengers') return `${Math.round(value)} pasageri`;
  return `${Number(value.toFixed(2))} ${unit}`;
}

export interface SpreadPoint {
  id: string;
  runId: string;
  day: number;
  seed: number;
  value: number;
}
export interface SpreadLane {
  variantId: string;
  name: string;
  /** 0 for the starting scenario, then 1… for each variant. */
  position: number;
  points: SpreadPoint[];
  stats: Statistics | null;
}
/** One lane per variant with a dot for every simulated day whose final value is known. */
export function spreadLanes(
  report: BatchDetails,
  values: ReadonlyMap<string, RunValues>,
  zoneId: string,
  name: string,
  unit: string,
): SpreadLane[] {
  const key = valueKey(zoneId, name, unit);
  return report.variants.map((variant) => ({
    variantId: variant.id,
    name: variant.name,
    position: variant.position,
    stats:
      report.metrics.find(
        (row) => row.variantId === variant.id && row.zoneId === zoneId && row.name === name,
      )?.values ?? null,
    points: report.replications
      .filter((row) => row.variantId === variant.id && row.resultsValid)
      .flatMap((row) => {
        const value = values.get(row.runId)?.[key];
        return value === undefined
          ? []
          : [{ id: row.id, runId: row.runId, day: row.seedIndex + 1, seed: row.seed, value }];
      }),
  }));
}

export type Stability = 'single' | 'stable' | 'moderate' | 'wide';
/** How far the realistic interval stretches compared with the typical value. */
export function stability(stats: Statistics | null): Stability {
  if (
    !stats ||
    stats.count < 2 ||
    stats.median === null ||
    stats.p05 === null ||
    stats.p95 === null
  )
    return 'single';
  const spread = stats.p95 - stats.p05;
  if (spread <= 0) return 'stable';
  if (stats.median === 0) return 'wide';
  const ratio = spread / Math.abs(stats.median);
  return ratio < 0.15 ? 'stable' : ratio < 0.4 ? 'moderate' : 'wide';
}

/** Days on the bad side of a threshold: above it when lower is better, below it otherwise. */
export function pastThreshold(points: SpreadPoint[], threshold: number, lowerIsBetter: boolean) {
  return points.filter((point) =>
    lowerIsBetter ? point.value > threshold : point.value < threshold,
  ).length;
}
/** "3 din 10 zile · cam 1 zi din 3" — a frequency a person can picture. */
export function howOften(count: number, total: number) {
  if (total === 0) return 'nicio zi simulată încă';
  if (count === 0) return `în niciuna din cele ${total} zile`;
  if (count === total) return `în toate cele ${total} zile`;
  const share = count / total;
  const picture =
    share <= 0.5
      ? `cam 1 zi din ${Math.round(1 / share)}`
      : `cam ${Math.round(share * 100)}% din zile`;
  return `în ${count} din ${total} zile · ${picture}`;
}

export type DayOutcome = 'better' | 'worse' | 'same' | 'missing';
export interface DuelDay {
  day: number;
  seed: number;
  outcome: DayOutcome;
  delta: number | null;
}
/**
 * The candidate against the starting scenario, day by day with the same arrivals.
 * Only seeds the server paired (same engine build, both valid) count.
 */
export function duel(
  report: BatchDetails,
  values: ReadonlyMap<string, RunValues>,
  candidateId: string,
  zoneId: string,
  metric: BatchMetric,
  unit: string,
): DuelDay[] {
  const baselineId = report.variants.find((row) => row.position === 0)?.id;
  const included = new Set(
    report.comparisons.find(
      (row) =>
        row.candidateVariantId === candidateId && row.zoneId === zoneId && row.name === metric.name,
    )?.includedSeeds ?? [],
  );
  const key = valueKey(zoneId, metric.name, unit);
  const value = (variantId: string | undefined, seed: number) => {
    const runId = report.replications.find(
      (row) => row.variantId === variantId && row.seed === seed,
    )?.runId;
    return runId ? values.get(runId)?.[key] : undefined;
  };
  return report.seeds.map((seed, index) => {
    const before = value(baselineId, seed),
      after = value(candidateId, seed);
    if (!included.has(seed) || before === undefined || after === undefined)
      return { day: index + 1, seed, outcome: 'missing', delta: null };
    const delta = after - before;
    const outcome: DayOutcome =
      Math.abs(delta) < 1e-9 ? 'same' : delta < 0 === metric.lowerIsBetter ? 'better' : 'worse';
    return { day: index + 1, seed, outcome, delta };
  });
}

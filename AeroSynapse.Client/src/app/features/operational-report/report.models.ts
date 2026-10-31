import type { RunComparison } from '../operations/comparison.models';
import type { PreparedReplay, ReplaySample } from '../replay/replay.models';

export interface ReportRun {
  id: string;
  status: string;
  resultsValid: boolean;
  failureReason: string | null;
  replay: PreparedReplay | null;
  message: string;
}
export interface ReportPair {
  baseline: ReportRun;
  candidate: ReportRun | null;
  comparison: RunComparison | null;
  comparisonError: string;
}
export const REPORT_METRICS = [
  { key: 'queuedPassengers', label: 'Pasageri în coadă', unit: 'pasageri', scale: 1 },
  { key: 'processedPassengers', label: 'Pasageri cu control încheiat', unit: 'pasageri', scale: 1 },
  {
    key: 'meanWaitMinutes',
    label: 'Timp mediu de așteptare',
    unit: 'minute',
    scale: 1,
  },
  {
    key: 'p95WaitMinutes',
    label: 'Timp de așteptare pentru 95% dintre pasageri (P95)',
    unit: 'minute',
    scale: 1,
  },
  { key: 'maximumQueue', label: 'Coada maximă observată', unit: 'pasageri', scale: 1 },
  {
    key: 'cumulativeThroughputPerMinute',
    label: 'Pasageri procesați pe minut · medie cumulată',
    unit: 'pasageri/min',
    scale: 1,
  },
  {
    key: 'cumulativeUtilization',
    label: 'Grad de utilizare · medie cumulată',
    unit: '%',
    scale: 100,
  },
  { key: 'boardingLateRisk', label: 'Risc depășire termen îmbarcare', unit: '%', scale: 100 },
] as const;
export type ReportMetric = (typeof REPORT_METRICS)[number];
export type MetricKey = ReportMetric['key'];
export const finite = (value: unknown): value is number =>
  typeof value === 'number' && Number.isFinite(value);
export const validRunId = (id: string) =>
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(id) &&
  id !== '00000000-0000-0000-0000-000000000000';
export function compatible(pair: ReportPair): boolean {
  const report = pair.comparison,
    right = pair.candidate;
  return (
    !!report &&
    !!right?.replay &&
    !!pair.baseline.replay &&
    pair.baseline.status === 'completed' &&
    pair.baseline.resultsValid &&
    right.status === 'completed' &&
    right.resultsValid &&
    right.id !== pair.baseline.id &&
    report.baseline.id === pair.baseline.id &&
    report.candidate.id === right.id &&
    report.baseline.status === 'completed' &&
    report.candidate.status === 'completed' &&
    report.baseline.resultsValid &&
    report.candidate.resultsValid &&
    report.versionsMatch === true &&
    report.comparability?.comparable === true &&
    report.comparability.populationMatches === true &&
    !report.comparability.issues.some((issue) => issue.severity === 'blocking')
  );
}
export function difference(before: number | null, after: number | null, allowed: boolean) {
  if (!allowed)
    return {
      delta: null,
      percent: null,
      reason: 'Rulări necomparabile sau compatibilitate neconfirmată',
    };
  if (!finite(before) || !finite(after))
    return { delta: null, percent: null, reason: 'Date lipsă' };
  const delta = after - before;
  if (!finite(delta))
    return { delta: null, percent: null, reason: 'Diferență în afara domeniului numeric' };
  if (before <= 0)
    return { delta, percent: null, reason: 'Procent indisponibil: baseline zero sau negativ' };
  const percent = (delta / before) * 100;
  return {
    delta,
    percent: finite(percent) ? percent : null,
    reason: finite(percent) ? '' : 'Procent în afara domeniului numeric',
  };
}
export function metricValue(
  sample: ReplaySample | null | undefined,
  metric: ReportMetric,
): number | null {
  if (!sample?.kpi) return null;
  const value = sample.kpi[metric.key];
  if (
    !finite(value) ||
    value < 0 ||
    (metric.scale === 100 && value > 1) ||
    (metric.unit === 'pasageri' && !Number.isInteger(value))
  )
    return null;
  return value;
}
export function reportZones(pair: ReportPair) {
  const zones = new Map<string, { id: string; label: string }>();
  for (const run of [pair.baseline, pair.candidate])
    for (const cp of run?.replay?.data.captured.layout.layout.checkpoints ?? [])
      zones.set(cp.id, { id: cp.id, label: `${cp.code} · ${cp.name}` });
  return [...zones.values()];
}
export function sampleAt(run: ReportRun | null, zoneId: string, minute: number) {
  return run?.replay?.byMinute.get(minute)?.get(zoneId) ?? null;
}
export function reportTimes(run: ReportRun | null): number[] {
  const replay = run?.replay;
  if (!replay) return [];
  const end = replay.data.captured.scenario.inputs.durationMinutes,
    interval = replay.data.intervalMinutes;
  return interval === null
    ? [...new Set([0, ...replay.minutes, end])].sort((a, b) => a - b)
    : [...Array.from({ length: Math.ceil(end / interval) }, (_, i) => i * interval), end];
}
function aligned(left: ReplaySample | null, right: ReplaySample | null): boolean {
  return (
    !!left?.kpi &&
    !!right?.kpi &&
    left.minute === right.minute &&
    Date.parse(left.recordedAtUtc) === Date.parse(right.recordedAtUtc) &&
    left.kpi.windowStartMinute === right.kpi.windowStartMinute
  );
}
function observedDifference(
  pair: ReportPair,
  left: ReplaySample | null,
  right: ReplaySample | null,
  metric: ReportMetric,
) {
  if (!pair.candidate) return { delta: null, percent: null, reason: 'Fără rulare După selectată' };
  const result = difference(
    metricValue(left, metric),
    metricValue(right, metric),
    compatible(pair),
  );
  return result.delta !== null && !aligned(left, right)
    ? { delta: null, percent: null, reason: 'Ferestre de observație nealiniate' }
    : result;
}
export function reportRow(pair: ReportPair, zone: string, metric: ReportMetric, minute: number) {
  const a = sampleAt(pair.baseline, zone, minute),
    b = sampleAt(pair.candidate, zone, minute);
  const before = metricValue(a, metric),
    after = metricValue(b, metric);
  return {
    minute,
    before,
    after,
    ...observedDifference(pair, a, b, metric),
    beforeCohort: a?.kpi?.waitSampleCount ?? null,
    afterCohort: b?.kpi?.waitSampleCount ?? null,
  };
}
export function summaryRows(pair: ReportPair, zone: string) {
  const a = pair.baseline.replay?.data.captured.scenario.inputs.durationMinutes;
  const b = pair.candidate?.replay?.data.captured.scenario.inputs.durationMinutes;
  return REPORT_METRICS.map((metric) => {
    const left = a == null ? null : sampleAt(pair.baseline, zone, a),
      right = b == null ? null : sampleAt(pair.candidate, zone, b);
    const before = metricValue(left, metric),
      after = metricValue(right, metric);
    return { metric, before, after, ...observedDifference(pair, left, right, metric) };
  });
}
export function temporalRows(pair: ReportPair, zone: string, metric: ReportMetric) {
  const minutes = [
    ...new Set([...reportTimes(pair.baseline), ...reportTimes(pair.candidate)]),
  ].sort((a, b) => a - b);
  return minutes.map((minute) => reportRow(pair, zone, metric, minute));
}
export function reportChart(pair: ReportPair, zone: string, metric: ReportMetric) {
  const runs = [pair.baseline, ...(pair.candidate ? [pair.candidate] : [])];
  const series = runs.map((run) => ({
    run,
    points: reportTimes(run).map((minute) => ({
      minute,
      value: metricValue(sampleAt(run, zone, minute), metric),
    })),
  }));
  const horizon = Math.max(
    1,
    ...runs.map((run) => run.replay?.data.captured.scenario.inputs.durationMinutes ?? 0),
  );
  let maximum = 1;
  for (const set of series)
    for (const point of set.points)
      if (point.value !== null) maximum = Math.max(maximum, point.value * metric.scale);
  return {
    horizon,
    maximum,
    series: series.map(({ run, points }) => {
      let path = '',
        markers = '',
        previous: number | undefined;
      for (const point of points) {
        if (point.value === null) {
          previous = undefined;
          continue;
        }
        const x = (50 + (point.minute / horizon) * 640).toFixed(3),
          y = (200 - ((point.value * metric.scale) / maximum) * 170).toFixed(3);
        const interval = run.replay?.data.intervalMinutes;
        const continuous =
          previous !== undefined && interval != null && point.minute - previous <= interval;
        path += `${continuous ? 'L' : 'M'}${x},${y} `;
        markers += `M${x},${y}h0.1 `;
        previous = point.minute;
      }
      return {
        runId: run.id,
        path,
        markers,
        observed: points.filter((p) => p.value !== null).length,
        missing: points.filter((p) => p.value === null).length,
      };
    }),
  };
}

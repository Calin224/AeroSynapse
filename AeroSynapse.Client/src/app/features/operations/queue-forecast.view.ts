import { ProgressZone } from '../../core/http/operations-stream';
import { QueueForecastResult, QueueForecastSeries } from './queue-forecast.models';

/** An observed queue length (live progress or captured KPI snapshot); never a forecast value. */
export interface QueueSample { minute: number; queue: number }
export type QueueHistory = Record<string, QueueSample[]>;
export type Risk = 'breach' | 'near' | 'clear' | 'unbounded';

export interface CheckpointOutlook {
  id: string;
  series: QueueForecastSeries;
  /** Observations up to and including the forecast origin. */
  observed: QueueSample[];
  /** Snapshots captured after the origin; shown only to evaluate a historical forecast, never used as input. */
  actual: QueueSample[];
  forecast: QueueSample[];
  peak: QueueSample;
  end: number;
  arrivals: number;
  risk: Risk;
  breachInMinutes: number | null;
  mitigated: QueueSample[] | null;
  backtestError: number | null;
}

const NEAR_THRESHOLD = 0.8;
const MAX_HISTORY_SAMPLES = 2000;

export function withObservation(history: QueueHistory, minute: number, zones: ProgressZone[]): QueueHistory {
  const next = { ...history };
  for (const zone of zones) {
    if (zone.queuedPassengers === null) continue;
    const samples = (next[zone.zoneId] ?? []).filter(sample => sample.minute !== minute);
    samples.push({ minute, queue: zone.queuedPassengers });
    samples.sort((a, b) => a.minute - b.minute);
    next[zone.zoneId] = samples.slice(-MAX_HISTORY_SAMPLES);
  }
  return next;
}

export function fromSnapshots(frames: { zoneId: string; minute: number; queuedPassengers: number }[]): QueueHistory {
  const history: QueueHistory = {};
  for (const frame of frames) (history[frame.zoneId] ??= []).push({ minute: frame.minute, queue: frame.queuedPassengers });
  for (const samples of Object.values(history)) samples.sort((a, b) => a.minute - b.minute);
  return history;
}

/** Re-applies the published recurrence with extra operators on staffed lanes, for drawing the service's mitigation. */
export function projectWithExtraStaff(series: QueueForecastSeries, extra: number): QueueSample[] {
  let queue = series.initialQueuePassengers;
  return series.points.map(point => {
    const capacity = point.availableStaff > 0 ? (point.availableStaff + extra) * series.serviceRatePerStaffMinute : 0;
    queue = Math.max(0, queue + point.arrivals - capacity);
    return { minute: point.minute, queue };
  });
}

export function outlooks(result: QueueForecastResult, history: QueueHistory): CheckpointOutlook[] {
  const threshold = result.queueThresholdPassengers, from = result.fromMinute, to = result.toMinute;
  const horizon = to - from;
  const rows = result.checkpoints.map((series): CheckpointOutlook => {
    const samples = history[series.checkpointId] ?? [];
    const observed = [...samples.filter(sample => sample.minute >= from - horizon && sample.minute < from),
      { minute: from, queue: series.initialQueuePassengers }];
    const actual = result.observationSource === 'captured_snapshot'
      ? samples.filter(sample => sample.minute > from && sample.minute <= to) : [];
    const forecast = series.points.map(point => ({ minute: point.minute, queue: point.queuePassengers }));
    const peak = forecast.reduce((best, sample) => sample.queue > best.queue ? sample : best, forecast[0]);
    const extra = series.mitigation?.extraStaff ?? 0;
    const mitigated = extra > 0 ? projectWithExtraStaff(series, extra) : null;
    const consistent = !mitigated || Math.abs(Math.max(...mitigated.map(sample => sample.queue)) - (series.mitigation?.peakQueuePassengers ?? NaN)) < 1e-9;
    const errors = actual.map(sample => Math.abs(sample.queue - (forecast.find(point => point.minute === sample.minute)?.queue ?? NaN)))
      .filter(Number.isFinite);
    return {
      id: series.checkpointId, series, observed, actual, forecast, peak,
      end: forecast[forecast.length - 1].queue,
      arrivals: series.points.reduce((sum, point) => sum + point.arrivals, 0),
      risk: threshold === null ? 'unbounded' : series.alert ? 'breach' : peak.queue > threshold * NEAR_THRESHOLD ? 'near' : 'clear',
      breachInMinutes: series.alert ? series.alert.minute - from : null,
      mitigated: consistent ? mitigated : null,
      backtestError: errors.length ? errors.reduce((sum, value) => sum + value, 0) / errors.length : null,
    };
  });
  const order: Record<Risk, number> = { breach: 0, near: 1, clear: 2, unbounded: 3 };
  return rows.sort((a, b) => order[a.risk] - order[b.risk]
    || (a.breachInMinutes ?? Infinity) - (b.breachInMinutes ?? Infinity) || b.peak.queue - a.peak.queue);
}

export function riskLabel(view: CheckpointOutlook) {
  switch (view.risk) {
    case 'breach': return view.breachInMinutes === 1 ? 'Depășire în 1 minut' : `Depășire în ${view.breachInMinutes} minute`;
    case 'near': return 'Aproape de prag';
    case 'clear': return 'Sub prag';
    default: return view.end > view.series.initialQueuePassengers + 0.5 ? 'Coada crește'
      : view.end < view.series.initialQueuePassengers - 0.5 ? 'Coada scade' : 'Stabil';
  }
}

export interface Tick { position: number; label: string }
export interface Box { width: number; height: number; left: number; right: number; top: number; bottom: number }

export function niceScale(value: number, divisions = 4) {
  const raw = Math.max(value, 1) / divisions, power = 10 ** Math.floor(Math.log10(raw));
  const step = [1, 2, 2.5, 5, 10].map(factor => factor * power).find(candidate => candidate >= raw)!;
  return { max: step * Math.ceil(Math.max(value, 1) / step), step };
}

function scales(box: Box, start: number, end: number, max: number) {
  const x = (minute: number) => box.left + (minute - start) / Math.max(end - start, 1) * (box.width - box.left - box.right);
  const y = (value: number) => box.height - box.bottom - value / max * (box.height - box.top - box.bottom);
  return { x, y };
}

function line(samples: QueueSample[], x: (m: number) => number, y: (v: number) => number) {
  return samples.map((sample, index) => `${index ? 'L' : 'M'}${x(sample.minute).toFixed(1)},${y(sample.queue).toFixed(1)}`).join(' ');
}

function area(samples: QueueSample[], x: (m: number) => number, y: (v: number) => number) {
  if (samples.length < 2) return '';
  const base = y(0).toFixed(1);
  return `${line(samples, x, y)} L${x(samples[samples.length - 1].minute).toFixed(1)},${base} L${x(samples[0].minute).toFixed(1)},${base} Z`;
}

export const MAIN_BOX: Box = { width: 760, height: 280, left: 48, right: 24, top: 34, bottom: 30 };
export const FLOW_BOX: Box = { width: 760, height: 96, left: 48, right: 24, top: 12, bottom: 22 };
export const SPARK_BOX: Box = { width: 220, height: 56, left: 2, right: 2, top: 6, bottom: 4 };

export function queueChart(view: CheckpointOutlook, result: QueueForecastResult, box = MAIN_BOX) {
  const threshold = result.queueThresholdPassengers, from = result.fromMinute, to = result.toMinute;
  const start = Math.min(from, view.observed[0]?.minute ?? from);
  const values = [...view.observed, ...view.forecast, ...view.actual, ...(view.mitigated ?? [])].map(sample => sample.queue);
  const { max, step } = niceScale(Math.max(...values, threshold === null ? 0 : threshold * 1.15));
  const { x, y } = scales(box, start, to, max);
  const forecast = [{ minute: from, queue: view.series.initialQueuePassengers }, ...view.forecast];
  const minuteStep = to - start > 40 ? 10 : 5;
  const xTicks: Tick[] = [];
  for (let minute = Math.ceil(start / minuteStep) * minuteStep; minute <= to; minute += minuteStep) xTicks.push({ position: x(minute), label: `${minute}` });
  const yTicks: Tick[] = [];
  for (let value = 0; value <= max + 1e-9; value += step) yTicks.push({ position: y(value), label: `${+value.toFixed(1)}` });
  const alert = view.series.alert;
  return {
    box, start, from, to, xTicks, yTicks,
    nowX: x(from), endX: x(to), top: box.top, bottom: box.height - box.bottom, left: box.left, right: box.width - box.right,
    observedLine: line(view.observed, x, y), observedArea: area(view.observed, x, y),
    forecastLine: line(forecast, x, y), forecastArea: area(forecast, x, y),
    mitigatedLine: view.mitigated ? line([forecast[0], ...view.mitigated], x, y) : '',
    thresholdY: threshold === null ? null : y(threshold),
    origin: { x: x(from), y: y(view.series.initialQueuePassengers) },
    peak: { x: x(view.peak.minute), y: y(view.peak.queue) },
    alert: alert ? { x: x(alert.minute), y: y(alert.queuePassengers) } : null,
    actual: view.actual.map(sample => ({ ...sample, x: x(sample.minute), y: y(sample.queue) })),
    x, y,
  };
}

/** Per-minute flow in the forecast intervals (t-1, t]: arrival bars against the staffed capacity step. */
export function flowChart(series: QueueForecastSeries, start: number, to: number, box = FLOW_BOX) {
  const { max } = niceScale(Math.max(...series.points.flatMap(point => [point.arrivals, point.capacity])), 2);
  const { x, y } = scales(box, start, to, max);
  const bars = series.points.map(point => {
    const left = x(point.minute - 1), right = x(point.minute), top = y(point.arrivals);
    return { minute: point.minute, x: left + 1, width: Math.max(1, right - left - 2), y: top, height: y(0) - top,
      surplus: point.arrivals > point.capacity };
  });
  const capacity = series.points.map((point, index) =>
    `${index ? 'L' : 'M'}${x(point.minute - 1).toFixed(1)},${y(point.capacity).toFixed(1)} L${x(point.minute).toFixed(1)},${y(point.capacity).toFixed(1)}`).join(' ');
  return { box, bars, capacity, max, baseline: y(0), top: y(max) };
}

export function sparkline(view: CheckpointOutlook, result: QueueForecastResult, box = SPARK_BOX) {
  const chart = queueChart(view, result, box);
  return { box, observed: chart.observedLine, forecast: chart.forecastLine, area: chart.forecastArea, thresholdY: chart.thresholdY, nowX: chart.nowX };
}

function totalQueue(history: QueueHistory): QueueSample[] {
  const totals = new Map<number, number>();
  for (const samples of Object.values(history)) for (const sample of samples) totals.set(sample.minute, (totals.get(sample.minute) ?? 0) + sample.queue);
  return [...totals].map(([minute, queue]) => ({ minute, queue })).sort((a, b) => a.minute - b.minute);
}

/** The observed minute with the largest total queue across checkpoints, or null without observations. */
export function busiestMinute(history: QueueHistory) {
  return totalQueue(history).reduce<QueueSample | null>((best, sample) => !best || sample.queue > best.queue ? sample : best, null)?.minute ?? null;
}

/** Whole-run context: total observed queue across checkpoints, on which the forecast window is marked. */
export function runTimeline(history: QueueHistory, duration: number, box: Box = { width: 760, height: 64, left: 4, right: 4, top: 6, bottom: 6 }) {
  const samples = totalQueue(history);
  const { max } = niceScale(Math.max(0, ...samples.map(sample => sample.queue)), 1);
  const { x, y } = scales(box, 0, duration, max);
  return { box, line: line(samples, x, y), area: area(samples, x, y), x };
}

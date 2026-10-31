import type { HeatmapRun } from '../digital-twin/heatmap-model';
import type { ScenarioInputs } from '../operations/operations.models';

/** A queue whose estimated wait reaches this many minutes counts as a bottleneck. */
export const WAIT_LIMIT_MINUTES = 5;
/** The most extra operators a single recommendation may ask for. */
const MAX_EXTRA_STAFF = 10;

export interface Bottleneck {
  zoneId: string;
  label: string;
  /** Minutes from the scenario start: from the queue starting to build until it is back under the limit. */
  fromMinute: number;
  toMinute: number;
  peakMinute: number;
  peakQueue: number;
  /** Infinity when passengers wait at a checkpoint nobody staffs. */
  peakWaitMinutes: number;
  arrivalsPerMinute: number;
  capacityPerMinute: number;
  staff: number;
  /** Other checkpoints nobody staffs during the window, where part of the flow could go instead. */
  closed: string[];
  /** Null when even MAX_EXTRA_STAFF more operators would not keep the wait under the limit. */
  fix: {
    extraStaff: number;
    /** Checkpoints that can lend operators for the window and still stay under the limit. */
    donors: { zoneId: string; label: string; count: number }[];
    expectedPeakWaitMinutes: number;
  } | null;
}
export interface SpareCapacity {
  zoneId: string;
  label: string;
  staff: number;
  peakWaitMinutes: number;
}
export interface Diagnosis {
  bottlenecks: Bottleneck[];
  /** Checkpoints that keep the wait under the limit with one operator fewer all run. */
  spare: SpareCapacity[];
  /** The longest estimated wait anywhere, for the all-clear message. */
  worst: { label: string; waitMinutes: number } | null;
}

interface Zone {
  zoneId: string;
  label: string;
  /** False for check-in desks, whose staff are not security operators. */
  checkpoint: boolean;
  rate: number;
  minutes: number[];
  queued: number[];
  /** Passengers who joined the queue during the interval ending at each sample. */
  arrivals: number[];
  staff: number[];
}

/** People on duty at a checkpoint at a minute: the shift plan when there is one, else the fixed allocation. */
function staffAt(inputs: ScenarioInputs, zoneId: string, fixed: number, minute: number) {
  const plan = inputs.staffShifts;
  if (!plan) return fixed;
  return plan.shifts.filter((shift) =>
    shift.segments.some(
      (segment) =>
        segment.checkpointId === zoneId &&
        segment.startMinute <= minute &&
        minute < segment.endMinute,
    ),
  ).length;
}

function zones(heatmap: HeatmapRun, inputs: ScenarioInputs): Zone[] {
  return heatmap.capacities.flatMap((capacity) => {
    const rows = heatmap.snapshots
      .filter((row) => row.zoneId === capacity.zoneId)
      .sort((a, b) => a.elapsedMinutes - b.elapsedMinutes);
    if (rows.length < 2 || !(capacity.serviceRatePerMinute > 0)) return [];
    const checkpoint = capacity.zoneType !== 'checkIn';
    return [
      {
        zoneId: capacity.zoneId,
        label: capacity.label,
        checkpoint,
        rate: capacity.serviceRatePerMinute,
        minutes: rows.map((row) => row.elapsedMinutes),
        queued: rows.map((row) => row.queuedPassengers),
        arrivals: rows.map((row, i) =>
          i === 0
            ? 0
            : Math.max(
                0,
                row.processedPassengers -
                  rows[i - 1].processedPassengers +
                  row.queuedPassengers -
                  rows[i - 1].queuedPassengers,
              ),
        ),
        staff: rows.map((row) =>
          checkpoint
            ? staffAt(inputs, capacity.zoneId, capacity.staffCount, row.elapsedMinutes)
            : capacity.staffCount,
        ),
      },
    ];
  });
}

/**
 * Replays the observed arrivals through a fluid queue with `extra` operators between `from` and
 * `to` (negative takes them away) and returns the estimated wait at each sample. Both sides of a
 * comparison go through the same model, so the difference is the staffing change alone.
 */
function waits(zone: Zone, extra = 0, from = -Infinity, to = Infinity) {
  const staff = (i: number) =>
    Math.max(0, zone.staff[i] + (zone.minutes[i] >= from && zone.minutes[i] <= to ? extra : 0));
  let queue = zone.queued[0];
  return zone.minutes.map((minute, i) => {
    if (i > 0)
      queue = Math.max(
        0,
        queue + zone.arrivals[i] - staff(i - 1) * zone.rate * (minute - zone.minutes[i - 1]),
      );
    const capacity = staff(i) * zone.rate;
    return queue < 1 ? 0 : capacity > 0 ? queue / capacity : Infinity;
  });
}

function observedWaits(zone: Zone) {
  return zone.queued.map((queue, i) => {
    const capacity = zone.staff[i] * zone.rate;
    return queue <= 0 ? 0 : capacity > 0 ? queue / capacity : Infinity;
  });
}

/** Runs of consecutive samples at or over the limit, as [first, last] sample indexes. */
function windows(wait: number[]) {
  const found: { start: number; end: number }[] = [];
  wait.forEach((value, i) => {
    if (value < WAIT_LIMIT_MINUTES) return;
    const last = found.at(-1);
    if (last && last.end === i - 1) last.end = i;
    else found.push({ start: i, end: i });
  });
  return found;
}

/** How many operators `zone` can lend between `from` and `to`, up to `wanted`, keeping one on duty and its wait under the limit. */
function lendable(zone: Zone, from: number, to: number, wanted: number) {
  const onDuty = zone.staff.filter((_, i) => zone.minutes[i] >= from && zone.minutes[i] <= to);
  let count = 0;
  while (
    count < wanted &&
    Math.min(...onDuty) > count + 1 &&
    Math.max(...waits(zone, -(count + 1), from, to)) < WAIT_LIMIT_MINUTES
  )
    count++;
  return count;
}

function bottleneck(
  zone: Zone,
  all: Zone[],
  wait: number[],
  window: { start: number; end: number },
): Bottleneck {
  // The queue starts building at the last empty sample before it crosses the limit.
  let start = window.start;
  while (start > 0 && zone.queued[start - 1] > 0) start--;
  start = Math.max(0, start - 1);
  const last = Math.min(window.end + 1, zone.minutes.length - 1);
  let peak = window.start;
  for (let i = window.start; i <= window.end; i++) if (wait[i] > wait[peak]) peak = i;
  const from = zone.minutes[start],
    to = zone.minutes[last];
  const arrived = zone.arrivals.slice(start + 1, peak + 1).reduce((sum, n) => sum + n, 0);
  const staff = Math.max(...zone.staff.slice(start, peak + 1));
  let fix: Bottleneck['fix'] = null;
  for (let extra = 1; extra <= MAX_EXTRA_STAFF && !fix; extra++) {
    const expected = Math.max(...waits(zone, extra, from, to).slice(start, last + 1));
    if (expected < WAIT_LIMIT_MINUTES)
      fix = { extraStaff: extra, donors: [], expectedPeakWaitMinutes: expected };
  }
  const others = zone.checkpoint ? all.filter((other) => other !== zone && other.checkpoint) : [];
  let needed = fix?.extraStaff ?? 0;
  for (const other of others) {
    if (!needed) break;
    const count = lendable(other, from, to, needed);
    if (count) {
      fix!.donors.push({ zoneId: other.zoneId, label: other.label, count });
      needed -= count;
    }
  }
  return {
    zoneId: zone.zoneId,
    label: zone.label,
    fromMinute: from,
    toMinute: to,
    peakMinute: zone.minutes[peak],
    peakQueue: zone.queued[peak],
    peakWaitMinutes: wait[peak],
    arrivalsPerMinute: arrived / Math.max(zone.minutes[peak] - from, 1),
    capacityPerMinute: staff * zone.rate,
    staff,
    closed: others
      .filter((other) =>
        other.staff.every(
          (count, i) => count === 0 || other.minutes[i] < from || other.minutes[i] > to,
        ),
      )
      .map((other) => other.label),
    fix,
  };
}

/** Where queues outgrow their operators in a finished run, and the smallest staffing change that keeps every wait under the limit. */
export function diagnose(heatmap: HeatmapRun | null, inputs: ScenarioInputs | null): Diagnosis {
  if (!heatmap || !inputs) return { bottlenecks: [], spare: [], worst: null };
  const all = zones(heatmap, inputs);
  const observed = new Map(all.map((zone) => [zone, observedWaits(zone)]));
  const bottlenecks = all
    .flatMap((zone) =>
      windows(observed.get(zone)!).map((window) =>
        bottleneck(zone, all, observed.get(zone)!, window),
      ),
    )
    .sort((a, b) => b.peakWaitMinutes - a.peakWaitMinutes);
  const spare = all.flatMap((zone): SpareCapacity[] => {
    if (Math.min(...zone.staff) < 2 || bottlenecks.some((row) => row.zoneId === zone.zoneId))
      return [];
    const fewer = Math.max(...waits(zone, -1));
    return fewer < WAIT_LIMIT_MINUTES
      ? [
          {
            zoneId: zone.zoneId,
            label: zone.label,
            staff: Math.max(...zone.staff),
            peakWaitMinutes: fewer,
          },
        ]
      : [];
  });
  const worst = all.reduce<Diagnosis['worst']>((best, zone) => {
    const value = Math.max(...observed.get(zone)!);
    return !best || value > best.waitMinutes ? { label: zone.label, waitMinutes: value } : best;
  }, null);
  return { bottlenecks, spare, worst };
}

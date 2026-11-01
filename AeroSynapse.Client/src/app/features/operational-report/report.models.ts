import type { RunComparison } from '../operations/comparison.models';
import type { KpiFrame } from '../operations/kpi.models';
import type { PreparedReplay } from '../replay/replay.models';

export const validRunId = (id: string) =>
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(id) &&
  id !== '00000000-0000-0000-0000-000000000000';

/** A run as the report sees it: its verified replay, or why there is none. */
export interface ReportRun {
  id: string;
  status: string;
  /** Plain-language reason the run has no data; empty when `replay` is present. */
  problem: string;
  replay: PreparedReplay | null;
}

export interface CheckpointResult {
  id: string;
  label: string;
  /** The KPI recorded at the end of the simulated period; null when that snapshot is missing. */
  kpi: KpiFrame | null;
}

export interface Totals {
  passengers: number;
  processed: number;
  meanWait: number | null;
  /** The checkpoint whose 95th-percentile wait was the longest. */
  worst: { label: string; p95: number } | null;
  late: number;
  /** Passengers who had a boarding deadline; 0 means the run has no boarding data. */
  lateOf: number;
  /** Share of time the busiest checkpoint's staff were busy, 0–1; null when not recorded. */
  busiest: number | null;
}

/**
 * Below this, staff sat idle most of the day: nobody queues, so a wait of seconds says nothing about a
 * real peak and the report shows "no queue" instead of a figure that only looks precise.
 */
export const LOW_LOAD = 0.2;
// A short peak in a long quiet day also averages low, so a real wait (p95 of a minute or more) keeps the figures.
export const lightLoad = (run: Totals) =>
  run.busiest !== null && run.busiest < LOW_LOAD && (run.worst?.p95 ?? 0) < 1;
const busyShare = (value: number) => `${Math.ceil(value * 100)}%`;

export interface KpiCard {
  key: string;
  label: string;
  value: string;
  /** The same figure for the comparison run; empty without one. */
  compared: string;
  meaning: string;
}

export interface QueuePoint {
  minute: number;
  value: number | null;
}

const numbers = new Intl.NumberFormat('ro-RO', { maximumFractionDigits: 1 });
export const num = (value: number) => numbers.format(value);
/** A wait a person can read: seconds under a minute ("0 min" reads as broken), minutes otherwise. */
export const minutes = (value: number) =>
  value >= 1 ? `${num(value)} min` : value * 60 < 1 ? 'sub 1 s' : `${Math.round(value * 60)} s`;
const NO_DATA = 'Fără date';

/** "5 pasageri", "20 de pasageri": Romanian adds "de" when the last two digits are 00 or 20–99. */
export function people(count: number) {
  const rest = count % 100;
  if (count === 1) return '1 pasager';
  return `${num(count)} ${count >= 20 && (rest === 0 || rest >= 20) ? 'de ' : ''}pasageri`;
}

export function checkpointResults(replay: PreparedReplay): CheckpointResult[] {
  const end = replay.byMinute.get(replay.data.captured.scenario.inputs.durationMinutes);
  return replay.data.captured.layout.layout.checkpoints.map((cp) => ({
    id: cp.id,
    label: `${cp.code} · ${cp.name}`,
    kpi: end?.get(cp.id)?.kpi ?? null,
  }));
}

/** Whole-run figures from every checkpoint's final KPI; null when any checkpoint lacks one. */
export function totals(results: CheckpointResult[]): Totals | null {
  if (!results.length || results.some((row) => !row.kpi)) return null;
  const kpis = results.map((row) => row.kpi!);
  const sum = (pick: (kpi: KpiFrame) => number) =>
    kpis.reduce((total, kpi) => total + pick(kpi), 0);
  const samples = sum((kpi) => kpi.waitSampleCount);
  const unknownMean = kpis.some((kpi) => kpi.waitSampleCount > 0 && kpi.meanWaitMinutes === null);
  const worst = results
    .filter((row) => row.kpi!.p95WaitMinutes !== null)
    .sort((a, b) => b.kpi!.p95WaitMinutes! - a.kpi!.p95WaitMinutes!)[0];
  return {
    passengers: sum((kpi) => kpi.populationCount),
    processed: sum((kpi) => kpi.processedPassengers),
    // Each passenger belongs to one checkpoint, so the sample-weighted mean is the run's mean.
    meanWait:
      !samples || unknownMean
        ? null
        : sum((kpi) => (kpi.meanWaitMinutes ?? 0) * kpi.waitSampleCount) / samples,
    worst: worst ? { label: worst.label, p95: worst.kpi!.p95WaitMinutes! } : null,
    late: sum((kpi) => kpi.boardingAtRiskCount),
    lateOf: sum((kpi) => kpi.boardingRiskPopulationCount),
    busiest: kpis.some((kpi) => kpi.cumulativeUtilization === null)
      ? null
      : Math.max(...kpis.map((kpi) => kpi.cumulativeUtilization!)),
  };
}

/** One to three short sentences, built only from the run's recorded figures. */
export function summary(run: Totals | null, checkpoints: number): string[] {
  if (!run)
    return [
      'Lipsesc rezultatele de final pentru cel puțin un punct de control, așa că nu putem rezuma rularea.',
    ];
  if (!run.passengers) return ['Simularea nu a avut pasageri.'];
  const share = Math.floor((run.processed / run.passengers) * 100);
  const light = lightLoad(run);
  const late = !run.lateOf
    ? '; nu avem date despre întârzieri la îmbarcare.'
    : run.late
      ? ` și ${num(run.late)} au ratat îmbarcarea${light ? ', deși aproape nimeni nu a stat la coadă' : ''}.`
      : ' și niciunul nu a ratat îmbarcarea.';
  const lines = [
    `Din ${people(run.passengers)}, ${num(run.processed)} au trecut de control (${share}%)${late}`,
  ];
  if (!run.processed) {
    lines.push('Niciun pasager nu a terminat controlul, deci nu avem timpi de așteptare.');
    return lines;
  }
  if (light) {
    lines.push(
      `Nu s-a format coadă: operatorii au fost ocupați cel mult ${busyShare(run.busiest!)} din timp, deci în scenariu sunt mult mai puțini pasageri decât poate controla personalul.`,
      'Nu afișăm timpi de așteptare: la o încărcare atât de mică nu spun nimic despre un vârf real de trafic.',
    );
    return lines;
  }
  if (run.meanWait === null || !run.worst) {
    lines.push('Lipsesc timpii de așteptare pentru această rulare.');
    return lines;
  }
  lines.push(`Așteptarea medie la control a fost de ${minutes(run.meanWait)}.`);
  const peak = `95% dintre pasageri au așteptat cel mult ${minutes(run.worst.p95)}`;
  lines.push(
    checkpoints > 1
      ? `Cel mai aglomerat punct de control a fost ${run.worst.label}: acolo, ${peak}.`
      : `${peak}.`,
  );
  return lines;
}

export type ReportTone = 'good' | 'warning' | 'critical' | 'unknown';

/** The one headline a manager reads first; the bands are this page's own reading aid, not a standard. */
export function verdict(run: Totals | null): { tone: ReportTone; headline: string } {
  if (!run || !run.passengers)
    return { tone: 'unknown', headline: 'Nu avem destule date pentru o concluzie.' };
  const unfinished = run.passengers - run.processed,
    late = run.lateOf ? run.late : 0;
  const share = Math.max(late / (run.lateOf || 1), unfinished / run.passengers);
  const tone: ReportTone =
    share >= 0.1
      ? 'critical'
      : late || unfinished || (run.worst?.p95 ?? 0) >= 10
        ? 'warning'
        : 'good';
  if (lightLoad(run))
    return {
      tone: 'unknown',
      headline: 'Personalul a fost mult peste cerere, așa că simularea nu arată un vârf real.',
    };
  const headline = late
    ? `${people(late)} au ratat îmbarcarea.`
    : unfinished
      ? `${people(unfinished)} nu au terminat controlul până la final.`
      : tone === 'warning'
        ? `Cozi lungi la ${run.worst!.label}.`
        : 'Operațiune fluentă: toți pasagerii au trecut de control la timp.';
  return { tone, headline };
}

/** One row per checkpoint, with bar widths relative to the busiest checkpoint. */
export function checkpointRows(results: CheckpointResult[]) {
  const top = (pick: (kpi: KpiFrame) => number | null) =>
    Math.max(0, ...results.map((row) => (row.kpi ? (pick(row.kpi) ?? 0) : 0)));
  const queueTop = top((kpi) => kpi.maximumQueue),
    waitTop = top((kpi) => kpi.meanWaitMinutes);
  return results.map((row) => {
    const busy = row.kpi?.cumulativeUtilization ?? null;
    return {
      ...row,
      busy: busy === null ? '—' : busyShare(busy),
      // Same rule as the whole run: an idle checkpoint shows "no queue", not seconds.
      light: busy !== null && busy < LOW_LOAD && (row.kpi?.p95WaitMinutes ?? 0) < 1,
      queueWidth: row.kpi && queueTop ? (row.kpi.maximumQueue / queueTop) * 100 : 0,
      waitWidth:
        row.kpi?.meanWaitMinutes && waitTop ? (row.kpi.meanWaitMinutes / waitTop) * 100 : 0,
    };
  });
}

function versus(
  now: number | null,
  then: number | null,
  show: (value: number) => string,
  more = 'mai mult',
  less = 'mai puțin',
) {
  if (then === null) return `Rularea de comparat: ${NO_DATA.toLowerCase()}`;
  const diff = now === null ? null : now - then;
  const change =
    diff === null
      ? ''
      : Math.abs(diff) < 0.05
        ? ' (la fel)'
        : ` (acum cu ${show(Math.abs(diff))} ${diff > 0 ? more : less})`;
  return `Rularea de comparat: ${show(then)}${change}`;
}

export function kpiCards(run: Totals, other: Totals | null = null): KpiCard[] {
  const lateNow = run.lateOf ? run.late : null,
    lateThen = other?.lateOf ? other.late : null;
  const light = lightLoad(run);
  const idle = light
    ? `Operatorii au fost ocupați cel mult ${busyShare(run.busiest!)} din timp, deci nu s-a format coadă; un timp de așteptare nu ar spune nimic aici.`
    : '';
  return [
    {
      key: 'processed',
      label: 'Au trecut de control',
      value: `${num(run.processed)} din ${num(run.passengers)}`,
      compared: other ? versus(run.processed, other.processed, num, 'mai mulți', 'mai puțini') : '',
      meaning: 'Pasagerii care au terminat controlul până la finalul simulării.',
    },
    {
      key: 'meanWait',
      label: 'Așteptare medie',
      value: light ? 'Fără coadă' : run.meanWait === null ? NO_DATA : minutes(run.meanWait),
      compared: other && !light ? versus(run.meanWait, other.meanWait, minutes) : '',
      meaning: idle || 'Cât a stat la coadă, în medie, un pasager care a terminat controlul.',
    },
    {
      key: 'peakWait',
      label: 'Așteptare la vârf',
      value: light ? 'Fără coadă' : run.worst ? minutes(run.worst.p95) : NO_DATA,
      compared:
        other && !light ? versus(run.worst?.p95 ?? null, other.worst?.p95 ?? null, minutes) : '',
      meaning:
        idle ||
        `95% dintre pasageri au așteptat cel mult atât, la cel mai aglomerat punct de control${run.worst ? ` (${run.worst.label})` : ''}.`,
    },
    {
      key: 'late',
      label: 'Au ratat îmbarcarea',
      value: lateNow === null ? NO_DATA : `${num(run.late)} din ${num(run.lateOf)}`,
      compared: other ? versus(lateNow, lateThen, num, 'mai mulți', 'mai puțini') : '',
      meaning: run.lateOf
        ? 'Pasagerii care nu au ajuns la poartă la timp, din cei care aveau o oră de îmbarcare.'
        : 'Scenariul nu are ore de îmbarcare, deci nu putem număra întârzierile.',
    },
  ];
}

/** Whether two runs may be compared; returns the reason when they may not, or ''. */
export function comparisonProblem(
  baseId: string,
  other: ReportRun,
  check: RunComparison | null,
): string {
  if (!other.replay) return other.problem || 'Rularea de comparat nu are rezultate.';
  if (!check || check.baseline.id !== baseId || check.candidate.id !== other.id)
    return 'Nu am putut verifica dacă cele două rulări se pot compara.';
  const comparability = check.comparability;
  if (
    check.versionsMatch === true &&
    comparability?.comparable === true &&
    comparability.populationMatches === true
  )
    return '';
  const reasons = (comparability?.issues ?? []).map((issue) => issue.message).join(' ');
  return `Rulările au fost simulate în condiții diferite, așa că nu le comparăm.${reasons ? ` ${reasons}` : ''}`;
}

/** Snapshot minutes the run should have: its interval grid plus the end. */
function expectedMinutes(replay: PreparedReplay): number[] {
  const end = replay.data.captured.scenario.inputs.durationMinutes,
    interval = replay.data.intervalMinutes;
  return interval === null
    ? [...new Set([0, ...replay.minutes, end])].sort((a, b) => a - b)
    : [...Array.from({ length: Math.ceil(end / interval) }, (_, i) => i * interval), end];
}

/** Passengers queuing at all checkpoints together; null where any checkpoint's snapshot is missing. */
export function queueSeries(replay: PreparedReplay): QueuePoint[] {
  const checkpoints = replay.data.captured.layout.layout.checkpoints;
  return expectedMinutes(replay).map((minute) => {
    const row = replay.byMinute.get(minute);
    const values = checkpoints.map((cp) => row?.get(cp.id)?.queuedPassengers);
    return {
      minute,
      value: values.every((value) => value !== undefined)
        ? values.reduce((total, value) => total! + value!, 0)!
        : null,
    };
  });
}

/** Formats a simulated minute as local clock time in the scenario's time zone. */
export function clock(replay: PreparedReplay): (minute: number) => string {
  const { startsAtUtc, timeZone } = replay.data.captured.scenario.inputs;
  let format: Intl.DateTimeFormat | null = null;
  try {
    format = new Intl.DateTimeFormat('ro-RO', { hour: '2-digit', minute: '2-digit', timeZone });
  } catch {
    // An unknown time zone falls back to the simulated minute rather than guessing a clock.
  }
  return (minute) =>
    format ? format.format(Date.parse(startsAtUtc) + minute * 60000) : `minutul ${minute}`;
}

const X0 = 50,
  WIDTH = 640,
  Y0 = 200,
  HEIGHT = 170;
/** Column lengths in minutes: a day gets about 30 columns, none shorter than one snapshot interval. */
const SLOTS = [1, 2, 5, 10, 15, 30, 60, 120, 240];
const TICKS = [1, 2, 5, 10, 15, 30, 60, 120, 180, 240, 360, 720];

/** A round gridline step (1, 2, 5 × 10ⁿ whole passengers). */
function niceStep(value: number) {
  const power = 10 ** Math.max(0, Math.floor(Math.log10(value)));
  return ([1, 2, 5, 10].find((factor) => factor * power >= value) ?? 10) * power;
}

/** The queue as columns: the longest queue in each clock-aligned slot, beside the comparison run's. */
export function queueChart(run: PreparedReplay, other: PreparedReplay | null) {
  const horizon = Math.max(1, run.data.captured.scenario.inputs.durationMinutes);
  const time = clock(run);
  // Local minute of the day the run starts at, so slots and ticks land on round clock times.
  const [, hour, minute] = time(0).match(/(\d\d):(\d\d)/) ?? [];
  const offset = hour ? Number(hour) * 60 + Number(minute) : 0;
  const slot =
    SLOTS.find((step) => step >= horizon / 30 && step >= (run.data.intervalMinutes ?? 0)) ??
    SLOTS.at(-1)!;
  const last = Math.ceil((horizon + offset) / slot) - 1;
  /** Longest queue per slot; null when a snapshot in it is missing, so a gap never reads as calm. */
  const longest = (points: QueuePoint[]) => {
    const slots = new Map<number, number | null>();
    for (const point of points) {
      // The closing snapshot joins the last slot instead of opening an empty one.
      const key = Math.min(Math.floor((point.minute + offset) / slot), last);
      const known = slots.has(key) ? slots.get(key)! : 0;
      slots.set(key, point.value === null || known === null ? null : Math.max(known, point.value));
    }
    return slots;
  };
  const series = queueSeries(run);
  const mine = longest(series),
    theirs = other ? longest(queueSeries(other)) : null;
  const tallest = Math.max(
    1,
    ...[...mine.values(), ...(theirs?.values() ?? [])].flatMap((value) =>
      value === null ? [] : [value],
    ),
  );
  const step = niceStep(tallest / 3),
    top = Math.ceil(tallest / step) * step;
  const x = (at: number) => X0 + (Math.min(Math.max(at, 0), horizon) / horizon) * WIDTH,
    y = (value: number) => Y0 - (value / top) * HEIGHT;
  const columns = [...mine].map(([key, value]) => {
    const from = Math.max(0, key * slot - offset),
      to = Math.min(horizon, (key + 1) * slot - offset);
    const left = x(from),
      band = x(to) - left,
      centre = left + band / 2;
    const width = Math.max(1, theirs ? Math.min(12, (band - 6) / 2) : Math.min(24, band - 4));
    const compared = theirs?.get(key) ?? null;
    const bar = (at: number, of: number) => ({ x: at, y: y(of), width, height: Y0 - y(of) });
    const say = (count: number | null) =>
      count === null ? 'fără date' : count ? `cel mult ${people(count)}` : 'fără coadă';
    const label = `${time(from)}–${time(to)}`;
    return {
      from,
      label,
      /** Hover text for the slot, with the comparison run's figure when there is one. */
      tip: `${label} · ${say(value)}${theirs ? ` · rularea de comparat: ${say(compared)}` : ''}`,
      value,
      other: compared,
      band: { x: left, width: band },
      mine: value ? bar(theirs ? centre - width - 1 : centre - width / 2, value) : null,
      theirs: compared ? bar(centre + 1, compared) : null,
    };
  });
  const highest = columns.reduce<(typeof columns)[number] | null>(
    (best, column) => ((column.value ?? 0) > (best?.value ?? 0) ? column : best),
    null,
  );
  const peak = series.reduce<QueuePoint | null>(
    (best, point) => (point.value !== null && point.value > (best?.value ?? -1) ? point : best),
    null,
  );
  const every = TICKS.find((tick) => tick >= horizon / 6) ?? TICKS.at(-1)!;
  const ticks: { x: number; label: string }[] = [];
  for (let at = Math.ceil(offset / every) * every - offset; at <= horizon; at += every)
    ticks.push({ x: x(at), label: time(at) });
  const known = series.filter((point) => point.value !== null);
  return {
    slot,
    from: time(0),
    to: time(horizon),
    ticks,
    grid: Array.from({ length: top / step + 1 }, (_, i) => ({
      y: y(i * step),
      label: num(i * step),
    })),
    columns,
    /** Value written above the tallest column of the chosen run, clear of its comparison column. */
    mark: highest?.mine && {
      value: highest.value!,
      x: highest.mine.x + highest.mine.width / 2,
      y: Math.min(highest.mine.y, highest.theirs?.y ?? Y0) - 6,
    },
    /** Share of recorded moments with nobody queuing, in whole percent; null without data. */
    idle: known.length
      ? // Rounded down: one short queue must never read as "empty 100% of the time".
        Math.floor((known.filter((point) => point.value === 0).length / known.length) * 100)
      : null,
    peak: peak && { value: peak.value!, time: time(peak.minute) },
    missing: series.length - known.length,
  };
}

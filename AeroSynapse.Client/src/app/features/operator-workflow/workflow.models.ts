import type { ScenarioInputs } from '../operations/operations.models';
import type {
  StaffingEvaluatedVariant,
  StaffingEvaluationDetails,
} from '../operations/staffing-evaluation.models';
import { assessmentLabel, reasonLabel } from '../operations/staffing-evaluation-panel';
import {
  flightPassengers,
  incidentArrivalWindow,
  scenarioClock,
  surgePayload,
} from '../scenario-studio/incident-designer';
import type { InputEvent, StudioLayout } from '../scenario-studio/studio.models';
import { formatWait, OutcomeRow, percentChange } from '../../shared/ui/outcome-cards';
import { addMinutes } from '../../shared/absolute-time';

export type WorkflowStage = 'event' | 'simulate' | 'optimize' | 'compare' | 'review';
export type EvaluationStage = Extract<WorkflowStage, 'optimize' | 'compare' | 'review'>;
export const WORKFLOW_STEPS: { id: WorkflowStage; label: string }[] = [
  {
    id: 'event',
    label: 'Configurare incident',
  },
  { id: 'simulate', label: 'Simulare impact' },
  { id: 'optimize', label: 'Optimizare personal' },
  { id: 'compare', label: 'Comparație rezultate' },
  { id: 'review', label: 'Decizie și audit' },
];

/** A span on the scenario timeline, in percent of the scenario duration. */
export interface TimelineSpan {
  left: number;
  width: number;
}
export interface TimelineFlight {
  id: string;
  code: string;
  gate: string;
  passengers: number;
  departure: string;
  /** Departure position, or null when the flight leaves outside the simulated window. */
  departureAt: number | null;
  arrivals: TimelineSpan | null;
  /** Where most passengers arrive, in percent of the axis; null when arrivals are even across the window. */
  arrivalPeak: number | null;
  /** "01:36–03:06", for the arrivals curve's label. */
  arrivalsLabel: string | null;
  boarding: TimelineSpan | null;
  cancelled: boolean;
}
export interface FlightTimeline {
  ticks: { left: number; label: string }[];
  flights: TimelineFlight[];
  surge: (TimelineSpan & { flightId: string | null; label: string }) | null;
}

/** Flights laid out on the simulated window, so a person sees when each one leaves. */
export function flightTimeline(
  inputs: ScenarioInputs,
  layout: StudioLayout,
  surge: InputEvent | null,
): FlightTimeline {
  const duration = inputs.durationMinutes > 0 ? inputs.durationMinutes : 1;
  const start = Date.parse(inputs.startsAtUtc),
    span = duration * 60000,
    end = start + span;
  const at = (timestamp: number) => Math.min(100, Math.max(0, ((timestamp - start) / span) * 100));
  const between = (fromUtc: string, toUtc: string): TimelineSpan | null => {
    const from = Date.parse(fromUtc),
      to = Date.parse(toUtc);
    return Number.isFinite(from) && Number.isFinite(to) && to > from && to > start && from < end
      ? { left: at(from), width: at(to) - at(from) }
      : null;
  };
  const step = duration <= 120 ? 15 : duration <= 360 ? 30 : 60;
  const ticks = Array.from({ length: Math.floor(duration / step) + 1 }, (_, i) =>
    addMinutes(inputs.startsAtUtc, i * step),
  ).map((utc) => ({ left: at(Date.parse(utc)), label: scenarioClock(inputs, utc) }));
  const flights = [...inputs.flights]
    .sort((a, b) => Date.parse(a.departureAtUtc) - Date.parse(b.departureAtUtc))
    .map((flight): TimelineFlight => {
      const departure = Date.parse(flight.departureAtUtc);
      const window = incidentArrivalWindow(inputs, flight.id);
      const peak = window ? arrivalPeak(inputs, flight.id, window) : null;
      return {
        id: flight.id,
        code: flight.code,
        gate: layout.layout.gates.find((gate) => gate.id === flight.gateId)?.code ?? '—',
        passengers: flightPassengers(inputs, flight.id),
        departure: scenarioClock(inputs, flight.departureAtUtc),
        departureAt: departure >= start && departure <= end ? at(departure) : null,
        arrivals: window ? between(window.start, window.end) : null,
        arrivalPeak: peak === null ? null : at(peak),
        arrivalsLabel: window
          ? `${scenarioClock(inputs, window.start)}–${scenarioClock(inputs, window.end)}`
          : null,
        boarding:
          flight.boardingOpensAtUtc && flight.boardingClosesAtUtc
            ? between(flight.boardingOpensAtUtc, flight.boardingClosesAtUtc)
            : null,
        cancelled: flight.status === 'cancelled',
      };
    });
  const payload = surge ? surgePayload(surge) : null;
  const surgeEnd = surge && payload ? addMinutes(surge.occurredAtUtc, payload.durationMinutes) : '';
  const window = surge && payload ? between(surge.occurredAtUtc, surgeEnd) : null;
  return {
    ticks,
    flights,
    surge:
      surge && payload && window
        ? {
            ...window,
            // Keep a short surge visible as a band rather than a hairline.
            width: Math.max(window.width, 1.2),
            flightId: surge.flightId ?? null,
            label: `${scenarioClock(inputs, surge.occurredAtUtc)}–${scenarioClock(inputs, surgeEnd)}`,
          }
        : null,
  };
}

/** The instant most passengers arrive: the profile's mode, or mid-window when the API derives it from the flows. */
function arrivalPeak(
  inputs: ScenarioInputs,
  flightId: string,
  window: { start: string; end: string },
) {
  const batch = inputs.passengerGeneration?.batches.find((row) => row.flightId === flightId);
  const offset =
    batch?.arrival.kind === 'triangular'
      ? batch.arrival.mode
      : batch?.arrival.kind === 'normal'
        ? batch.arrival.mean
        : null;
  if (batch) return offset === null ? null : Date.parse(addMinutes(inputs.startsAtUtc, offset));
  return inputs.flows.some((row) => row.flightId === flightId)
    ? (Date.parse(window.start) + Date.parse(window.end)) / 2
    : null;
}

/** Where a new surge on this flight should start: when its passengers begin to arrive. */
export function surgeStartTime(inputs: ScenarioInputs, flightId: string, duration: number) {
  const flight = inputs.flights.find((row) => row.id === flightId);
  const batch = inputs.passengerGeneration?.batches.find((row) => row.flightId === flightId);
  const preferred = batch
    ? addMinutes(inputs.startsAtUtc, batch.arrival.minimum)
    : flight
      ? addMinutes(flight.departureAtUtc, -45)
      : inputs.startsAtUtc;
  const window = incidentArrivalWindow(inputs, flightId);
  return window
    ? new Date(
        Math.max(
          Date.parse(window.start),
          Math.min(Date.parse(preferred), Date.parse(addMinutes(window.end, -duration))),
        ),
      ).toISOString()
    : inputs.startsAtUtc;
}

export interface StaffMove {
  checkpointId: string;
  name: string;
  before: number;
  after: number;
  /** One entry per operator, for drawing people; capped so a big terminal stays readable. */
  beforePeople: number[];
  afterPeople: number[];
}
/** What a plan changes against today, before it is valued in money. */
export interface PlanSavings {
  passengerHoursSaved: number;
  /** Operator hours on duty over the day beyond today's; negative when the plan needs fewer. */
  operatorHoursAdded: number;
}
export interface EvaluationSummary {
  recommended: boolean;
  /** The recommended (or best simulated) plan against today; null without waits to compare. */
  savings: PlanSavings | null;
  /** Plain sentence naming where operators move, e.g. "Mutăm 2 operatori la CP-1". */
  headline: string;
  waitBefore: string;
  waitAfter: string;
  outcomes: OutcomeRow[];
  moves: StaffMove[];
  candidates: {
    runId: string;
    position: number;
    /** "Varianta 2", or what kind of plan it is when the evaluation compared shift plans; unique within the list. */
    title: string;
    /** "12 operatori · 6 în plus față de acum". */
    subtitle: string;
    /** Why a variant lost, in plain words; null for the recommended one or without a reason. */
    reason: string | null;
    /** People whose control changes against today, the plain reading of the cost. */
    changes: number;
    status: string;
    label: string;
    wait: string;
    /** Mean wait as a share of the baseline's, for a bar; null without both values. */
    waitShare: number | null;
    /** The plan makes passengers wait longer than today. */
    worse: boolean;
    cost: number;
    recommended: boolean;
    configuration: { checkpointId: string; name: string; count: number; delta: number }[];
  }[];
}

/** Paid operator hours over the day: each shift from start to end, breaks and gaps included; else staffed slots; else fixed counts all day. */
function operatorHours(variant: StaffingEvaluatedVariant, durationMinutes: number) {
  const schedule = variant.schedule;
  if (schedule?.shifts?.length)
    return (
      schedule.shifts.reduce(
        (sum, shift) =>
          sum + shift.segments[shift.segments.length - 1].endMinute - shift.segments[0].startMinute,
        0,
      ) / 60
    );
  return schedule?.checkpoints.length
    ? (schedule.checkpoints.reduce((sum, row) => sum + row.staff.reduce((a, b) => a + b, 0), 0) *
        schedule.slotMinutes) /
        60
    : (total(variant.configuration) * durationMinutes) / 60;
}
function planSavings(
  baseline: StaffingEvaluatedVariant,
  variant: StaffingEvaluatedVariant,
  durationMinutes: number,
): PlanSavings | null {
  const before = baseline.metrics,
    after = variant.metrics;
  if (before?.meanWaitMinutes == null || after?.meanWaitMinutes == null) return null;
  if (before.populationCount <= 0) return null;
  return {
    passengerHoursSaved:
      (before.populationCount * (before.meanWaitMinutes - after.meanWaitMinutes)) / 60,
    operatorHoursAdded:
      operatorHours(variant, durationMinutes) - operatorHours(baseline, durationMinutes),
  };
}

const PEOPLE_CAP = 12;
const total = (rows: readonly { staffCount: number }[]) =>
  rows.reduce((sum, row) => sum + row.staffCount, 0);
/** Repeated titles get " · A", " · B", so two plans of the same kind can be told apart. */
function distinct(titles: string[]) {
  return titles.map((title, index) => {
    const same = titles.filter((other) => other === title);
    return same.length > 1
      ? `${title} · ${String.fromCharCode(65 + titles.slice(0, index).filter((o) => o === title).length)}`
      : title;
  });
}
const people = (count: number) => Array.from({ length: Math.min(count, PEOPLE_CAP) }, (_, i) => i);
const operators = (count: number) => (count === 1 ? '1 operator' : `${count} operatori`);

type Shifts = NonNullable<NonNullable<StaffingEvaluatedVariant['schedule']>['shifts']>;
/** Moves straight from one checkpoint to another; a break in between is not a move. */
export function shiftMoves(shifts: Shifts) {
  return shifts.reduce(
    (total, shift) =>
      total +
      shift.segments.filter(
        (segment, index) =>
          index > 0 &&
          shift.segments[index - 1].endMinute === segment.startMinute &&
          shift.segments[index - 1].checkpointId !== segment.checkpointId,
      ).length,
    0,
  );
}
function planTitle(variant: StaffingEvaluatedVariant) {
  if (!variant.schedule) return `Varianta ${variant.position}`;
  const shifts = variant.schedule.shifts;
  // A fixed plan keeps the same people on duty all day; shift plans also respect breaks.
  if (!shifts) return 'Plan fix · alocare constantă, fără pauze';
  const moves = shiftMoves(shifts);
  return `Plan pe ture · ${moves ? (moves === 1 ? 'o mutare' : `${moves} mutări`) : 'fără mutări'}`;
}

/** Per-slot staffing of a shift plan beside today's, and every person's shift as bars on one day axis. */
export interface ShiftPlanView {
  /** The plan's name, as its card in the compared plans says it. */
  title: string;
  /** Why it lost, when it is shown only for comparison. */
  reason: string | null;
  slots: number;
  /** Clock time each slot starts, for the cells' titles. */
  slotTimes: string[];
  ticks: { left: number; label: string }[];
  rows: {
    checkpointId: string;
    name: string;
    arrivals: number[];
    peak: number;
    now: number[];
    /** Today's staffing when it never changes, so the row says it once instead of in every slot. */
    constantNow: number | null;
    proposed: number[];
    /** The most anyone plans at once here. */
    most: number;
    /** "Acum: 1 operator toată ziua · pe ture: 0–5, cei mai mulți la 02:36". */
    summary: string;
  }[];
  people: {
    id: string;
    name: string;
    hours: string;
    bars: { left: number; width: number; checkpoint: string; tone: number; moved: boolean }[];
    breaks: { left: number; width: number }[];
  }[];
  moves: number;
  /** False when the shift plan lost to today or to the fixed plan: it is shown, but not proposed. */
  recommended: boolean;
}
export function shiftPlanView(
  evaluation: StaffingEvaluationDetails,
  inputs: ScenarioInputs,
  names: ReadonlyMap<string, string> = new Map(),
): ShiftPlanView | null {
  const baseline = evaluation.variants.find((row) => row.position === 0);
  const others = evaluation.variants.filter((row) => row.schedule?.shifts?.length);
  const plan =
    others.find((row) => row.runId === evaluation.recommendedRunId) ??
    [...others].sort(
      (a, b) => (a.metrics?.meanWaitMinutes ?? Infinity) - (b.metrics?.meanWaitMinutes ?? Infinity),
    )[0];
  if (!baseline?.schedule || !plan?.schedule?.shifts) return null;
  const day = inputs.durationMinutes > 0 ? inputs.durationMinutes : 1;
  const at = (minute: number) => Math.min(100, Math.max(0, (minute / day) * 100));
  const slots = plan.schedule.checkpoints[0]?.staff.length ?? 0;
  const order = plan.schedule.checkpoints.map((row) => row.checkpointId);
  const slotTimes = Array.from({ length: slots }, (_, slot) =>
    scenarioClock(inputs, addMinutes(inputs.startsAtUtc, (slot * day) / Math.max(1, slots))),
  );
  const titles = distinct(evaluation.variants.filter((row) => row.position > 0).map(planTitle));
  const position = evaluation.variants.filter((row) => row.position > 0).indexOf(plan);
  return {
    title: titles[position] ?? planTitle(plan),
    reason:
      plan.runId === evaluation.recommendedRunId || !plan.assessment.reasons.length
        ? null
        : reasonLabel(plan.assessment.reasons[0], baseline.configuration),
    slots,
    slotTimes,
    ticks: Array.from({ length: Math.floor(day / 60) + 1 }, (_, hour) => hour * 60).map(
      (minute) => ({
        left: at(minute),
        label: scenarioClock(inputs, addMinutes(inputs.startsAtUtc, minute)),
      }),
    ),
    rows: plan.schedule.checkpoints.map((row) => {
      const arrivals = row.arrivals ?? [];
      const now =
        baseline.schedule!.checkpoints.find((other) => other.checkpointId === row.checkpointId)
          ?.staff ?? [];
      const constantNow = now.length && now.every((count) => count === now[0]) ? now[0] : null;
      const most = Math.max(0, ...row.staff),
        least = Math.min(...row.staff);
      const busiest = slotTimes[row.staff.indexOf(most)] ?? '';
      return {
        checkpointId: row.checkpointId,
        name: row.name,
        arrivals,
        peak: Math.max(1, ...arrivals),
        now,
        constantNow,
        proposed: row.staff,
        most,
        summary: `Acum: ${constantNow === null ? 'variază pe parcursul zilei' : `${operators(constantNow)} toată ziua`} · pe ture: ${least === most ? operators(most) : `între ${least} și ${most} operatori`}${most > least ? `, cei mai mulți la ${busiest}` : ''}`,
      };
    }),
    people: [...plan.schedule.shifts]
      .sort((a, b) => a.segments[0].startMinute - b.segments[0].startMinute)
      .map((shift, index) => {
        const first = shift.segments[0].startMinute,
          last = shift.segments[shift.segments.length - 1].endMinute;
        return {
          id: shift.staffMemberId,
          name: names.get(shift.staffMemberId) ?? `Operator ${index + 1}`,
          hours: `${scenarioClock(inputs, addMinutes(inputs.startsAtUtc, first))}–${scenarioClock(inputs, addMinutes(inputs.startsAtUtc, last))}`,
          bars: shift.segments.map((segment, position) => ({
            left: at(segment.startMinute),
            width: at(segment.endMinute) - at(segment.startMinute),
            checkpoint:
              plan.schedule!.checkpoints.find((row) => row.checkpointId === segment.checkpointId)
                ?.name ?? '—',
            tone: Math.max(0, order.indexOf(segment.checkpointId)),
            moved:
              position > 0 &&
              shift.segments[position - 1].endMinute === segment.startMinute &&
              shift.segments[position - 1].checkpointId !== segment.checkpointId,
          })),
          breaks: shift.segments.slice(1).flatMap((segment, position) => {
            const end = shift.segments[position].endMinute;
            return segment.startMinute > end
              ? [{ left: at(end), width: at(segment.startMinute) - at(end) }]
              : [];
          }),
        };
      }),
    moves: shiftMoves(plan.schedule.shifts),
    recommended: plan.runId === evaluation.recommendedRunId,
  };
}

/** The baseline against the recommended (or best simulated) candidate, in plain outcomes. */
export function evaluationSummary(
  evaluation: StaffingEvaluationDetails,
  /** The simulated day's length, for valuing staff in plans without a schedule. */
  durationMinutes: number,
): EvaluationSummary | null {
  const baseline = evaluation.variants.find((row) => row.position === 0);
  if (!baseline) return null;
  const others = evaluation.variants.filter((row) => row.position > 0);
  const recommended = others.find((row) => row.runId === evaluation.recommendedRunId);
  const best =
    recommended ??
    [...others]
      .filter((row) => row.metrics?.meanWaitMinutes != null)
      .sort((a, b) => a.metrics!.meanWaitMinutes! - b.metrics!.meanWaitMinutes!)[0];
  type Metrics = NonNullable<StaffingEvaluatedVariant['metrics']>;
  const base = baseline;
  /**
   * Waits under a minute read better in seconds. Both wait cards follow the unit of the
   * headline (the mean wait) and round like it, so the cards repeat the verdict's figures.
   */
  const seconds =
    Math.max(base.metrics?.meanWaitMinutes ?? 0, best?.metrics?.meanWaitMinutes ?? 0) < 1;
  const waitRow = (key: string, label: string, read: (metrics: Metrics) => number | null) => {
    const before = base.metrics ? read(base.metrics) : null,
      after = best?.metrics ? read(best.metrics) : null,
      scale = (value: number | null) => (value === null ? null : seconds ? value * 60 : value);
    return {
      key,
      label,
      unit: seconds ? 'secunde' : 'minute',
      before: scale(before),
      after: scale(after),
      percent: percentChange(before, after),
    };
  };
  const moves = baseline.configuration.map((item): StaffMove => {
    const after =
      best?.configuration.find((other) => other.checkpointId === item.checkpointId)?.staffCount ??
      item.staffCount;
    return {
      checkpointId: item.checkpointId,
      name: item.name,
      before: item.staffCount,
      after,
      beforePeople: people(item.staffCount),
      afterPeople: people(after),
    };
  });
  const gains = moves.filter((move) => move.after > move.before);
  const shifts = best?.schedule?.shifts;
  const before = total(baseline.configuration),
    after = best ? total(best.configuration) : before;
  const headline = !best
    ? 'Nicio variantă simulată nu are rezultate complete'
    : shifts?.length
      ? `Plan pe ture: ${operators(shifts.length)} alocați în funcție de sosiri`
      : after !== before
        ? // People are added or sent home, not just moved, so the totals say it first.
          `${operators(after)} în loc de ${before} (${after > before ? '+' : '−'}${Math.abs(after - before)})`
        : !gains.length
          ? 'Alocare de personal nemodificată'
          : gains.length === 1
            ? `Realocare: ${operators(gains[0].after - gains[0].before)} la ${gains[0].name}`
            : `Realocare operatori la ${gains.map((move) => move.name).join(' și ')}`;
  const baselineWait = baseline.metrics?.meanWaitMinutes ?? null;
  return {
    recommended: !!recommended,
    savings: best ? planSavings(baseline, best, durationMinutes) : null,
    headline,
    waitBefore: formatWait(baselineWait),
    waitAfter: formatWait(best?.metrics?.meanWaitMinutes),
    outcomes: best
      ? [
          waitRow('mean', 'Așteptarea medie', (m) => m.meanWaitMinutes),
          waitRow('p95', 'Așteptarea pentru 95% dintre pasageri', (m) => m.p95WaitMinutes),
          (() => {
            const before = base.metrics?.boardingRisk ?? null,
              after = best.metrics?.boardingRisk ?? null;
            return {
              key: 'risk',
              label: 'Risc de depășire a termenului de îmbarcare',
              unit: '%',
              before: before === null ? null : before * 100,
              after: after === null ? null : after * 100,
              percent: percentChange(before, after),
            };
          })(),
        ]
      : [],
    moves,
    candidates: distinct(others.map(planTitle)).map((title, index) => {
      const variant = others[index];
      const wait = variant.metrics?.meanWaitMinutes ?? null;
      const count = total(variant.configuration);
      const recommendedHere = variant.runId === evaluation.recommendedRunId;
      return {
        runId: variant.runId,
        position: variant.position,
        title,
        subtitle: `${operators(count)} · ${count === before ? 'la fel ca acum' : `${Math.abs(count - before)} ${count > before ? 'în plus' : 'mai puțin'} față de acum`}`,
        reason:
          recommendedHere || !variant.assessment.reasons.length
            ? null
            : reasonLabel(variant.assessment.reasons[0], baseline.configuration),
        changes: variant.reallocatedPeople,
        status: variant.assessment.status,
        label: assessmentLabel(variant.assessment.status),
        wait: formatWait(wait),
        waitShare:
          wait === null || !baselineWait ? null : Math.min(100, (wait / baselineWait) * 100),
        worse: wait !== null && baselineWait !== null && wait > baselineWait,
        cost: variant.reallocationCost,
        recommended: variant.runId === evaluation.recommendedRunId,
        configuration: baseline.configuration.map((item) => {
          const count =
            variant.configuration.find((other) => other.checkpointId === item.checkpointId)
              ?.staffCount ?? item.staffCount;
          return {
            checkpointId: item.checkpointId,
            name: item.name,
            count,
            delta: count - item.staffCount,
          };
        }),
      };
    }),
  };
}

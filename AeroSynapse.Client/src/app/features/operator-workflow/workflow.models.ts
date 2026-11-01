import type { ScenarioInputs } from '../operations/operations.models';
import type {
  StaffingEvaluatedVariant,
  StaffingEvaluationDetails,
} from '../operations/staffing-evaluation.models';
import { assessmentLabel } from '../operations/staffing-evaluation-panel';
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
      return {
        id: flight.id,
        code: flight.code,
        gate: layout.layout.gates.find((gate) => gate.id === flight.gateId)?.code ?? '—',
        passengers: flightPassengers(inputs, flight.id),
        departure: scenarioClock(inputs, flight.departureAtUtc),
        departureAt: departure >= start && departure <= end ? at(departure) : null,
        arrivals: window ? between(window.start, window.end) : null,
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
export interface EvaluationSummary {
  recommended: boolean;
  /** Plain sentence naming where operators move, e.g. "Mutăm 2 operatori la CP-1". */
  headline: string;
  waitBefore: string;
  waitAfter: string;
  outcomes: OutcomeRow[];
  moves: StaffMove[];
  candidates: {
    runId: string;
    position: number;
    /** "Varianta 2", or what kind of plan it is when the evaluation compared shift plans. */
    title: string;
    status: string;
    label: string;
    wait: string;
    /** Mean wait as a share of the baseline's, for a bar; null without both values. */
    waitShare: number | null;
    cost: number;
    recommended: boolean;
    configuration: { checkpointId: string; name: string; count: number; delta: number }[];
  }[];
}

const PEOPLE_CAP = 12;
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
  slots: number;
  ticks: { left: number; label: string }[];
  rows: {
    checkpointId: string;
    name: string;
    arrivals: number[];
    peak: number;
    now: number[];
    proposed: number[];
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
  return {
    slots,
    ticks: Array.from({ length: Math.floor(day / 60) + 1 }, (_, hour) => hour * 60).map(
      (minute) => ({
        left: at(minute),
        label: scenarioClock(inputs, addMinutes(inputs.startsAtUtc, minute)),
      }),
    ),
    rows: plan.schedule.checkpoints.map((row) => {
      const arrivals = row.arrivals ?? [];
      return {
        checkpointId: row.checkpointId,
        name: row.name,
        arrivals,
        peak: Math.max(1, ...arrivals),
        now:
          baseline.schedule!.checkpoints.find((other) => other.checkpointId === row.checkpointId)
            ?.staff ?? [],
        proposed: row.staff,
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
export function evaluationSummary(evaluation: StaffingEvaluationDetails): EvaluationSummary | null {
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
      scale = (value: number | null) =>
        value === null ? null : seconds ? value * 60 : value;
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
  const headline = !best
    ? 'Nicio variantă simulată nu are rezultate complete'
    : shifts?.length
      ? `Plan pe ture: ${operators(shifts.length)} alocați în funcție de sosiri`
      : !gains.length
        ? 'Alocare de personal nemodificată'
        : gains.length === 1
          ? `Realocare: ${operators(gains[0].after - gains[0].before)} la ${gains[0].name}`
          : `Realocare operatori la ${gains.map((move) => move.name).join(' și ')}`;
  const baselineWait = baseline.metrics?.meanWaitMinutes ?? null;
  return {
    recommended: !!recommended,
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
    candidates: others.map((variant) => {
      const wait = variant.metrics?.meanWaitMinutes ?? null;
      return {
        runId: variant.runId,
        position: variant.position,
        title: planTitle(variant),
        status: variant.assessment.status,
        label: assessmentLabel(variant.assessment.status),
        wait: formatWait(wait),
        waitShare:
          wait === null || !baselineWait ? null : Math.min(100, (wait / baselineWait) * 100),
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

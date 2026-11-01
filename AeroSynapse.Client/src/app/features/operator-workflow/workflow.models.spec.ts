import type { ScenarioInputs } from '../operations/operations.models';
import { describeSurge, scenarioClock } from '../scenario-studio/incident-designer';
import type { InputEvent, StudioLayout } from '../scenario-studio/studio.models';
import {
  evaluationSummary,
  flightTimeline,
  shiftPlanView,
  surgeStartTime,
} from './workflow.models';
import { baselineRunId, ids, workflowEvaluation } from './workflow.testing';

const inputs = {
  startsAtUtc: '2026-10-16T10:00:00Z',
  durationMinutes: 60,
  seed: 42,
  timeZone: 'Europe/Bucharest',
  flights: [
    {
      id: 'f-late',
      code: 'RO 2',
      gateId: 'g-2',
      departureAtUtc: '2026-10-16T12:00:00Z',
      passengerCapacity: 10,
    },
    {
      id: 'f-1',
      code: 'RO 1',
      gateId: 'g-1',
      departureAtUtc: '2026-10-16T10:45:00Z',
      passengerCapacity: 150,
    },
  ],
  populations: [],
  flows: [
    {
      id: 'flow',
      populationId: 'p',
      flightId: 'f-1',
      routeId: 'r',
      passengerCount: 120,
      startsAtUtc: '2026-10-16T10:00:00Z',
      endsAtUtc: '2026-10-16T10:45:00Z',
    },
  ],
  staffAllocations: [],
  events: [],
} as unknown as ScenarioInputs;
const layout = {
  layout: {
    gates: [
      { id: 'g-1', code: 'G-1' },
      { id: 'g-2', code: 'G-2' },
    ],
  },
} as unknown as StudioLayout;
const surge: InputEvent = {
  id: 'surge',
  type: 'incident.passenger-surge',
  occurredAtUtc: '2026-10-16T10:15:00Z',
  flightId: 'f-1',
  gateId: 'g-1',
  checkpointId: 'cp',
  payloadJson: JSON.stringify({
    contractVersion: '1.0',
    durationMinutes: 5,
    share: 0.5,
    isSynthetic: true,
  }),
};

describe('Decision flow presentation', () => {
  it('shows scenario times in the scenario time zone, like the simulation clock', () => {
    expect(scenarioClock(inputs, '2026-10-16T10:30:00Z')).toBe('13:30');
    expect(describeSurge(surge, inputs)).toBe(
      'Val de pasageri · RO 1 · 13:15–13:20 · 50% din pasageri',
    );
  });
  it('lays flights out by departure and marks the surge on its own flight', () => {
    const line = flightTimeline(inputs, layout, surge);
    expect(line.flights.map((row) => row.code)).toEqual(['RO 1', 'RO 2']);
    expect(line.flights[0]).toMatchObject({
      gate: 'G-1',
      passengers: 120,
      departure: '13:45',
      departureAt: 75,
    });
    // A flight leaving after the simulated window has no marker on it.
    expect(line.flights[1].departureAt).toBeNull();
    expect(line.surge).toMatchObject({ flightId: 'f-1', left: 25, label: '13:15–13:20' });
    expect(line.ticks.map((tick) => tick.label)).toEqual([
      '13:00',
      '13:15',
      '13:30',
      '13:45',
      '14:00',
    ]);
  });
  it('starts a new surge 45 minutes before departure, inside the scenario', () => {
    expect(surgeStartTime(inputs, 'f-1', 5)).toBe('2026-10-16T10:00:00.000Z');
    expect(surgeStartTime({ ...inputs, durationMinutes: 180 }, 'f-late', 5)).toBe(
      '2026-10-16T11:15:00.000Z',
    );
  });
  it('compares the baseline with the recommended candidate in plain outcomes', () => {
    const summary = evaluationSummary(workflowEvaluation())!;
    expect(summary.recommended).toBe(true);
    expect(summary.outcomes[0]).toMatchObject({ before: 2, after: 1, percent: -50 });
    expect(summary.outcomes[2]).toMatchObject({ before: 0, after: 0, percent: null });
    expect(summary.candidates).toEqual([
      expect.objectContaining({ runId: ids.otherRun, recommended: true, cost: 20 }),
    ]);
    expect(summary.waitBefore).toBe('2.00 min');
    const quick = workflowEvaluation();
    quick.variants[0].configuration = [
      { checkpointId: 'cp-1', name: 'CP-1', staffCount: 1 },
      { checkpointId: 'cp-2', name: 'CP-2', staffCount: 2 },
    ];
    quick.variants[1].configuration = [
      { checkpointId: 'cp-1', name: 'CP-1', staffCount: 3 },
      { checkpointId: 'cp-2', name: 'CP-2', staffCount: 0 },
    ];
    quick.variants[0].metrics!.meanWaitMinutes = 0.5;
    quick.variants[1].metrics!.meanWaitMinutes = 0.1;
    const plain = evaluationSummary(quick)!;
    // Short waits read in seconds, and the headline names where people move.
    expect(plain.headline).toBe('Realocare: 2 operatori la CP-1');
    expect([plain.waitBefore, plain.waitAfter]).toEqual(['30.00 s', '6.00 s']);
    expect(plain.outcomes[0]).toMatchObject({ unit: 'secunde', before: 30, after: 6 });
    // The p95 card follows the headline's unit and rounding, even past a minute.
    quick.variants[0].metrics!.meanWaitMinutes = 0.178;
    quick.variants[0].metrics!.p95WaitMinutes = 1.04;
    quick.variants[1].metrics!.p95WaitMinutes = 0.14;
    const rounded = evaluationSummary(quick)!;
    expect(rounded.waitBefore).toBe('10.68 s');
    expect(rounded.outcomes[0]).toMatchObject({ before: 10.68, after: 6 });
    expect(rounded.outcomes[1].unit).toBe('secunde');
    expect(rounded.outcomes[1].before).toBeCloseTo(62.4);
    expect(rounded.outcomes[1].after).toBeCloseTo(8.4);
    quick.variants[0].metrics!.meanWaitMinutes = 2 / 60;
    quick.variants[1].metrics!.meanWaitMinutes = 1.8 / 60;
    const precise = evaluationSummary(quick)!;
    expect([precise.waitBefore, precise.waitAfter]).toEqual(['2.00 s', '1.80 s']);
    expect(precise.outcomes[0].before).toBeCloseTo(2);
    expect(precise.outcomes[0].after).toBeCloseTo(1.8);
    expect(precise.outcomes[0].percent).toBeCloseTo(-10);
    expect(plain.moves[0]).toMatchObject({ before: 1, after: 3, afterPeople: [0, 1, 2] });
    expect(plain.candidates[0].configuration[1]).toMatchObject({ count: 0, delta: -2 });
    expect(plain.candidates[0].waitShare).toBeCloseTo(20);
    const unrecommended = { ...workflowEvaluation(), recommendedRunId: null };
    expect(evaluationSummary(unrecommended)!.recommended).toBe(false);
    expect(
      evaluationSummary({
        ...unrecommended,
        variants: unrecommended.variants.filter((row) => row.runId !== baselineRunId),
      }),
    ).toBeNull();
  });
});

describe('Shift plans in the decision flow', () => {
  const A = 'a0000000-0000-0000-0000-0000000000aa',
    B = 'b0000000-0000-0000-0000-0000000000bb',
    P1 = 'c0000000-0000-0000-0000-000000000001',
    P2 = 'c0000000-0000-0000-0000-000000000002';
  const row = (checkpointId: string, name: string, staff: number[], arrivals: number[]) => ({
    checkpointId,
    name,
    staff,
    arrivals,
  });
  function evaluation() {
    const value = workflowEvaluation();
    value.variants[0].schedule = {
      slotMinutes: 15,
      checkpoints: [
        row(A, 'CP-A', [1, 1, 1, 1], [2, 8, 4, 0]),
        row(B, 'CP-B', [1, 1, 1, 1], [0, 0, 6, 2]),
      ],
    };
    value.variants[1].schedule = {
      slotMinutes: 15,
      checkpoints: [
        row(A, 'CP-A', [1, 2, 0, 1], [2, 8, 4, 0]),
        row(B, 'CP-B', [0, 0, 1, 1], [0, 0, 6, 2]),
      ],
      shifts: [
        // Straight from A to B at 13:30: one move. The second person pauses 13:30–13:45.
        {
          staffMemberId: P1,
          segments: [
            { checkpointId: A, startMinute: 0, endMinute: 30 },
            { checkpointId: B, startMinute: 30, endMinute: 60 },
          ],
        },
        {
          staffMemberId: P2,
          segments: [
            { checkpointId: A, startMinute: 15, endMinute: 30 },
            { checkpointId: A, startMinute: 45, endMinute: 60 },
          ],
        },
      ],
    };
    return value;
  }
  it('names shift variants by their moves and leads with the shift plan', () => {
    const summary = evaluationSummary(evaluation())!;
    expect(summary.headline).toBe('Plan pe ture: 2 operatori alocați în funcție de sosiri');
    expect(summary.candidates[0].title).toBe('Plan pe ture · o mutare');
    const fixed = evaluation();
    fixed.variants[1].schedule!.shifts = null;
    expect(evaluationSummary(fixed)!.candidates[0].title).toBe(
      'Plan fix · alocare constantă, fără pauze',
    );
    expect(evaluationSummary(workflowEvaluation())!.candidates[0].title).toBe('Varianta 1');
  });
  it('lays the plan out per slot beside today and every shift on the day axis', () => {
    const plan = shiftPlanView(evaluation(), inputs, new Map([[P1, 'Ana Pop']]))!;
    expect(plan.ticks.map((tick) => tick.label)).toEqual(['13:00', '14:00']);
    expect(plan.rows[0]).toMatchObject({
      name: 'CP-A',
      peak: 8,
      now: [1, 1, 1, 1],
      proposed: [1, 2, 0, 1],
    });
    expect(plan.moves).toBe(1);
    const [first, second] = plan.people;
    expect(first).toMatchObject({ name: 'Ana Pop', hours: '13:00–14:00' });
    expect(first.bars.map((bar) => [bar.checkpoint, bar.moved])).toEqual([
      ['CP-A', false],
      ['CP-B', true],
    ]);
    expect(second).toMatchObject({ name: 'Operator 2', breaks: [{ left: 50, width: 25 }] });
    expect(shiftPlanView(workflowEvaluation(), inputs)).toBeNull();
  });
});

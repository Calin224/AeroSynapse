import { ScenarioInputs } from '../operations/operations.models';
import {
  basicErrors,
  departureSpacingErrors,
  makeDraft,
  PRESETS,
  StudioLayout,
} from './studio.models';

const start = new Date('2026-10-31T06:00:00Z');
const at = (minute: number) => new Date(+start + minute * 60000).toISOString();
function inputs(gap: number): ScenarioInputs {
  return {
    startsAtUtc: at(0),
    durationMinutes: 240,
    seed: 42,
    timeZone: 'UTC',
    minimumDepartureSpacingMinutes: 60,
    flights: [60, 60 + gap].map((minute, index) => ({
      id: `flight-${index}`,
      code: `AS ${index + 1}`,
      gateId: 'gate',
      departureAtUtc: at(minute),
      boardingOpensAtUtc: at(minute - 12),
      boardingClosesAtUtc: at(minute - 2),
      passengerCapacity: 120,
      delayMinutes: 0,
      status: 'scheduled',
      isSynthetic: true,
    })),
    populations: [],
    flows: [],
    staffAllocations: [],
    events: [],
  };
}

describe('One-hour spacing between departures at the same gate', () => {
  it.each([0, 1, 59, 59.99])(
    'rejects a %s-minute gap at one gate, allows it across gates, and retains original field indices',
    (gap) => {
      const value = inputs(gap);
      value.flights.reverse();
      const errors = departureSpacingErrors(value);
      expect(errors['inputs.flights[0].departureAtUtc'][0]).toContain('AS 1');
      expect(errors['inputs.flights[1].departureAtUtc'][0]).toContain('AS 2');
      value.flights[0].gateId = 'gate-2';
      expect(departureSpacingErrors(value)).toEqual({});
    },
  );

  it('accepts exactly an hour, checks effective delayed times, and ignores cancelled departures', () => {
    const value = inputs(60);
    expect(departureSpacingErrors(value)).toEqual({});
    value.flights[0].delayMinutes = 1;
    expect(Object.keys(departureSpacingErrors(value))).toHaveLength(2);
    value.flights[0].status = 'cancelled';
    expect(departureSpacingErrors(value)).toEqual({});
    delete value.minimumDepartureSpacingMinutes;
    value.flights[0].status = 'scheduled';
    expect(departureSpacingErrors(value)).toEqual({});
  });

  const layout = (gateCount: number): StudioLayout => {
    const gates = Array.from({ length: gateCount }, (_, index) => `gate-${index + 1}`);
    return {
      version: { id: 'layout', sceneId: 'scene', version: 1, createdAtUtc: at(0) },
      layout: {
        scene: { id: 'scene', code: 'S', name: 'Scene', isSynthetic: true },
        gates: gates.map((id) => ({
          id,
          code: id,
          name: id,
          capacity: 150,
          terminalId: 'terminal',
        })),
        checkpoints: [
          { id: 'cp', code: 'CP1', name: 'Control', capacityPerMinute: 3, terminalId: 'terminal' },
        ],
        routes: gates.map((id) => ({
          id: `route-${id}`,
          code: 'R',
          fromCheckpointId: 'cp',
          toGateId: id,
          terminalId: 'terminal',
        })),
        staff: [1, 2, 3].map((index) => ({
          id: `staff-${index}`,
          code: `OP${index}`,
          displayName: `Operator ${index}`,
          terminalId: 'terminal',
          checkpointId: 'cp',
        })),
      },
    };
  };

  it('keeps the peak bank ten minutes apart by giving each departure its own gate', () => {
    const departures = makeDraft(layout(5), 'peak', start).inputs.flights.map((flight) =>
      Date.parse(flight.departureAtUtc),
    );
    expect(departures.map((time) => (time - +start) / 60000)).toEqual([150, 160, 170, 180, 190]);
  });

  it.each([1, 5])('uses the hour rule in every new preset with %s gate(s)', (gateCount) => {
    for (const preset of PRESETS) {
      const draft = makeDraft(layout(gateCount), preset.id, start);
      expect(draft.inputs.minimumDepartureSpacingMinutes).toBe(60);
      expect(basicErrors(draft)).toEqual({});
      expect(departureSpacingErrors(draft.inputs)).toEqual({});
      expect(
        draft.inputs.flights.every(
          (flight) =>
            Date.parse(flight.departureAtUtc) <= +start + draft.inputs.durationMinutes * 60000,
        ),
      ).toBe(true);
      if (preset.id === 'workday') {
        expect(draft.inputs.flights).toHaveLength(8);
        expect(draft.inputs.durationMinutes).toBe(480);
      }
    }
  });

  it('schedules one of each incident inside the showcase bank, before the delayed flight closes', () => {
    const roomy = layout(5);
    roomy.layout.staff.push(
      ...[4, 5, 6].map((index) => ({
        id: `staff-${index}`,
        code: `OP${index}`,
        displayName: `Operator ${index}`,
        terminalId: 'terminal',
        checkpointId: null,
      })),
    );
    const { inputs } = makeDraft(roomy, 'showcase', start);
    const allocated = new Set(inputs.staffAllocations.map((row) => row.staffMemberId));
    expect(
      inputs
        .operators!.filter((person) => person.role === 'security' && !allocated.has(person.id))
        .map((person) => person.id),
    ).toEqual(['staff-4', 'staff-5']);
    const minute = (utc: string) => (Date.parse(utc) - +start) / 60000;
    const [surge, closure, reopening, delay] = inputs.events;
    const last = inputs.flights.at(-1)!;
    expect(
      inputs.events.map((event) => [event.type, minute(event.occurredAtUtc), event.payloadJson]),
    ).toEqual([
      ['passenger_surge', 100, '{"passengerCount":120,"windowMinutes":15}'],
      ['checkpoint_unavailable', 110, '{}'],
      ['resume', 135, '{}'],
      ['flight_change', 140, '{"delayMinutes":20}'],
    ]);
    expect(surge.flightId).toBe(inputs.flights[2].id);
    expect(closure.checkpointId).toBe(reopening.checkpointId);
    expect(delay.flightId).toBe(last.id);
    expect(minute(delay.occurredAtUtc)).toBeLessThan(minute(last.boardingClosesAtUtc!));
    expect(minute(last.departureAtUtc) + 20).toBeLessThanOrEqual(inputs.durationMinutes);
  });
});

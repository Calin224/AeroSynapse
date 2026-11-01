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
      gateId: `gate-${index}`,
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

describe('One-hour spacing between scenario departures', () => {
  it.each([0, 1, 59, 59.99])(
    'rejects a %s-minute gap even at different gates and retains original field indices',
    (gap) => {
      const value = inputs(gap);
      value.flights.reverse();
      const errors = departureSpacingErrors(value);
      expect(errors['inputs.flights[0].departureAtUtc'][0]).toContain('AS 1');
      expect(errors['inputs.flights[1].departureAtUtc'][0]).toContain('AS 2');
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

  it('uses the hour rule in every new preset and fits all departures inside its horizon', () => {
    const layout: StudioLayout = {
      version: { id: 'layout', sceneId: 'scene', version: 1, createdAtUtc: at(0) },
      layout: {
        scene: { id: 'scene', code: 'S', name: 'Scene', isSynthetic: true },
        gates: [{ id: 'gate', code: 'G1', name: 'Gate', capacity: 150, terminalId: 'terminal' }],
        checkpoints: [
          { id: 'cp', code: 'CP1', name: 'Control', capacityPerMinute: 3, terminalId: 'terminal' },
        ],
        routes: [
          {
            id: 'route',
            code: 'R',
            fromCheckpointId: 'cp',
            toGateId: 'gate',
            terminalId: 'terminal',
          },
        ],
        staff: [1, 2, 3].map((index) => ({
          id: `staff-${index}`,
          code: `OP${index}`,
          displayName: `Operator ${index}`,
          terminalId: 'terminal',
          checkpointId: 'cp',
        })),
      },
    };
    for (const preset of PRESETS) {
      const draft = makeDraft(layout, preset.id, start);
      expect(draft.inputs.minimumDepartureSpacingMinutes).toBe(
        'departureSpacingMinutes' in preset ? preset.departureSpacingMinutes : 60,
      );
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
});

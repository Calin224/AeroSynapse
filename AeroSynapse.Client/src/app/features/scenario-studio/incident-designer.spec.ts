import { TestBed } from '@angular/core/testing';
import {
  IncidentDesigner,
  incidentArrivalWindow,
  incidentProblems,
  newSurge,
  PASSENGER_SURGE,
  surgePayload,
} from './incident-designer';
import type { ScenarioInputs } from '../operations/operations.models';
import { makeDraft, StudioLayout } from './studio.models';

const layout: StudioLayout = {
  version: { id: 'v1', sceneId: 'demo', version: 1, createdAtUtc: '2026-10-16T10:00:00Z' },
  layout: {
    scene: { id: 'demo', code: 'DEMO', name: 'Aurora', isSynthetic: true },
    gates: [{ id: 'g1', code: 'G1', name: 'Poarta demo', capacity: 150, terminalId: 't1' }],
    checkpoints: [
      { id: 'cp1', code: 'CP1', name: 'Control demo', capacityPerMinute: 6, terminalId: 't1' },
    ],
    routes: [
      { id: 'r1', code: 'CP1-G1', fromCheckpointId: 'cp1', toGateId: 'g1', terminalId: 't1' },
    ],
    staff: [],
  },
};
const inputs = () => makeDraft(layout, 'principal', new Date('2026-10-16T10:00:00Z')).inputs;
describe('Incident designer', () => {
  function editor() {
    const source = inputs(),
      fixture = TestBed.createComponent(IncidentDesigner);
    fixture.componentRef.setInput('inputs', source);
    fixture.componentRef.setInput('layout', layout);
    fixture.componentRef.setInput('event', newSurge(source, layout));
    fixture.componentInstance.changed.subscribe((event) =>
      fixture.componentRef.setInput('event', event),
    );
    fixture.detectChanges();
    return fixture;
  }
  it('creates unique typed demo incidents with a consistent flight, checkpoint and gate', () => {
    const source = inputs(),
      first = newSurge(source, layout),
      second = newSurge(source, layout);
    expect(first.id).not.toEqual(second.id);
    expect(first.type).toBe(PASSENGER_SURGE);
    expect(first.flightId).toBe(source.flights[0].id);
    expect(first.checkpointId).toBe('cp1');
    expect(first.gateId).toBe('g1');
    expect(surgePayload(first)).toEqual({
      contractVersion: '1.0',
      durationMinutes: 5,
      share: 0.5,
      isSynthetic: true,
    });
  });
  it('edits numeric payloads through the form and shows affected resources and duration', async () => {
    const fixture = editor(),
      view = fixture.componentInstance,
      id = view.event().id;
    const duration = fixture.nativeElement.querySelectorAll('input')[1] as HTMLInputElement;
    duration.value = '2.5';
    duration.dispatchEvent(new Event('input'));
    await fixture.whenStable();
    fixture.detectChanges();
    const percentage = fixture.nativeElement.querySelectorAll('input')[2] as HTMLInputElement;
    percentage.value = '35';
    percentage.dispatchEvent(new Event('input'));
    await fixture.whenStable();
    fixture.detectChanges();
    const time = fixture.nativeElement.querySelector('input') as HTMLInputElement;
    time.value = '2026-10-16T13:03';
    time.dispatchEvent(new Event('input'));
    await fixture.whenStable();
    fixture.detectChanges();
    expect(view.event().id).toBe(id);
    expect(view.payload()?.share).toBe(0.35);
    expect(view.payload()?.durationMinutes).toBe(2.5);
    expect(view.event().occurredAtUtc).toBe('2026-10-16T10:03:00.000Z');
    const text = fixture.nativeElement.textContent;
    expect(text).toContain('Control demo');
    expect(text).toContain('Poarta demo');
    expect(text).toContain('13:03');
    expect(text).toContain('13:05');
    expect(text).not.toContain('minutul');
    expect(text).toContain('35%');
    expect(fixture.nativeElement.querySelector('textarea')).toBeNull();
  });
  it('preserves the other fields while a required numeric input is temporarily empty', () => {
    const fixture = editor(),
      view = fixture.componentInstance;
    view.setPayload('durationMinutes', null);
    fixture.detectChanges();
    expect(view.payload()).toBeNull();
    expect(fixture.nativeElement.textContent).toContain('Payload invalid');
    expect(fixture.nativeElement.querySelectorAll('input')).toHaveLength(3);
    view.setPayload('durationMinutes', 4);
    fixture.detectChanges();
    expect(view.payload()?.share).toBe(0.5);
    expect(view.payload()?.durationMinutes).toBe(4);
  });
  it('surfaces unsupported payload versions without silently resetting them', () => {
    const fixture = editor(),
      view = fixture.componentInstance;
    const bad = {
      ...view.event(),
      payloadJson: '{"contractVersion":"2.0","durationMinutes":5,"share":0.5,"isSynthetic":true}',
    };
    fixture.componentRef.setInput('event', bad);
    fixture.detectChanges();
    expect(view.payload()).toBeNull();
    expect(view.event().payloadJson).toBe(bad.payloadJson);
    expect(fixture.nativeElement.querySelector('[role=alert]')).not.toBeNull();
    view.resetPayload();
    fixture.detectChanges();
    expect(view.event().id).toBe(bad.id);
    expect(view.payload()?.contractVersion).toBe('1.0');
  });
  it('flags impossible intervals, shares, cancelled flights and stale targets', () => {
    const fixture = editor(),
      view = fixture.componentInstance;
    view.setPayload('share', 1.1);
    fixture.detectChanges();
    expect(view.problems().join()).toContain('100%');
    view.setTime('2026-10-16T15:59:00.000Z');
    fixture.detectChanges();
    expect(view.problems().join()).toContain('interval');
    fixture.componentRef.setInput('event', { ...view.event(), gateId: 'stale' });
    fixture.detectChanges();
    expect(view.problems().join()).toContain('Resursele');
    view.setFlight(view.inputs().flights[0].id);
    fixture.detectChanges();
    expect(view.event().gateId).toBe('g1');
    const source = structuredClone(view.inputs());
    source.flights[0].status = 'cancelled';
    fixture.componentRef.setInput('inputs', source);
    fixture.detectChanges();
    expect(view.problems().join()).toContain('zbor activ');
  });
  it('retains unknown reserved incident types as explicit errors and disables editing while busy', () => {
    const fixture = editor(),
      view = fixture.componentInstance;
    fixture.componentRef.setInput('event', { ...view.event(), type: 'incident.unknown' });
    fixture.componentRef.setInput('disabled', true);
    fixture.detectChanges();
    expect(view.problems()).toContain('Tip de incident nesuportat.');
    expect(fixture.nativeElement.querySelector('fieldset').disabled).toBe(true);
    expect(fixture.nativeElement.textContent).toContain('Tip nesuportat');
  });

  it('rejects a surge outside the flight arrival window even when it fits an eight-hour scenario', () => {
    const fixture = editor(),
      view = fixture.componentInstance;
    const source = makeDraft(layout, 'workday', new Date('2026-10-16T10:00:00Z')).inputs;
    fixture.componentRef.setInput('inputs', source);
    fixture.componentRef.setInput('event', newSurge(source, layout));
    view.setTime('2026-10-16T10:56:00.000Z');
    fixture.detectChanges();
    expect(view.arrivalWindow()).toEqual({
      start: '2026-10-16T10:00:00.000Z',
      end: '2026-10-16T10:58:00.000Z',
    });
    expect(view.problems().join()).toContain('13:58');
    expect(
      fixture.nativeElement.querySelector('[data-testid=incident-arrival-window]').textContent,
    ).toContain('13:00–13:58');
    expect(fixture.nativeElement.querySelectorAll('input')[1].max).toBe('2');
    view.setPayload('durationMinutes', 2);
    fixture.detectChanges();
    expect(view.problems()).toEqual([]);
  });

  it('uses explicit arrival profiles and delayed boarding closure, and rejects missing profiles', () => {
    const source = inputs(),
      flight = source.flights[0];
    flight.boardingClosesAtUtc = '2026-10-16T10:23:00Z';
    flight.delayMinutes = 4;
    source.passengerGeneration = {
      batches: [
        { flightId: flight.id, checkpointId: 'cp1', arrival: { minimum: 20, maximum: 30 } },
      ],
    } as ScenarioInputs['passengerGeneration'];
    expect(incidentArrivalWindow(source, flight.id)).toEqual({
      start: '2026-10-16T10:20:00.000Z',
      end: '2026-10-16T10:27:00.000Z',
    });
    const event = newSurge(source, layout);
    expect(event.occurredAtUtc).toBe('2026-10-16T10:20:00.000Z');
    expect(incidentProblems(event, source, layout)).toEqual([]);
    event.occurredAtUtc = '2026-10-16T10:19:00Z';
    expect(incidentProblems(event, source, layout).join()).toContain('13:20');
    expect(incidentArrivalWindow(source, source.flights[1].id)).toBeNull();
  });

  it('bounds the initial duration for legacy flights whose entire arrival window is shorter than five minutes', () => {
    const source = inputs(),
      flight = source.flights[0];
    flight.departureAtUtc = '2026-10-16T10:06:00Z';
    delete flight.boardingOpensAtUtc;
    delete flight.boardingClosesAtUtc;
    const event = newSurge(source, layout);
    expect(incidentArrivalWindow(source, flight.id)).toEqual({
      start: '2026-10-16T10:00:00.000Z',
      end: '2026-10-16T10:03:00.000Z',
    });
    expect(surgePayload(event)?.durationMinutes).toBe(3);
    expect(incidentProblems(event, source, layout)).toEqual([]);
  });

  it('repositions an incident on flight change without changing its duration or identity', () => {
    const fixture = editor(),
      view = fixture.componentInstance;
    view.setTime('2026-10-16T11:10:00.000Z');
    view.setFlight(view.inputs().flights[0].id);
    fixture.detectChanges();
    expect(view.event().occurredAtUtc).toBe('2026-10-16T10:53:00.000Z');
    expect(view.duration()).toBe(5);
    expect(view.problems()).toEqual([]);
  });
});

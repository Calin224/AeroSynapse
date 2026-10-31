import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { FlightSchedulePanel } from './flight-schedule-panel';
import { FlightSchedule, ScenarioDetails } from './operations.models';

const scenario: ScenarioDetails = {
  scenario: {
    id: 'scenario-1',
    code: 'demo',
    name: 'Demo',
    description: '',
    sceneId: 'scene-1',
    layoutVersionId: 'layout-1',
    revision: 7,
  },
  inputs: {
    startsAtUtc: '2026-10-13T10:00:00Z',
    durationMinutes: 90,
    seed: 42,
    timeZone: 'UTC',
    flights: [],
    populations: [],
    flows: [],
    staffAllocations: [],
    events: [],
  },
};
const schedule: FlightSchedule = {
  scenarioId: 'scenario-1',
  revision: 7,
  startsAtUtc: scenario.inputs.startsAtUtc,
  durationMinutes: 90,
  isSynthetic: true,
  gates: Array.from({ length: 5 }, (_, index) => ({
    id: `gate-${index + 1}`,
    code: `G-${index + 1}`,
    name: 'Poartă demo',
    capacity: 150,
    isSynthetic: true,
  })),
  flights: Array.from({ length: 5 }, (_, index) => ({
    id: `flight-${index + 1}`,
    code: `DEMO-${index + 1}`,
    gateId: `gate-${index + 1}`,
    departureAtUtc: `2026-10-13T11:${String(index * 5).padStart(2, '0')}:00Z`,
    boardingOpensAtUtc: '2026-10-13T10:30:00Z',
    boardingClosesAtUtc: '2026-10-13T10:55:00Z',
    passengerCapacity: 120,
    delayMinutes: 5,
    status: 'delayed',
    isSynthetic: true,
  })),
};

describe('Synthetic flight schedule controls', () => {
  let http: HttpTestingController;
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());
  function component() {
    const fixture = TestBed.createComponent(FlightSchedulePanel);
    fixture.componentRef.setInput('scenario', scenario);
    fixture.detectChanges();
    http.expectOne('/api/scenarios/scenario-1/flight-schedule').flush(schedule);
    fixture.detectChanges();
    return fixture;
  }
  it('shows five flights with gates, effective delayed times and a clear demo notice', () => {
    const fixture = component();
    const root: HTMLElement = fixture.nativeElement;
    expect(root.querySelectorAll('tbody tr')).toHaveLength(5);

    expect(root.textContent).toContain('11:05:00');
    expect(root.textContent).toContain('G-5');
  });
  it('saves an edited flight with the schedule revision, boarding cutoff and synthetic marker', () => {
    const fixture = component();
    const view = fixture.componentInstance;
    const changed = vi.fn();
    view.changed.subscribe(changed);
    view.edit(schedule.flights[0]);
    view.draft!.close = '2026-10-13T10:54:00.000Z';
    view.draft!.gateId = 'gate-2';
    view.save();
    const request = http.expectOne('/api/scenarios/scenario-1/flights/flight-1');
    expect(request.request.method).toBe('PUT');
    expect(request.request.body.revision).toBe(7);
    expect(request.request.body.flight).toMatchObject({
      boardingClosesAtUtc: '2026-10-13T10:54:00.000Z',
      gateId: 'gate-2',
      delayMinutes: 5,
      isSynthetic: true,
    });
    const updated = { ...scenario, scenario: { ...scenario.scenario, revision: 8 } };
    request.flush(updated);
    expect(changed).toHaveBeenCalledWith(updated);
    expect(view.draft).toBeNull();
    expect(view.busy()).toBe(false);
  });
  it('rejects closure at departure, delayed departure outside the horizon and unsupported dates before sending', () => {
    const view = component().componentInstance;
    view.edit(schedule.flights[0]);
    view.draft!.close = view.draft!.departure;
    view.save();
    expect(view.message()).toContain('Deschidere < închidere < plecare');
    view.draft!.close = '2026-10-13T10:55:00.000Z';
    view.draft!.delay = 31;
    view.save();
    expect(view.message()).toContain('intervalul scenariului');
    view.draft!.delay = 0;
    view.draft!.open = 'invalid';
    view.save();
    expect(view.message()).toContain('Deschidere < închidere < plecare');
    http.expectNone('/api/scenarios/scenario-1/flights/flight-1');
  });
  it('preserves the editor after a rejected update', () => {
    const view = component().componentInstance;
    view.edit(schedule.flights[0]);
    view.save();
    http
      .expectOne('/api/scenarios/scenario-1/flights/flight-1')
      .flush({}, { status: 409, statusText: 'Conflict' });
    expect(view.draft?.id).toBe('flight-1');
    expect(view.busy()).toBe(false);
  });
  it('deletes using the current revision and cancels an outdated schedule request when the scenario changes', () => {
    const fixture = component();
    const view = fixture.componentInstance;
    view.remove(schedule.flights[0]);
    const request = http.expectOne('/api/scenarios/scenario-1/flights/flight-1?revision=7');
    expect(request.request.method).toBe('DELETE');
    request.flush(scenario);
    fixture.componentRef.setInput('scenario', {
      ...scenario,
      scenario: { ...scenario.scenario, id: 'scenario-2' },
    });
    fixture.detectChanges();
    const stale = http.expectOne('/api/scenarios/scenario-2/flight-schedule');
    fixture.componentRef.setInput('scenario', scenario);
    fixture.detectChanges();
    expect(stale.cancelled).toBe(true);
    http.expectOne('/api/scenarios/scenario-1/flight-schedule').flush(schedule);
  });
});

import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { apiErrorInterceptor } from '../../core/http/api-error.interceptor';
import { EventsPanel } from './events-panel';
import { NavigationConfig, NavigationSettings } from './navigation.models';
import { ScenarioDetails } from './operations.models';

const config: NavigationConfig = { navigationVersion: '1.0',
  graph: { nodes: [{ id: 'hall-1', name: 'Coridor principal', kind: 'Walkway', x: 0, y: 0, z: 0, isActive: true, isStepFree: true, requiredTags: [] }], edges: [] },
  checkpointBindings: [], gateBindings: [], accessProfiles: [], closedZoneIds: [], closedEdgeIds: [] };
const settings: NavigationSettings = { scenarioId: 'scenario-1', revision: 4, passengerProfilesActive: true, config, defaults: config,
  checkpoints: [{ id: 'cp-1', name: 'Control A' }, { id: 'cp-2', name: 'Control B' }], gates: [] };
const event = (id: string, type: string, minute: number, extra: object = {}) => ({ id, type, gateId: null, flightId: null, checkpointId: null,
  occurredAtUtc: new Date(Date.parse('2026-10-14T10:00:00Z') + minute * 60000).toISOString(), payloadJson: '{}', ...extra });
const scenario: ScenarioDetails = { scenario: { id: 'scenario-1', code: 'demo', name: 'Demo', description: '', sceneId: 'scene-1', layoutVersionId: 'layout-1', revision: 4 },
  inputs: { startsAtUtc: '2026-10-14T10:00:00Z', durationMinutes: 60, seed: 42, timeZone: 'UTC',
    flights: [{ id: 'flight-1', code: 'DEMO-1', gateId: 'gate-1', departureAtUtc: '2026-10-14T11:00:00Z', passengerCapacity: 2 }],
    populations: [], flows: [], staffAllocations: [],
    events: [
      event('b', 'passenger_surge', 20, { checkpointId: 'cp-1', payloadJson: '{"passengerCount":30,"windowMinutes":5}' }),
      event('c', 'resume', 20, { checkpointId: 'cp-1' }),
      event('a', 'checkpoint_unavailable', 20, { checkpointId: 'cp-1' }),
      event('n', 'operator-note', 5, { payloadJson: '{"note":"liber"}' }),
    ] } };

describe('Scheduled operational events', () => {
  let http: HttpTestingController;
  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(withInterceptors([apiErrorInterceptor])), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());
  function component() {
    const fixture = TestBed.createComponent(EventsPanel); fixture.componentRef.setInput('scenario', structuredClone(scenario));
    fixture.componentRef.setInput('active', true); fixture.detectChanges();
    http.expectOne('/api/scenarios/scenario-1/navigation/settings').flush(structuredClone(settings)); fixture.detectChanges(); return fixture;
  }

  it('lists simultaneous events in the engine order and keeps notes out of the timeline', () => {
    const fixture = component();
    const rows = [...(fixture.nativeElement as HTMLElement).querySelectorAll('tbody tr')].map((row) => row.textContent ?? '');
    expect(rows).toHaveLength(3);
    expect(rows[0]).toContain('Control indisponibil'); expect(rows[1]).toContain('Reluare'); expect(rows[2]).toContain('Val de pasageri');
    expect(rows[2]).toContain('30 pasageri în 5 min');
    expect(fixture.nativeElement.textContent).toContain('1 note descriptive');
  });

  it('schedules an event at the chosen virtual minute with only the fields its type uses', () => {
    const view = component().componentInstance; const changed = vi.fn(); view.changed.subscribe(changed);
    view.add(); view.draft = { ...view.draft!, type: 'staff_reduction', minute: 15, checkpointId: 'cp-2', staffCount: 1 };
    view.save();
    const request = http.expectOne('/api/scenarios/scenario-1/events');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toMatchObject({ revision: 4, event: { type: 'staff_reduction', occurredAtUtc: '2026-10-14T10:15:00.000Z',
      checkpointId: 'cp-2', flightId: null, gateId: null, payloadJson: '{"staffCount":1}' } });
    request.flush(scenario);
    expect(changed).toHaveBeenCalledWith(scenario); expect(view.draft).toBeNull();
  });

  it('shows explicit server conflicts and rejects minutes outside the horizon before sending', () => {
    const view = component().componentInstance;
    view.add(); view.draft = { ...view.draft!, minute: 60 }; view.save();
    expect(view.message()).toContain('între 0 și 59'); http.expectNone('/api/scenarios/scenario-1/events');
    view.draft = { ...view.draft!, type: 'resume', target: 'zone', zoneId: 'hall-1', minute: 10 }; view.save();
    const request = http.expectOne('/api/scenarios/scenario-1/events');
    expect(JSON.parse(request.request.body.event.payloadJson)).toEqual({ zoneId: 'hall-1' });
    request.flush({ errors: { 'inputs.events[4].payloadJson': ['The event contradicts the scheduled timeline.'] } }, { status: 400, statusText: 'Bad Request' });
    expect(view.message()).toContain('contradicts the scheduled timeline'); expect(view.draft).not.toBeNull();
  });

  it('refreshes targets after scenario changes and clears drafts when switching scenarios', () => {
    const fixture = component(); const view = fixture.componentInstance;
    view.add();
    fixture.componentRef.setInput('scenario', { ...scenario, scenario: { ...scenario.scenario, revision: 5 } });
    fixture.detectChanges();
    expect(view.blocked()).toBe(true);
    http.expectOne('/api/scenarios/scenario-1/navigation/settings').flush({ ...settings, revision: 5 });
    fixture.detectChanges();
    expect(view.draft).not.toBeNull();
    fixture.componentRef.setInput('scenario', { ...scenario, scenario: { ...scenario.scenario, id: 'scenario-2' } });
    fixture.detectChanges();
    expect(view.draft).toBeNull(); expect(view.settings()).toBeNull();
    http.expectOne('/api/scenarios/scenario-2/navigation/settings').flush({ ...settings, scenarioId: 'scenario-2' });
    fixture.detectChanges();
    expect(view.blocked()).toBe(false);
  });
});

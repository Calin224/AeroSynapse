import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { apiErrorInterceptor } from '../../core/http/api-error.interceptor';
import { ScenarioDetails } from './operations.models';
import { ResourcePanel } from './resource-panel';
import { IntegratedResourceConfig, ResourceSettings } from './resource.models';

const resources: IntegratedResourceConfig = { contractVersion: '1.0',
  gates: [{ gateId: 'gate', availability: [{ startMinute: 0, endMinute: 60 }] }],
  workers: [{ staffMemberId: 'worker', skills: ['boarding'], shifts: [{ startMinute: 0, endMinute: 60 }] }],
  teams: [{ teamId: 'team', name: 'Handling', memberIds: ['worker'], gateIds: ['gate'] }],
  requirements: [{ flightId: 'flight', compatibleGateIds: ['gate'], requiredSkills: ['boarding'], minimumTeamSize: 1 }] };
const scenario: ScenarioDetails = { scenario: { id: 'scenario', revision: 3, name: 'Demo', code: 'DEMO', description: '', sceneId: 'scene', layoutVersionId: 'layout' },
  inputs: { startsAtUtc: '2026-10-17T08:00:00Z', durationMinutes: 60, seed: 42, timeZone: 'UTC', flights: [], populations: [], flows: [], staffAllocations: [], events: [] } };
const settings: ResourceSettings = { scenarioId: 'scenario', revision: 3, startsAtUtc: scenario.inputs.startsAtUtc, durationMinutes: 60,
  compatible: true, incompatibilityReason: null, resources,
  flights: [{ id: 'flight', code: 'DEMO 1', passengerCapacity: 100, gateId: 'gate', departureAtUtc: '2026-10-17T08:40:00Z', boardingOpensAtUtc: '2026-10-17T08:10:00Z' }],
  gates: [{ id: 'gate', name: 'Poarta A', terminalId: 'terminal' }],
  staff: [{ id: 'worker', name: 'Operator A', terminalId: 'terminal', reservedForSecurity: false },
    { id: 'security', name: 'Operator securitate', terminalId: 'terminal', reservedForSecurity: true }] };

describe('Integrated resource planning', () => {
  let http: HttpTestingController;
  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(withInterceptors([apiErrorInterceptor])), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());
  function component(value = settings) {
    const fixture = TestBed.createComponent(ResourcePanel);
    fixture.componentRef.setInput('scenario', structuredClone(scenario)); fixture.componentRef.setInput('active', true); fixture.detectChanges();
    http.expectOne('/api/scenarios/scenario/resources/settings').flush(structuredClone(value)); fixture.detectChanges();
    return fixture;
  }
  it('starts with explicitly empty availability and shifts and excludes security staff', () => {
    const fixture = component({ ...settings, resources: null }); const view = fixture.componentInstance;
    expect(view.draft?.gates).toEqual([]);
    view.includeGate('gate'); view.includeWorker('worker'); view.includeWorker('security');
    expect(view.draft?.gates[0].availability).toEqual([]);
    expect(view.draft?.workers.map(worker => worker.staffMemberId)).toEqual(['worker']);
    expect(view.draft?.workers[0].shifts).toEqual([]);
    expect(fixture.nativeElement.textContent).toContain('Model integrat dezactivat');
    view.propose(); http.expectNone('/api/scenarios/scenario/resources/proposals'); fixture.destroy();
  });
  it('can extend a saved subset with captured resources and keeps undeclared gates out of compatibility choices', () => {
    const fixture = component({ ...settings, gates: [...settings.gates, { id: 'gate-b', name: 'Poarta B', terminalId: 'terminal' }],
      staff: [...settings.staff, { id: 'worker-b', name: 'Operator B', terminalId: 'terminal', reservedForSecurity: false }] });
    const view = fixture.componentInstance;
    expect(fixture.nativeElement.querySelectorAll('select')[1].textContent).not.toContain('Poarta B');
    view.includeGate('gate-b'); view.includeWorker('worker-b'); view.includeWorker('security'); fixture.detectChanges();
    expect(fixture.nativeElement.querySelectorAll('select')[1].textContent).toContain('Poarta B');
    expect(view.draft?.workers.map(item => item.staffMemberId)).toEqual(['worker', 'worker-b']);
    expect(view.draft?.gates[1].availability).toEqual([]); fixture.destroy();
  });
  it('keeps references consistent when removing resources from an existing draft', () => {
    const fixture = component(); const view = fixture.componentInstance;
    view.removeGate('gate'); view.removeWorker('worker');
    expect(view.draft?.teams[0].gateIds).toEqual([]); expect(view.draft?.teams[0].memberIds).toEqual([]);
    expect(view.draft?.requirements[0].compatibleGateIds).toEqual([]);
    view.save(); http.expectNone('/api/scenarios/scenario/resources/settings'); fixture.destroy();
  });
  it('invalidates an existing proposal on input, before focus leaves the edited field', async () => {
    const fixture = component(); const view = fixture.componentInstance;
    view.result.set({ status: 'timeout', solverStatus: 'UNKNOWN', timedOut: true, score: null, bestBound: null,
      assignments: [], rules: [], advisoryOnly: true, wallTimeSeconds: 0,
      constraints: { scenarioId: 'scenario', scenarioRevision: 3, resources,
        flights: [{ flightId: 'flight', currentGateId: 'gate', startMinute: 10, endMinute: 40 }] } });
    fixture.detectChanges();
    const input: HTMLInputElement = fixture.nativeElement.querySelector('input[type=number]');
    input.value = '12.5'; input.dispatchEvent(new Event('input', { bubbles: true }));
    fixture.detectChanges(); await fixture.whenStable();
    expect(view.result()).toBeNull(); expect(view.dirty()).toBe(true);
    expect(view.draft?.gates[0].availability[0].startMinute).toBe(12.5);
    view.propose(); http.expectNone('/api/scenarios/scenario/resources/proposals'); fixture.destroy();
  });
  it('shows the captured assignment interval and people without applying the proposal', () => {
    const fixture = component(); const view = fixture.componentInstance; const changed = vi.fn(); view.changed.subscribe(changed);
    view.propose();
    http.expectOne('/api/scenarios/scenario/resources/proposals').flush({ status: 'optimal', advisoryOnly: true, score: 0, bestBound: 0,
      assignments: [{ flightId: 'flight', gateId: 'gate', teamId: 'team' }], rules: [],
      constraints: { scenarioId: 'scenario', scenarioRevision: 3, resources, flights: [{ flightId: 'flight', startMinute: 10, endMinute: 40 }] } });
    fixture.detectChanges(); const row: HTMLTableRowElement = fixture.nativeElement.querySelector('tbody tr');
    expect(row.textContent).toContain('10–40 min'); expect(row.textContent).toContain('Operator A');
    expect(changed).not.toHaveBeenCalled(); fixture.destroy();
  });
  it('saves explicit configuration and current revision and emits the updated scenario', () => {
    const fixture = component(); const view = fixture.componentInstance; const changed = vi.fn();
    view.changed.subscribe(changed); view.edit(); view.save();
    const call = http.expectOne('/api/scenarios/scenario/resources/settings');
    expect(call.request.method).toBe('PUT'); expect(call.request.body).toEqual({ revision: 3, resources });
    const saved = { ...scenario, scenario: { ...scenario.scenario, revision: 4 } }; call.flush(saved);
    expect(changed).toHaveBeenCalledWith(saved); expect(view.busy()).toBe(false); fixture.destroy();
  });
  it('shows concrete infeasible rules and blocks proposals with unsaved edits', () => {
    const fixture = component(); const view = fixture.componentInstance;
    view.edit(); view.propose(); http.expectNone('/api/scenarios/scenario/resources/proposals'); view.dirty.set(false); view.propose();
    const call = http.expectOne('/api/scenarios/scenario/resources/proposals');
    expect(call.request.body).toEqual({ revision: 3, timeLimitSeconds: 5 });
    call.flush({ status: 'infeasible', advisoryOnly: true, score: null, assignments: [],
      rules: [{ code: 'team_shift', flightId: 'flight', resourceId: 'team', message: 'Members outside their shifts.' }],
      constraints: { scenarioId: 'scenario', scenarioRevision: 3 } }); fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Infezabil');
    expect(fixture.nativeElement.textContent).toContain('team_shift · DEMO 1 Handling');
    expect(fixture.nativeElement.textContent).toContain('Members outside their shifts.');
    view.addWindow(view.draft!.gates[0].availability); expect(view.result()).toBeNull(); fixture.destroy();
  });
  it('cancels an obsolete proposal and reloads settings when the revision changes', () => {
    const fixture = component(); const view = fixture.componentInstance; view.propose();
    const call = http.expectOne('/api/scenarios/scenario/resources/proposals');
    fixture.componentRef.setInput('scenario', { ...scenario, scenario: { ...scenario.scenario, revision: 4 } }); fixture.detectChanges();
    expect(call.cancelled).toBe(true); expect(view.result()).toBeNull(); expect(view.busy()).toBe(false);
    http.expectOne('/api/scenarios/scenario/resources/settings').flush({ ...settings, revision: 4 }); fixture.detectChanges(); fixture.destroy();
  });
  it('preserves a pending request on refresh of the same revision and shows timeout honestly', () => {
    const fixture = component(); const view = fixture.componentInstance; view.propose();
    const call = http.expectOne('/api/scenarios/scenario/resources/proposals');
    fixture.componentRef.setInput('scenario', structuredClone(scenario)); fixture.detectChanges(); expect(call.cancelled).toBe(false);
    call.flush({ status: 'timeout', advisoryOnly: true, score: null, rules: [], assignments: [], constraints: { scenarioId: 'scenario', scenarioRevision: 3 } });
    fixture.detectChanges(); expect(fixture.nativeElement.textContent).toContain('Timp expirat fără o soluție'); fixture.destroy();
  });
  it('rejects mismatched settings and proposals and permits retry after an error', () => {
    const fixture = component({ ...settings, revision: 4 }); const view = fixture.componentInstance;
    expect(view.settings()).toBeNull(); view.load();
    http.expectOne('/api/scenarios/scenario/resources/settings').flush({}, { status: 503, statusText: 'Unavailable' });
    view.load(); http.expectOne('/api/scenarios/scenario/resources/settings').flush(settings);
    view.propose(); http.expectOne('/api/scenarios/scenario/resources/proposals').flush({ advisoryOnly: true, constraints: { scenarioId: 'other', scenarioRevision: 3 } });
    expect(view.result()).toBeNull(); expect(view.message()).toContain('nu corespunde'); fixture.destroy();
  });
  it('blocks incompatible scenarios and allows explicit deactivation without discarding the draft on errors', () => {
    const fixture = component({ ...settings, compatible: false, incompatibilityReason: 'Dynamic schedule' }); const view = fixture.componentInstance;
    expect(fixture.nativeElement.querySelector('fieldset').disabled).toBe(true);
    view.propose(); view.save(); http.expectNone('/api/scenarios/scenario/resources/proposals');
    view.save(true); const call = http.expectOne('/api/scenarios/scenario/resources/settings');
    expect(call.request.body).toEqual({ revision: 3, resources: null });
    call.flush({}, { status: 409, statusText: 'Conflict' });
    expect(view.draft).toEqual(resources); expect(view.busy()).toBe(false); fixture.destroy();
  });
});

import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { apiErrorInterceptor } from '../../core/http/api-error.interceptor';
import { ScenarioDetails } from './operations.models';
import { ResourcePanel } from './resource-panel';
import { IntegratedResourceConfig, ResourceSettings } from './resource.models';

const resources: IntegratedResourceConfig = {
  contractVersion: '1.0',
  gates: [{ gateId: 'gate', availability: [{ startMinute: 0, endMinute: 60 }] }],
  workers: [
    { staffMemberId: 'worker', skills: ['boarding'], shifts: [{ startMinute: 0, endMinute: 60 }] },
  ],
  teams: [{ teamId: 'team', name: 'Handling', memberIds: ['worker'], gateIds: ['gate'] }],
  requirements: [
    {
      flightId: 'flight',
      compatibleGateIds: ['gate'],
      requiredSkills: ['boarding'],
      minimumTeamSize: 1,
    },
  ],
};
const scenario: ScenarioDetails = {
  scenario: {
    id: 'scenario',
    revision: 3,
    name: 'Demo',
    code: 'DEMO',
    description: '',
    sceneId: 'scene',
    layoutVersionId: 'layout',
  },
  inputs: {
    startsAtUtc: '2026-10-17T08:00:00Z',
    durationMinutes: 60,
    seed: 42,
    timeZone: 'UTC',
    flights: [],
    populations: [],
    flows: [],
    staffAllocations: [],
    events: [],
  },
};
const settings: ResourceSettings = {
  scenarioId: 'scenario',
  revision: 3,
  startsAtUtc: scenario.inputs.startsAtUtc,
  durationMinutes: 60,
  compatible: true,
  incompatibilityReason: null,
  resources,
  flights: [
    {
      id: 'flight',
      code: 'DEMO 1',
      passengerCapacity: 100,
      gateId: 'gate',
      departureAtUtc: '2026-10-17T08:40:00Z',
      boardingOpensAtUtc: '2026-10-17T08:10:00Z',
    },
  ],
  gates: [
    { id: 'gate', name: 'Poarta A', terminalId: 'terminal' },
    { id: 'gate-b', name: 'Poarta B', terminalId: 'terminal' },
  ],
  staff: [
    { id: 'worker', name: 'Operator A', terminalId: 'terminal', reservedForSecurity: false },
    { id: 'worker-b', name: 'Operator B', terminalId: 'terminal', reservedForSecurity: false },
    { id: 'worker-c', name: 'Operator C', terminalId: 'terminal', reservedForSecurity: false },
    {
      id: 'security',
      name: 'Operator securitate',
      terminalId: 'terminal',
      reservedForSecurity: true,
    },
  ],
};
const proposal = (gateId: string, revision = 3) => ({
  status: 'optimal',
  advisoryOnly: true,
  score: gateId === 'gate' ? 0 : 1,
  bestBound: 0,
  assignments: [{ flightId: 'flight', gateId, teamId: 'team' }],
  rules: [],
  constraints: {
    scenarioId: 'scenario',
    scenarioRevision: revision,
    resources,
    flights: [{ flightId: 'flight', currentGateId: 'gate', startMinute: 10, endMinute: 40 }],
  },
});
const revised = (
  revision: number,
  inputs: Partial<ScenarioDetails['inputs']> = {},
): ScenarioDetails => ({
  ...scenario,
  scenario: { ...scenario.scenario, revision },
  inputs: { ...scenario.inputs, ...inputs },
});

describe('Integrated resource planning', () => {
  let http: HttpTestingController;
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([apiErrorInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());
  function component(value = settings) {
    const fixture = TestBed.createComponent(ResourcePanel);
    fixture.componentRef.setInput('scenario', structuredClone(scenario));
    fixture.componentRef.setInput('active', true);
    fixture.detectChanges();
    http.expectOne('/api/scenarios/scenario/resources/settings').flush(structuredClone(value));
    fixture.detectChanges();
    return fixture;
  }
  it('starts a plan that works as is: gates open all day, free people in pairs, security staff left out', () => {
    const fixture = component({ ...settings, resources: null });
    const view = fixture.componentInstance;
    expect(view.draft?.gates).toEqual([
      { gateId: 'gate', availability: [{ startMinute: 0, endMinute: 60 }] },
      { gateId: 'gate-b', availability: [{ startMinute: 0, endMinute: 60 }] },
    ]);
    expect(view.draft?.teams.map((team) => [team.name, team.memberIds, team.gateIds])).toEqual([
      ['Echipa 1', ['worker', 'worker-b'], ['gate', 'gate-b']],
      ['Echipa 2', ['worker-c'], ['gate', 'gate-b']],
    ]);
    expect(view.draft?.workers.map((worker) => [worker.staffMemberId, worker.shifts])).toEqual(
      ['worker', 'worker-b', 'worker-c'].map((id) => [id, [{ startMinute: 0, endMinute: 60 }]]),
    );
    expect(view.draft?.requirements).toEqual([
      {
        flightId: 'flight',
        compatibleGateIds: ['gate', 'gate-b'],
        requiredSkills: [],
        minimumTeamSize: 1,
      },
    ]);
    expect(fixture.nativeElement.textContent).toContain('Plan nou');
    expect(fixture.nativeElement.textContent).not.toContain('Operator securitate');
    fixture.destroy();
  });
  it('saves a new plan, keeps it through its own revision and asks for a proposal on that revision', () => {
    const fixture = component({ ...settings, resources: null });
    const view = fixture.componentInstance;
    const changed = vi.fn();
    view.changed.subscribe(changed);
    view.calculate();
    const save = http.expectOne('/api/scenarios/scenario/resources/settings');
    expect(save.request.method).toBe('PUT');
    expect(save.request.body).toEqual({ revision: 3, resources: view.draft });
    const saved = revised(4, { integratedResources: view.draft });
    save.flush(saved);
    expect(changed).toHaveBeenCalledWith(saved);
    const solve = http.expectOne('/api/scenarios/scenario/resources/proposals');
    expect(solve.request.body).toEqual({ revision: 4, timeLimitSeconds: 5 });
    fixture.componentRef.setInput('scenario', saved);
    fixture.detectChanges();
    expect(solve.cancelled).toBe(false);
    http.expectNone('/api/scenarios/scenario/resources/settings');
    solve.flush(proposal('gate', 4));
    fixture.detectChanges();
    expect(view.result()?.status).toBe('optimal');
    expect(view.busy()).toBe(false);
    expect(fixture.nativeElement.textContent).toContain('Plan salvat');
    fixture.destroy();
  });
  it('calculates a saved, unchanged plan without saving it again', () => {
    const fixture = component();
    const view = fixture.componentInstance;
    view.calculate();
    http.expectNone('/api/scenarios/scenario/resources/settings');
    http.expectOne('/api/scenarios/scenario/resources/proposals').flush(proposal('gate'));
    fixture.detectChanges();
    const text = fixture.nativeElement.textContent;
    expect(text).toContain('niciun zbor nu trebuie mutat');
    expect(text).toContain('08:10–08:40');
    expect(text).toContain('Operator A');
    expect(fixture.nativeElement.querySelector('tr.moved')).toBeNull();
    expect(text).not.toContain('Mută');
    fixture.destroy();
  });
  it('shows the flights to move and moves them in the schedule in one call', () => {
    const fixture = component();
    const view = fixture.componentInstance;
    const changed = vi.fn();
    view.changed.subscribe(changed);
    view.calculate();
    http.expectOne('/api/scenarios/scenario/resources/proposals').flush(proposal('gate-b'));
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Un zbor trebuie mutat');
    expect(fixture.nativeElement.querySelector('tr.moved').textContent).toContain(
      'Poarta A → Poarta B',
    );
    const button = [...fixture.nativeElement.querySelectorAll('button')].find(
      (item: HTMLButtonElement) => item.textContent!.includes('Mută'),
    ) as HTMLButtonElement;
    button.click();
    const call = http.expectOne('/api/scenarios/scenario/resources/apply');
    expect(call.request.body).toEqual({
      revision: 3,
      flights: [{ flightId: 'flight', gateId: 'gate-b' }],
    });
    const moved = revised(4, {
      flights: [{ ...settings.flights[0], gateId: 'gate-b' }],
      integratedResources: resources,
    });
    call.flush(moved);
    fixture.detectChanges();
    expect(changed).toHaveBeenCalledWith(moved);
    expect(view.result()).toBeNull();
    expect(view.settings()?.revision).toBe(4);
    expect(view.settings()?.flights[0].gateId).toBe('gate-b');
    expect(fixture.nativeElement.textContent).toContain('Am mutat un zbor');
    fixture.destroy();
  });
  it('gives new flights the default rule and drops rules of removed flights', () => {
    const fixture = component({
      ...settings,
      resources: {
        ...resources,
        requirements: [
          {
            flightId: 'removed',
            compatibleGateIds: ['gate'],
            requiredSkills: [],
            minimumTeamSize: 2,
          },
        ],
      },
    });
    expect(fixture.componentInstance.draft?.requirements).toEqual([
      { flightId: 'flight', compatibleGateIds: ['gate'], requiredSkills: [], minimumTeamSize: 1 },
    ]);
    fixture.destroy();
  });
  it('keeps teams and flights consistent when a gate is turned off and another on', () => {
    const fixture = component();
    const view = fixture.componentInstance;
    view.removeGate('gate');
    expect(view.draft?.teams[0].gateIds).toEqual([]);
    expect(view.draft?.requirements[0].compatibleGateIds).toEqual([]);
    view.includeGate('gate-b');
    expect(view.draft?.gates).toEqual([
      { gateId: 'gate-b', availability: [{ startMinute: 0, endMinute: 60 }] },
    ]);
    expect(view.draft?.requirements[0].compatibleGateIds).toEqual(['gate-b']);
    expect(view.dirty()).toBe(true);
    fixture.destroy();
  });
  it('derives workers from team members and keeps saved skills and shifts', () => {
    const fixture = component();
    const view = fixture.componentInstance;
    const team = view.draft!.teams[0];
    view.toggleMember(team, 'worker-b');
    expect(view.draft?.workers).toEqual([
      resources.workers[0],
      { staffMemberId: 'worker-b', skills: [], shifts: [{ startMinute: 0, endMinute: 60 }] },
    ]);
    view.toggleMember(team, 'worker-b');
    expect(view.draft?.workers).toEqual(resources.workers);
    fixture.destroy();
  });
  it('picks opening hours on a 15-minute clock and drops a stale proposal on change', async () => {
    const fixture = component();
    const view = fixture.componentInstance;
    view.result.set(proposal('gate') as never);
    fixture.detectChanges();
    const select: HTMLSelectElement = fixture.nativeElement.querySelector('.window select');
    expect([...select.options].map((option) => option.textContent!.trim())).toEqual([
      '08:00',
      '08:15',
      '08:30',
      '08:45',
      '09:00',
    ]);
    select.value = select.options[1].value;
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    await fixture.whenStable();
    expect(view.draft?.gates[0].availability[0].startMinute).toBe(15);
    expect(view.result()).toBeNull();
    expect(view.dirty()).toBe(true);
    http.expectNone('/api/scenarios/scenario/resources/proposals');
    fixture.destroy();
  });
  it('explains impossible rules in plain words', () => {
    const fixture = component();
    const view = fixture.componentInstance;
    view.calculate();
    http
      .expectOne('/api/scenarios/scenario/resources/proposals')
      .flush({
        status: 'infeasible',
        advisoryOnly: true,
        score: null,
        assignments: [],
        rules: [
          {
            code: 'team_shift',
            flightId: 'flight',
            resourceId: 'team',
            message: 'Members outside their shifts.',
          },
        ],
        constraints: { scenarioId: 'scenario', scenarioRevision: 3 },
      });
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Nu se poate');
    expect(fixture.nativeElement.textContent).toContain(
      'DEMO 1 · Handling: unii membri nu sunt în tură în timpul zborului',
    );
    view.addWindow(view.draft!.gates[0].availability);
    expect(view.result()).toBeNull();
    fixture.destroy();
  });
  it('cancels an obsolete proposal and reloads settings when the revision changes elsewhere', () => {
    const fixture = component();
    const view = fixture.componentInstance;
    view.calculate();
    const call = http.expectOne('/api/scenarios/scenario/resources/proposals');
    fixture.componentRef.setInput('scenario', revised(4));
    fixture.detectChanges();
    expect(call.cancelled).toBe(true);
    expect(view.result()).toBeNull();
    expect(view.busy()).toBe(false);
    http
      .expectOne('/api/scenarios/scenario/resources/settings')
      .flush({ ...settings, revision: 4 });
    fixture.detectChanges();
    fixture.destroy();
  });
  it('preserves a pending request on refresh of the same revision and shows timeout honestly', () => {
    const fixture = component();
    const view = fixture.componentInstance;
    view.calculate();
    const call = http.expectOne('/api/scenarios/scenario/resources/proposals');
    fixture.componentRef.setInput('scenario', structuredClone(scenario));
    fixture.detectChanges();
    expect(call.cancelled).toBe(false);
    call.flush({
      status: 'timeout',
      advisoryOnly: true,
      score: null,
      rules: [],
      assignments: [],
      constraints: { scenarioId: 'scenario', scenarioRevision: 3 },
    });
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain(
      'Timpul de calcul s-a terminat fără o soluție',
    );
    fixture.destroy();
  });
  it('rejects mismatched settings and proposals and permits retry after an error', () => {
    const fixture = component({ ...settings, revision: 4 });
    const view = fixture.componentInstance;
    expect(view.settings()).toBeNull();
    view.load();
    http
      .expectOne('/api/scenarios/scenario/resources/settings')
      .flush({}, { status: 503, statusText: 'Unavailable' });
    view.load();
    http.expectOne('/api/scenarios/scenario/resources/settings').flush(settings);
    view.calculate();
    http
      .expectOne('/api/scenarios/scenario/resources/proposals')
      .flush({ advisoryOnly: true, constraints: { scenarioId: 'other', scenarioRevision: 3 } });
    expect(view.result()).toBeNull();
    expect(view.message()).toContain('nu corespunde');
    fixture.destroy();
  });
  it('blocks incompatible scenarios but still lets the saved plan be removed, keeping the draft on errors', () => {
    const fixture = component({
      ...settings,
      compatible: false,
      incompatibilityReason: 'Dynamic schedule',
    });
    const view = fixture.componentInstance;
    expect(fixture.nativeElement.querySelector('fieldset').disabled).toBe(true);
    view.calculate();
    http.expectNone('/api/scenarios/scenario/resources/proposals');
    view.deactivate();
    const call = http.expectOne('/api/scenarios/scenario/resources/settings');
    expect(call.request.body).toEqual({ revision: 3, resources: null });
    call.flush({}, { status: 409, statusText: 'Conflict' });
    expect(view.draft).toEqual(resources);
    expect(view.busy()).toBe(false);
    expect(view.message()).not.toBe('');
    fixture.destroy();
  });
});

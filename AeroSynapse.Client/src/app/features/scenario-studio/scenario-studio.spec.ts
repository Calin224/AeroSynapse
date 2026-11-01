import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { apiErrorInterceptor } from '../../core/http/api-error.interceptor';
import { SimulationContext } from '../../core/simulation-context';
import { ScenarioDetails } from '../operations/operations.models';
import { ScenarioStudio } from './scenario-studio';
import { fromScenario, makeDraft, shiftStart, StudioLayout } from './studio.models';
import { ObjectFields } from './object-fields';
import { IncidentDesigner, surgePayload } from './incident-designer';
import { By } from '@angular/platform-browser';

const layout: StudioLayout = {
  version: { id: 'layout-7', sceneId: 'scene', version: 7, createdAtUtc: '2026-10-16T10:00:00Z' },
  layout: {
    scene: { id: 'scene', code: 'DEMO', name: 'Aurora', isSynthetic: true },
    gates: [{ id: 'gate', code: 'G1', name: 'Poarta demo', capacity: 150, terminalId: 'terminal' }],
    checkpoints: [
      {
        id: 'checkpoint',
        code: 'CP1',
        name: 'Control demo',
        capacityPerMinute: 6,
        terminalId: 'terminal',
      },
    ],
    routes: [
      {
        id: 'route',
        code: 'CP1-G1',
        fromCheckpointId: 'checkpoint',
        toGateId: 'gate',
        terminalId: 'terminal',
      },
    ],
    staff: [
      {
        id: 'staff',
        code: 'ST1',
        displayName: 'Operator demo',
        terminalId: 'terminal',
        checkpointId: 'checkpoint',
      },
    ],
  },
};
const saved = (): ScenarioDetails => {
  const value = makeDraft(layout, 'principal', new Date('2026-10-16T10:00:00Z'));
  value.inputs.events = [
    {
      id: 'event',
      type: 'demo-note',
      occurredAtUtc: '2026-10-16T10:05:00.123Z',
      checkpointId: 'checkpoint',
      gateId: null,
      flightId: value.inputs.flights[0].id,
      payloadJson: '{"lanes":2,"metadata":{"note":"păstrează","active":true},"counts":[1,2]}',
    },
  ];
  value.inputs.passengerGeneration = { generatorVersion: '1.0', batches: [], flow: null };
  value.inputs.navigation = {
    navigationVersion: '1.0',
    graph: { nodes: [], edges: [] },
    checkpointBindings: [],
    gateBindings: [],
    accessProfiles: [],
    closedZoneIds: ['preserve-zone'],
    closedEdgeIds: [],
  };
  return {
    scenario: {
      id: 'original',
      code: value.code,
      name: value.name,
      description: value.description,
      sceneId: value.sceneId,
      layoutVersionId: value.layoutVersionId,
      revision: 4,
      isSynthetic: true,
    },
    inputs: value.inputs,
  };
};

describe('Scenario Studio', () => {
  let http: HttpTestingController;
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(withInterceptors([apiErrorInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());
  function component() {
    const fixture = TestBed.createComponent(ScenarioStudio);
    fixture.detectChanges();
    http.expectOne('/api/scenes').flush([layout.layout.scene]);
    fixture.detectChanges();
    return fixture;
  }
  function principal(view: ScenarioStudio) {
    view.newScenario('principal');
    http.expectOne('/api/scenes/scene/layout-versions').flush(layout);
  }
  function load(view: ScenarioStudio, value = saved()) {
    view.select(value.scenario.id);
    http.expectOne('/api/scenarios/' + value.scenario.id).flush(value);
    http.expectOne('/api/scenes/scene/layout-versions/layout-7').flush(layout);
    return value;
  }
  it('loads the primary preset in one click with five coherent flights and bounded nominal staff', () => {
    const fixture = component(),
      view = fixture.componentInstance;
    (fixture.nativeElement.querySelector('button.principal') as HTMLButtonElement).click();
    http.expectOne('/api/scenes/scene/layout-versions').flush(layout);
    fixture.detectChanges();
    expect(view.draft!.inputs.flights).toHaveLength(5);
    expect(view.draft!.inputs.populations.reduce((sum, row) => sum + row.count, 0)).toBe(600);
    expect(view.draft!.inputs.staffAllocations).toEqual([
      { staffMemberId: 'staff', checkpointId: 'checkpoint' },
    ]);
    expect(
      view.draft!.inputs.flows.every((flow) =>
        view.draft!.inputs.flights.some((flight) => flight.id === flow.flightId),
      ),
    ).toBe(true);
    view.save();
    const request = http.expectOne('/api/scenarios');
    expect(request.request.method).toBe('POST');
    expect(request.request.body.inputs).toEqual(view.draft!.inputs);
    expect(request.request.body.revision).toBeUndefined();
    request.flush({}, { status: 503, statusText: 'Unavailable' });
    expect(view.dirty()).toBe(true);
  });
  it('starts demo presets with the check-in desks of the layout', () => {
    const desks = {
      id: 'desks',
      code: 'CHECK-IN',
      name: 'Check-in',
      deskCount: 12,
      serviceSeconds: 120,
      walkingSeconds: 90,
      terminalId: 'terminal',
    };
    const withDesks: StudioLayout = {
      ...layout,
      layout: { ...layout.layout, checkInAreas: [desks] },
    };
    const start = new Date('2026-10-16T10:00:00Z');
    expect(makeDraft(withDesks, 'principal', start).inputs.checkIn).toEqual({
      areaId: 'desks',
      staffCount: 8,
      holdBaggageShare: 0.4,
    });
    // The Bottleneck guide isolates one control; older layouts have no desks.
    const threeOperators = [
      ...layout.layout.staff,
      { ...layout.layout.staff[0], id: 'staff-2', code: 'ST2' },
      { ...layout.layout.staff[0], id: 'staff-3', code: 'ST3' },
    ];
    const bottleneck: StudioLayout = {
      ...withDesks,
      layout: { ...withDesks.layout, staff: threeOperators },
    };
    expect(makeDraft(bottleneck, 'bottleneck', start).inputs.checkIn).toBeUndefined();
    expect(makeDraft(layout, 'principal', start).inputs.checkIn).toBeUndefined();
  });
  it('fills in the team of an older scenario as an unsaved change', () => {
    const fixture = component(),
      view = fixture.componentInstance,
      older = saved();
    // An older scenario may have no team at all, or gate operators from before that role was removed.
    older.inputs.operators = [{ id: 'g1', name: 'Poartă', role: 'gate' as never }];
    load(view, older);
    expect(view.operators('security').map((person) => person.name)).toEqual(['Operator demo']);
    expect(view.draft!.inputs.operators!.some((person) => person.id === 'g1')).toBe(false);
    expect(view.dirty()).toBe(true);
  });
  it('keeps one named team: security fills layout slots, check-in opens desks', () => {
    const desks = {
      id: 'desks',
      code: 'CHECK-IN',
      name: 'Check-in',
      deskCount: 2,
      serviceSeconds: 120,
      walkingSeconds: 90,
      terminalId: 'terminal',
    };
    const withDesks: StudioLayout = {
      ...layout,
      layout: { ...layout.layout, checkInAreas: [desks] },
    };
    const fixture = component(),
      view = fixture.componentInstance;
    (fixture.nativeElement.querySelector('button.principal') as HTMLButtonElement).click();
    http.expectOne('/api/scenes/scene/layout-versions').flush(withDesks);
    fixture.detectChanges();
    const roles = () => view.draft!.inputs.operators!.map((person) => person.role);
    // The preset names the placed slot and both open desks.
    expect(roles()).toEqual(['security', 'checkIn', 'checkIn']);
    expect(view.operators('security')[0]).toEqual({
      id: 'staff',
      name: 'Operator demo',
      role: 'security',
    });
    expect(view.canAdd('security')).toBe(false);
    expect(view.canAdd('checkIn')).toBe(false);
    view.removeOperator(view.operators('checkIn')[0]);
    expect(view.draft!.inputs.checkIn!.staffCount).toBe(1);
    // Without check-in operators passengers go straight to security; the first one opens a desk again.
    view.removeOperator(view.operators('checkIn')[0]);
    expect(view.draft!.inputs.checkIn).toBeNull();
    view.addOperator('checkIn');
    expect(view.draft!.inputs.checkIn).toEqual({
      areaId: 'desks',
      staffCount: 1,
      holdBaggageShare: 0.4,
    });
    view.addOperator('checkIn');
    expect(view.draft!.inputs.checkIn!.staffCount).toBe(2);
    view.removeOperator(view.operators('security')[0]);
    expect(view.draft!.inputs.staffAllocations).toEqual([]);
    view.addOperator('security');
    expect(view.draft!.inputs.staffAllocations).toEqual([
      { staffMemberId: 'staff', checkpointId: 'checkpoint' },
    ]);
  });
  it('creates flights with their own passengers, allocates free gates and keeps edits coherent', () => {
    const fixture = component(),
      view = fixture.componentInstance;
    const gates = ['gate', 'gate-2'].map((id, index) => ({
      id,
      code: `G${index + 1}`,
      name: 'Poarta',
      capacity: 150,
      terminalId: 'terminal',
    }));
    const wide = {
      ...layout,
      layout: {
        ...layout.layout,
        gates,
        routes: gates.map((gate) => ({
          ...layout.layout.routes[0],
          id: `route-${gate.id}`,
          toGateId: gate.id,
        })),
      },
    };
    view.newScenario();
    http.expectOne('/api/scenes/scene/layout-versions').flush(wide);
    shiftStart(view.draft!, '2026-10-16T10:00:00Z');
    view.addFlight();
    const flights = view.draft!.inputs.flights;
    expect(flights.map((flight) => flight.gateId)).toEqual(['gate', 'gate-2']);
    expect(flights.map((flight) => flight.departureAtUtc)).toEqual([
      '2026-10-16T11:00:00.000Z',
      '2026-10-16T11:15:00.000Z',
    ]);
    expect(view.flightsWithoutPassengers()).toEqual([]);
    expect(view.draft!.inputs.populations).toHaveLength(2);
    view.setPassengers(flights[0], 140);
    expect(view.simpleGroup(flights[0])!.population.count).toBe(140);
    expect(flights[0].passengerCapacity).toBe(140);
    view.setGate(flights[0], 'gate-2');
    expect(view.simpleGroup(flights[0])!.flow.routeId).toBe('route-gate-2');
    view.setDeparture(flights[0], '');
    view.setDeparture(flights[0], '2026-10-16T11:15:00.000Z');
    expect(flights[0].boardingClosesAtUtc).toBe('2026-10-16T11:13:00.000Z');
    expect(view.simpleGroup(flights[0])!.flow.endsAtUtc).toBe('2026-10-16T11:13:00.000Z');
    view.removeFlightWithPassengers(flights[0]);
    expect(view.draft!.inputs.flights).toHaveLength(1);
    expect(view.draft!.inputs.populations).toHaveLength(1);
    expect(view.draft!.inputs.flows[0].flightId).toBe(flights[1].id);
  });
  it('does not simplify or remove shared groups, profiles or referenced flights', () => {
    const view = component().componentInstance;
    load(view);
    const flight = view.draft!.inputs.flights[0];
    const before = structuredClone(view.draft!.inputs);
    expect(view.simpleGroup(flight)).toBeNull();
    view.setPassengers(flight, 90);
    view.removeFlightWithPassengers(flight);
    expect(view.draft!.inputs).toEqual(before);
    delete view.draft!.inputs.passengerGeneration;
    delete view.draft!.inputs.navigation;
    view.draft!.inputs.flows[1].populationId = view.draft!.inputs.flows[0].populationId;
    expect(view.simpleGroup(flight)).toBeNull();
  });
  it('schedules flights and linked arrivals by absolute departure times over midnight', () => {
    const view = component().componentInstance;
    principal(view);
    view.setStart('2026-10-31T21:30:00.000Z');
    const flight = view.draft!.inputs.flights[0];
    expect(view.clock(flight.departureAtUtc)).toBe('00:30 · 2026-11-01');
    view.setDeparture(flight, '2026-10-31T22:15:00.000Z');
    expect(flight.departureAtUtc).toBe('2026-10-31T22:15:00.000Z');
    expect(flight.boardingOpensAtUtc).toBe('2026-10-31T22:03:00.000Z');
    // The arrival window keeps ending 30 minutes before the moved departure.
    expect(view.simpleGroup(flight)!.flow.endsAtUtc).toBe('2026-10-31T21:45:00.000Z');
    view.addIncident();
    view.setTime(view.draft!.inputs.events[0], 'occurredAtUtc', '2026-10-31T21:45:00.000Z');
    view.save();
    const request = http.expectOne('/api/scenarios');
    expect(request.request.body.inputs.flights[0].departureAtUtc).toBe('2026-10-31T22:15:00.000Z');
    expect(request.request.body.inputs.events[0].occurredAtUtc).toBe('2026-10-31T21:45:00.000Z');
    request.flush({}, { status: 503, statusText: 'Unavailable' });
  });
  it('asks for an absolute start time before adding flights to an incomplete draft', () => {
    const view = component().componentInstance;
    principal(view);
    view.setStart('');
    expect(() => view.addFlight()).not.toThrow();
    expect(view.draft!.inputs.flights).toHaveLength(5);
    expect(view.message()).toContain('data și ora de început');
  });
  it('keeps the user on an invalid step and opens server errors in the relevant step', () => {
    const view = component().componentInstance;
    principal(view);
    view.draft!.name = '';
    view.goStep(2);
    expect(view.step()).toBe(1);
    expect(view.errors()['name']).toBeDefined();
    view.draft!.name = 'Scenariu';
    view.goStep(2);
    expect(view.step()).toBe(2);
    view.goStep(4);
    http
      .expectOne('/api/scenarios/validate')
      .flush(
        { errors: { 'inputs.flows[0].passengerCount': ['Prea mulți pasageri'] } },
        { status: 400, statusText: 'Invalid' },
      );
    view.showIssue('inputs.flows[0].passengerCount');
    expect(view.step()).toBe(2);
    expect(view.advancedTraffic()).toBe(true);
    expect(view.draft!.name).toBe('Scenariu');
  });
  it('blocks saving and validation when manually edited departures are less than an hour apart', () => {
    const view = component().componentInstance;
    principal(view);
    shiftStart(view.draft!, '2026-10-16T10:00:00Z');
    view.setDeparture(view.draft!.inputs.flights[1], '2026-10-16T11:59:00.000Z');
    view.save();
    view.validate();
    http.expectNone('/api/scenarios');
    http.expectNone('/api/scenarios/validate');
    expect(view.errors()['inputs.flights[0].departureAtUtc'][0]).toContain('minimum 60');
    expect(view.errors()['inputs.flights[1].departureAtUtc'][0]).toContain('minimum 60');
  });
  it('shows live scheduling errors only while invalid and disables every way forward until corrected', () => {
    const fixture = component(),
      view = fixture.componentInstance;
    principal(view);
    shiftStart(view.draft!, '2026-10-16T10:00:00Z');
    view.goStep(2);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    const next = () => root.querySelector<HTMLButtonElement>('.wizard-footer .btn-primary')!;
    const stepButtons = () => root.querySelectorAll<HTMLButtonElement>('.stepper button');
    expect(root.querySelector('[data-testid=flight-rule-error]')).toBeNull();
    expect(root.textContent).not.toContain('Zborurile adăugate sunt distanțate automat');
    expect(next().disabled).toBe(false);
    view.setDeparture(view.draft!.inputs.flights[1], '2026-10-16T11:59:00.000Z');
    fixture.detectChanges();
    expect(root.querySelectorAll('[data-testid=flight-rule-error]')).toHaveLength(1);
    expect(root.querySelector('[data-testid=flight-rule-error]')?.textContent).toContain(
      'minimum 60',
    );
    expect(next().disabled).toBe(true);
    expect(stepButtons()[2].disabled).toBe(true);
    expect(stepButtons()[3].disabled).toBe(true);
    expect(stepButtons()[0].disabled).toBe(false);
    view.goStep(4);
    expect(view.step()).toBe(2);
    http.expectNone('/api/scenarios/validate');
    view.setDeparture(view.draft!.inputs.flights[1], '2026-10-16T12:00:00.000Z');
    fixture.detectChanges();
    expect(root.querySelector('[data-testid=flight-rule-error]')).toBeNull();
    expect(next().disabled).toBe(false);
    expect(stepButtons()[2].disabled).toBe(false);
    view.goStep(3);
    expect(view.step()).toBe(3);
  });
  it('blocks invalid absolute times and prevents skipping flight validation from an earlier step', () => {
    const fixture = component(),
      view = fixture.componentInstance;
    principal(view);
    shiftStart(view.draft!, '2026-10-16T10:00:00Z');
    view.goStep(2);
    view.setDeparture(view.draft!.inputs.flights[0], '');
    fixture.detectChanges();
    expect(
      fixture.nativeElement.querySelector('[data-testid=flight-rule-error]').textContent,
    ).toContain('HH:MM');
    expect(view.navigationBlocked(3)).toBe(true);
    view.goStep(1);
    expect(view.navigationBlocked(2)).toBe(false);
    expect(view.navigationBlocked(4)).toBe(true);
    view.goStep(4);
    expect(view.step()).toBe(2);
    http.expectNone('/api/scenarios/validate');
    view.setDeparture(view.draft!.inputs.flights[0], '2026-10-16T11:00:00.000Z');
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid=flight-rule-error]')).toBeNull();
    expect(view.navigationBlocked(3)).toBe(false);
  });
  it('updates the rule when a delay closes the gap and releases it when that flight is cancelled', async () => {
    const fixture = component(),
      view = fixture.componentInstance;
    principal(view);
    view.goStep(2);
    fixture.detectChanges();
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    const delay = root.querySelector<HTMLInputElement>('.flight-row input[max="10080"]')!;
    delay.value = '1';
    delay.dispatchEvent(new Event('input', { bubbles: true }));
    await fixture.whenStable();
    expect(view.flightRuleProblems().join()).toContain('minimum 60');
    expect(view.navigationBlocked(3)).toBe(true);
    const status = [...root.querySelectorAll<HTMLSelectElement>('.flight-row select')].find(
      (select) => select.querySelector('option[value="cancelled"]'),
    )!;
    status.value = 'cancelled';
    status.dispatchEvent(new Event('change', { bubbles: true }));
    await fixture.whenStable();
    expect(view.flightRuleProblems()).toEqual([]);
    expect(view.navigationBlocked(3)).toBe(false);
  });
  it('preserves advanced settings, targets, payloads and precision while saving and reloading an edit', () => {
    const fixture = component(),
      view = fixture.componentInstance,
      source = load(view),
      input = structuredClone(source.inputs);
    const remember = vi.spyOn(TestBed.inject(SimulationContext), 'remember');
    view.draft!.name = 'Nume editat';
    view.save();
    const update = http.expectOne('/api/scenarios/original');
    expect(update.request.method).toBe('PUT');
    expect(update.request.body.inputs).toEqual(input);
    expect(update.request.body.revision).toBe(4);
    const updated = {
      ...source,
      scenario: { ...source.scenario, name: 'Nume editat', revision: 5 },
    };
    update.flush(updated);
    expect(view.dirty()).toBe(false);
    expect(remember).toHaveBeenCalledWith('original');
    view.reload();
    http.expectOne('/api/scenarios/original').flush(updated);
    http.expectOne('/api/scenes/scene/layout-versions/layout-7').flush(layout);
    expect(view.draft).toEqual(fromScenario(updated));
    expect(source.scenario.name).not.toBe('Nume editat');
  });
  it('adds and edits a typed incident through the designer, saves it and reloads without changing its ID', () => {
    const fixture = component(),
      view = fixture.componentInstance;
    principal(view);
    fixture.detectChanges();
    (Array.from(fixture.nativeElement.querySelectorAll('button')) as HTMLButtonElement[])
      .find((button) => button.textContent?.trim() === 'Adaugă val de pasageri')!
      .click();
    fixture.detectChanges();
    const event = view.draft!.inputs.events[0],
      id = event.id;
    const designer = fixture.debugElement.query(By.directive(IncidentDesigner))
      .componentInstance as IncidentDesigner;
    designer.setPayload('durationMinutes', 3);
    fixture.detectChanges();
    expect(surgePayload(view.draft!.inputs.events[0])?.durationMinutes).toBe(3);
    expect(view.draft!.inputs.events[0].id).toBe(id);
    view.save();
    const request = http.expectOne('/api/scenarios');
    expect(request.request.body.inputs.events[0]).toEqual(view.draft!.inputs.events[0]);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('app-incident-designer fieldset').disabled).toBe(
      true,
    );
    const response: ScenarioDetails = {
      scenario: {
        id: 'incident-scenario',
        code: view.draft!.code,
        name: view.draft!.name,
        description: view.draft!.description,
        sceneId: 'scene',
        layoutVersionId: 'layout-7',
        revision: 1,
        isSynthetic: true,
      },
      inputs: structuredClone(request.request.body.inputs),
    };
    request.flush(response);
    view.reload();
    http.expectOne('/api/scenarios/incident-scenario').flush(response);
    http.expectOne('/api/scenes/scene/layout-versions/layout-7').flush(layout);
    expect(view.draft!.inputs.events[0].id).toBe(id);
    expect(view.dirty()).toBe(false);
  });
  it('keeps an invalid incident draft and presents API errors at its exact payload field', () => {
    const fixture = component(),
      view = fixture.componentInstance;
    principal(view);
    view.addIncident();
    const event = view.draft!.inputs.events[0];
    view.updateIncident({ ...event, payloadJson: event.payloadJson.replace('0.5', '1.5') });
    view.save();
    http.expectOne('/api/scenarios').flush(
      {
        code: 'validation_failed',
        errors: {
          'inputs.events[0].payloadJson.share': ['Suma ponderilor depășește 1.'],
        },
      },
      { status: 400, statusText: 'Bad Request' },
    );
    expect(view.issues()[0].label).toBe('Eveniment 1 → Pondere passenger surge');
    expect(view.draft!.inputs.events[0].id).toBe(event.id);
    expect(view.dirty()).toBe(true);
    expect(view.saved()).toBeNull();
    http.expectNone(
      (request) => request.url.includes('/runs') || request.url.includes('/simulations'),
    );
  });
  it('shows every server error with the exact flight, capacity, time and staff field without losing edits', () => {
    const fixture = component(),
      view = fixture.componentInstance;
    load(view);
    view.draft!.name = 'Păstrează ciorna';
    view.validate();
    http.expectOne('/api/scenarios/validate').flush(
      {
        code: 'validation_failed',
        errors: {
          'inputs.flights[0].passengerCapacity': ['Must be positive.'],
          'inputs.flights[1].gateId': ['Gate is not present in the selected layout version.'],
          'inputs.flights[2].boardingClosesAtUtc': [
            'Boarding must close strictly before departure.',
          ],
          'inputs.staffAllocations[0].staffMemberId': [
            'Staff member is not available in the selected layout version.',
          ],
          'inputs.durationMinutes': ['Must be between 1 and 10080 minutes.'],
          'inputs.seed': ['Must be nonnegative.'],
        },
      },
      { status: 400, statusText: 'Invalid' },
    );
    fixture.detectChanges();
    const text = fixture.nativeElement.textContent;
    expect(text).toContain('Zbor 1 → Capacitate pasageri');
    expect(text).toContain('Zbor 3 → Închidere îmbarcare');
    expect(text).toContain('Alocare personal 1 → Persoană');
    expect(view.issues()).toHaveLength(6);
    expect(view.draft!.name).toBe('Păstrează ciorna');
    expect(view.saved()!.scenario.revision).toBe(4);
    http.expectNone((request) => request.method === 'PUT');
  });
  it('keeps a rejected revision and waits for an explicit discard before reloading', () => {
    const fixture = component(),
      view = fixture.componentInstance;
    const original = load(view);
    view.draft!.name = 'Ciornă în conflict';
    view.save();
    http
      .expectOne('/api/scenarios/original')
      .flush({ code: 'resource_conflict' }, { status: 409, statusText: 'Conflict' });
    expect(view.draft!.name).toBe('Ciornă în conflict');
    expect(view.saved()!.scenario.revision).toBe(4);
    view.reload();
    expect(view.pending()).not.toBeNull();
    http.expectNone((request) => request.method === 'GET');
    view.discardAndContinue();
    http.expectOne('/api/scenarios/original').flush(original);
    http.expectOne('/api/scenes/scene/layout-versions/layout-7').flush(layout);
    expect(view.dirty()).toBe(false);
  });
  it('duplicates the saved source through its API without overwriting the original', () => {
    const fixture = component(),
      view = fixture.componentInstance;
    const original = load(view),
      snapshot = structuredClone(original);
    view.beginDuplicate();
    view.duplicateCode = 'new-copy';
    view.duplicateName = 'Copie demo';
    view.duplicate();
    const request = http.expectOne('/api/scenarios/original/duplicate');
    expect(request.request.body).toEqual({ code: 'new-copy', name: 'Copie demo' });
    request.flush({
      ...original,
      scenario: {
        ...original.scenario,
        id: 'copy',
        code: 'new-copy',
        name: 'Copie demo',
        revision: 1,
      },
    });
    expect(view.saved()!.scenario.id).toBe('copy');
    expect(original).toEqual(snapshot);
    expect(view.draft!.inputs).toEqual(snapshot.inputs);
  });
  it('does not discard a draft when choosing another scenario or preset', () => {
    const fixture = component(),
      view = fixture.componentInstance;
    load(view);
    view.draft!.name = 'Modificare';
    view.newScenario('calm');
    expect(view.pending()).not.toBeNull();
    http.expectNone((request) => request.method === 'POST');
    view.pending.set(null);
    expect(view.draft!.name).toBe('Modificare');
    view.select('other');
    http.expectNone((request) => request.method === 'GET');
    expect(view.draft!.name).toBe('Modificare');
  });
  it('validates numeric fields before serialization and allows one allocation per available person', () => {
    const fixture = component(),
      view = fixture.componentInstance;
    load(view);
    view.allocate('staff', 'checkpoint');
    view.allocate('staff', 'checkpoint');
    expect(view.draft!.inputs.staffAllocations).toHaveLength(1);
    view.draft!.inputs.flights[0].passengerCapacity = NaN;
    view.save();
    http.expectNone((request) => request.method === 'PUT');
    expect(view.issues()[0].label).toBe('Zbor 1 → Capacitate pasageri');
  });
  it('uses validation without persisting and invalidates the check after any edit', () => {
    const fixture = component(),
      view = fixture.componentInstance;
    load(view);
    view.draft!.name = 'Verifică';
    view.validate();
    http.expectOne('/api/scenarios/validate').flush({ isValid: true });
    expect(view.isValidated()).toBe(true);
    expect(view.dirty()).toBe(true);
    view.draft!.inputs.seed++;
    view.edited();
    expect(view.isValidated()).toBe(false);
    http.expectNone((request) => request.method === 'PUT');
  });
  it('preserves the old draft after load errors and does not launch duplicate saves', () => {
    const fixture = component(),
      view = fixture.componentInstance;
    load(view);
    view.draft!.name = 'Păstrează';
    view.save();
    view.save();
    http.expectOne('/api/scenarios/original').flush({}, { status: 503, statusText: 'Unavailable' });
    expect(view.draft!.name).toBe('Păstrează');
    view.reload();
    view.discardAndContinue();
    http.expectOne('/api/scenarios/original').flush({}, { status: 503, statusText: 'Unavailable' });
    expect(view.draft!.name).toBe('Păstrează');
    expect(view.busy()).toBe(false);
  });
  it('shifts all absolute times together and retains relative times and event precision', () => {
    const draft = fromScenario(saved());
    const original = structuredClone(draft.inputs);
    shiftStart(draft, '2026-10-17T11:30:00Z');
    const offset = Date.parse(draft.inputs.startsAtUtc) - Date.parse(original.startsAtUtc);
    expect(
      Date.parse(draft.inputs.flights[0].departureAtUtc) -
        Date.parse(original.flights[0].departureAtUtc),
    ).toBe(offset);
    expect(
      Date.parse(draft.inputs.flows[0].endsAtUtc) - Date.parse(original.flows[0].endsAtUtc),
    ).toBe(offset);
    expect(draft.inputs.events[0].occurredAtUtc).toBe('2026-10-17T11:35:00.123Z');
    expect(draft.inputs.events[0].payloadJson).toBe(original.events[0].payloadJson);
  });
});

describe('Structured event and configuration fields', () => {
  it('replaces nested values and array entries without changing the source object', () => {
    const fixture = TestBed.createComponent(ObjectFields);
    const source = { lanes: 2, metadata: { note: 'original' }, sequence: [1, 2] };
    fixture.componentRef.setInput('value', source);
    const changed = vi.fn();
    fixture.componentInstance.changed.subscribe(changed);
    fixture.componentInstance.replace('metadata', { note: 'edited', active: true });
    expect(changed.mock.calls[0][0]).toEqual({
      ...source,
      metadata: { note: 'edited', active: true },
    });
    expect(source.metadata.note).toBe('original');
    fixture.componentRef.setInput('value', [1, 2]);
    fixture.componentInstance.replace('1', 3);
    expect(changed.mock.lastCall![0]).toEqual([1, 3]);
  });
  it('does not overwrite another key during rename and preserves false, zero and null types', () => {
    const fixture = TestBed.createComponent(ObjectFields);
    fixture.componentRef.setInput('value', { enabled: false, count: 0, optional: null });
    const changed = vi.fn();
    fixture.componentInstance.changed.subscribe(changed);
    fixture.componentInstance.rename('count', 'enabled');
    expect(changed).not.toHaveBeenCalled();
    fixture.componentInstance.rename('optional', 'note');
    expect(changed.mock.lastCall![0]).toEqual({ enabled: false, count: 0, note: null });
  });
});

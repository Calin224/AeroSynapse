import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { PassengerGenerationPanel } from './passenger-generation-panel';
import { PassengerFlowReport, PassengerGenerationConfig, PassengerGenerationResult, PassengerSettings, ScenarioDetails } from './operations.models';

const config: PassengerGenerationConfig = { generatorVersion: '1.0', batches: [{
  flightId: 'flight-1', checkpointId: 'checkpoint-1', countMode: 'full', modeledCount: 120,
  arrival: { kind: 'uniform', minimum: 0, maximum: 55, mean: 27, stdDev: 8, mode: 27 },
  service: { kind: 'exponential', minimum: .05, maximum: 10, mean: 1, stdDev: 1, mode: 1 },
  travel: { kind: 'uniform', minimum: .8, maximum: 1.3, mean: 1, stdDev: .1, mode: 1 },
  groups: [{ kind: 'solo', weight: 1, minSize: 1, maxSize: 1, serviceMultiplier: 1, travelMultiplier: 1 }], surges: [],
}] };
const scenario: ScenarioDetails = {
  scenario: { id: 'scenario-1', code: 'demo', name: 'Demo', description: '', sceneId: 'scene-1', layoutVersionId: 'layout-1', revision: 7 },
  inputs: { startsAtUtc: '2026-10-14T10:00:00Z', durationMinutes: 90, seed: 42, timeZone: 'UTC',
    flights: [{ id: 'flight-1', code: 'DEMO-1', gateId: 'gate-1', departureAtUtc: '2026-10-14T11:00:00Z', passengerCapacity: 120 }],
    populations: [], flows: [], staffAllocations: [], events: [], passengerGeneration: config },
};
const settings: PassengerSettings = {
  scenarioId: 'scenario-1', revision: 7, seed: 42, config, defaults: config,
  checkpoints: [{ id: 'checkpoint-1', name: 'Control DEMO', capacityPerMinute: 4 }],
  flights: [{ id: 'flight-1', code: 'DEMO-1', capacity: 120, isCancelled: false, boardingCloseMinute: 55, checkpointIds: ['checkpoint-1'] }],
};
const preview: PassengerGenerationResult = {
  generatorVersion: '1.0', seed: 42, isSynthetic: true, rng: 'PCG64', numpyVersion: '2.5.4', fingerprint: 'a'.repeat(64),
  configuredCount: 120, modeledCount: 23, hasSubset: true, offset: 0, returnedCount: 1, hasMore: true,
  batches: [{ flightId: 'flight-1', configuredCount: 120, modeledCount: 23, unmodeledCount: 97, countMode: 'subset', groupCount: 23, surgePassengerCount: 15, isSynthetic: true }],
  checkpointDemand: [{ checkpointId: 'checkpoint-1', modeledCount: 23, peakArrivalsPerMinute: 15, meanServiceMinutes: 1,
    staffCount: 1, estimatedCapacityPerMinute: 1, overloaded: true }],
  passengers: [{ id: 'demo-pax-flight-1-000001', flightId: 'flight-1', checkpointId: 'checkpoint-1', routeId: 'route-1',
    groupId: 'demo-group-flight-1-000001', groupKind: 'solo', arrivalMinute: 20, serviceMinutes: 1, travelMinutes: 2, inSurge: true, isSynthetic: true }],
};

const report: PassengerFlowReport = {
  generatorVersion: '1.0', seed: 42, isSynthetic: true, fingerprint: 'b'.repeat(64), durationMinutes: 90, modeledCount: 120,
  stages: [{ stage: 'bagDrop', arrivedPassengers: 40, processedPassengers: 38, queuedPassengers: 2, meanWaitMinutes: 6.5, maxWaitMinutes: 15, blockedMinutes: 10 }],
  segments: [{ segment: 'holdBaggage', passengers: 40, reachedGatePassengers: 38, missedBoardingPassengers: 3, meanJourneyMinutes: 12,
    maxJourneyMinutes: 30, meanQueueMinutes: 7, meanClosureDelayMinutes: 4.25 }],
};

describe('Passenger panel', () => {
  let http: HttpTestingController;
  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());
  function component() {
    const fixture = TestBed.createComponent(PassengerGenerationPanel);
    fixture.componentRef.setInput('scenario', structuredClone(scenario));
    fixture.detectChanges();
    http.expectOne('/api/scenarios/scenario-1/passengers/settings').flush(structuredClone(settings));
    fixture.detectChanges();
    return fixture;
  }

  it('shows each flight with its passenger count and checkpoint, with technical settings collapsed', () => {
    const fixture = component();
    const root: HTMLElement = fixture.nativeElement;
    const row = root.querySelector('.flight-row')!;
    expect(row.textContent).toContain('DEMO-1');
    expect(row.textContent).toContain('din 120');
    expect(row.querySelector('select')).not.toBeNull();
    const seed = root.querySelector('input[name=passengerSeed]')!;
    expect(seed.closest('details')!.open).toBe(false);
    expect(seed.closest('details')!.querySelector('summary')!.textContent).toBe('Setări avansate');
    expect(fixture.componentInstance.config).toEqual(config);
    expect(fixture.componentInstance.dirty()).toBe(false);
  });
  it('keeps the full aircraft as full and anything less as an explicit subset', () => {
    const view = component().componentInstance, batch = view.config!.batches[0];
    view.countChanged(batch, 23);
    expect(batch).toMatchObject({ countMode: 'subset', modeledCount: 23 });
    expect(view.dirty()).toBe(true);
    view.countChanged(batch, 120);
    expect(batch).toMatchObject({ countMode: 'full', modeledCount: 120 });
  });
  it('saves profiles and seed against the current revision and leaves edits available after a rejection', () => {
    const view = component().componentInstance;
    const changed = vi.fn(); view.changed.subscribe(changed);
    view.seed = 43; view.config!.batches[0].countMode = 'subset'; view.config!.batches[0].modeledCount = 23;
    view.modified(); view.save();
    const rejected = http.expectOne('/api/scenarios/scenario-1/passengers/settings');
    expect(rejected.request.method).toBe('PUT');
    expect(rejected.request.body).toMatchObject({ revision: 7, seed: 43, config: { batches: [{ countMode: 'subset', modeledCount: 23 }] } });
    rejected.flush({}, { status: 409, statusText: 'Conflict' });
    expect(view.dirty()).toBe(true); expect(view.busy()).toBe(false); expect(view.config!.batches[0].modeledCount).toBe(23);
    view.save(); http.expectOne('/api/scenarios/scenario-1/passengers/settings').flush(scenario);
    expect(changed).toHaveBeenCalledWith(scenario); expect(view.dirty()).toBe(false);
  });

  it('offers all open checkpoints first and saves either a spread or a pinned control', () => {
    const fixture = component(), view = fixture.componentInstance;
    const select: HTMLSelectElement = fixture.nativeElement.querySelector('.flight-row select');
    expect(select.closest('label')!.textContent).toContain('Control de securitate');
    expect([...select.options].map(option => option.textContent!.trim())).toEqual(['Toate controalele spre poartă', 'Doar Control DEMO']);
    view.laneChanged(view.config!.batches[0], 'spread'); view.save();
    const spread = http.expectOne('/api/scenarios/scenario-1/passengers/settings');
    expect(spread.request.body.config.batches[0]).toMatchObject({ checkpointMode: 'spread', checkpointId: 'checkpoint-1' });
    spread.flush(scenario);
    view.laneChanged(view.config!.batches[0], 'checkpoint-1'); view.save();
    const pinned = http.expectOne('/api/scenarios/scenario-1/passengers/settings');
    expect(pinned.request.body.config.batches[0]).toMatchObject({ checkpointMode: 'fixed', checkpointId: 'checkpoint-1' });
    pinned.flush(scenario);
  });

  it('uses persisted profiles for pagination and shows explicit subset totals and overload estimates', () => {
    const fixture = component(); const view = fixture.componentInstance;
    view.generate(); http.expectOne('/api/scenarios/scenario-1/passengers?revision=7&offset=0&limit=100').flush(preview);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Pasageri nesimulați: 97');
    expect(fixture.nativeElement.textContent).toContain('Supraaglomerat');
    expect(fixture.nativeElement.textContent).toContain(preview.fingerprint);
    view.generate(100); http.expectOne('/api/scenarios/scenario-1/passengers?revision=7&offset=100&limit=100').flush({ ...preview, offset: 100, hasMore: false });
    view.modified(); view.generate();
    http.expectNone('/api/scenarios/scenario-1/passengers?revision=7&offset=0&limit=100');
    expect(view.preview()).toBeNull();
  });

  it('previews incident-derived passengers without activating explicit profiles or changing the scenario', () => {
    const fixture = TestBed.createComponent(PassengerGenerationPanel);
    const source = structuredClone(scenario); source.inputs.passengerGeneration = null;
    source.inputs.events = [{ id: 'incident', type: 'incident.passenger-surge', occurredAtUtc: source.inputs.startsAtUtc,
      payloadJson: '{"contractVersion":"1.0","durationMinutes":5,"share":0.5,"isSynthetic":true}' }];
    fixture.componentRef.setInput('scenario', source); fixture.detectChanges();
    http.expectOne('/api/scenarios/scenario-1/passengers/settings').flush({ ...settings, config: null });
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('din incidentele scenariului');
    const view = fixture.componentInstance; view.generate();
    http.expectOne('/api/scenarios/scenario-1/passengers?revision=7&offset=0&limit=100').flush(preview);
    expect(view.preview()?.batches[0].surgePassengerCount).toBe(15);
    http.expectNone(request => request.method === 'PUT' || request.method === 'POST');
  });

  it('prevents invalid seeds and disables operations while another scenario operation is running', () => {
    const fixture = component(); const view = fixture.componentInstance;
    view.seed = 4.5; view.save(); expect(view.message()).toContain('seed) trebuie să fie un întreg');
    fixture.componentRef.setInput('disabled', true); fixture.detectChanges();
    view.seed = 42; view.save(); view.generate();
    http.expectNone(request => request.url.includes('/passengers') && !request.url.endsWith('/settings'));
    http.expectNone(request => request.method === 'PUT');
  });

  it('cancels obsolete settings requests when switching scenarios', () => {
    const fixture = component();
    fixture.componentRef.setInput('scenario', { ...scenario, scenario: { ...scenario.scenario, id: 'scenario-2' } });
    fixture.detectChanges();
    const obsolete = http.expectOne('/api/scenarios/scenario-2/passengers/settings');
    fixture.componentRef.setInput('scenario', structuredClone(scenario)); fixture.detectChanges();
    expect(obsolete.cancelled).toBe(true);
    http.expectOne('/api/scenarios/scenario-1/passengers/settings').flush(structuredClone(settings));
  });

  it('saves explicit alternative flows and removes them when disabled', () => {
    const view = component().componentInstance;
    view.flowChanged(view.config!, true);
    view.addClosure(view.config!.flow!);
    view.config!.flow!.closures[0] = { stage: 'bagDrop', startsMinute: 15, endsMinute: 30 };
    expect(view.dirty()).toBe(true);
    view.save();
    const saved = http.expectOne('/api/scenarios/scenario-1/passengers/settings');
    expect(saved.request.body.config.flow.segments.map((segment: { kind: string }) => segment.kind)).toEqual(['standard', 'holdBaggage', 'reducedMobility', 'transfer']);
    expect(saved.request.body.config.flow.segments.reduce((total: number, segment: { share: number }) => total + segment.share, 0)).toBeCloseTo(1, 9);
    expect(saved.request.body.config.flow.closures).toEqual([{ stage: 'bagDrop', startsMinute: 15, endsMinute: 30 }]);
    saved.flush(scenario);
    view.flowChanged(view.config!, false); view.save();
    const removed = http.expectOne('/api/scenarios/scenario-1/passengers/settings');
    expect(removed.request.body.config.flow).toBeNull();
    removed.flush(scenario);
  });

  it('analyzes saved flows and shows only aggregate statistics per stage and type', () => {
    const fixture = component(); const view = fixture.componentInstance;
    view.analyzeFlows(); http.expectOne('/api/scenarios/scenario-1/passengers/flows?revision=7').flush(report);
    fixture.detectChanges();
    const text = fixture.nativeElement.textContent as string;
    expect(text).toContain('Predare bagaj de cală'); expect(text).toContain('Bagaj de cală'); expect(text).toContain('4.25');
    expect(text).not.toContain('demo-pax');
    view.modified(); expect(view.flowReport()).toBeNull();
    view.analyzeFlows(); http.expectNone('/api/scenarios/scenario-1/passengers/flows?revision=7');
  });
});

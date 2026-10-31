import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { NavigationPanel } from './navigation-panel';
import { NavigationConfig, NavigationResult, NavigationSettings } from './navigation.models';
import { RunDetails, ScenarioDetails } from './operations.models';

const config: NavigationConfig = { navigationVersion: '1.0',
  graph: { nodes: [
    { id: 'cp-1', name: 'Control', kind: 'Security', x: 0, y: 0, z: 0, isActive: true, isStepFree: true, requiredTags: [] },
    { id: 'hall-1', name: 'Coridor principal', kind: 'Walkway', x: 50, y: 0, z: 0, isActive: true, isStepFree: true, requiredTags: [] },
    { id: 'gate-1', name: 'Poarta', kind: 'Gate', x: 100, y: 0, z: 0, isActive: true, isStepFree: true, requiredTags: [] }],
    edges: [{ id: 'edge-1', fromZoneId: 'cp-1', toZoneId: 'hall-1', distanceMeters: 50, travelSeconds: 60, isBidirectional: false, isActive: true, isStepFree: true, requiredTags: [] },
      { id: 'edge-2', fromZoneId: 'hall-1', toZoneId: 'gate-1', distanceMeters: 50, travelSeconds: 60, isBidirectional: false, isActive: true, isStepFree: true, requiredTags: [] }] },
  checkpointBindings: [{ resourceId: 'cp-1', zoneId: 'cp-1' }], gateBindings: [{ resourceId: 'gate-1', zoneId: 'gate-1' }],
  accessProfiles: [{ groupKind: 'solo', requireStepFree: false, accessTags: [], forbiddenZoneIds: [], walkingMultiplier: 1 }], closedZoneIds: [], closedEdgeIds: [],
};
const scenario: ScenarioDetails = { scenario: { id: 'scenario-1', code: 'demo', name: 'Demo', description: '', sceneId: 'scene-1', layoutVersionId: 'layout-1', revision: 7 },
  inputs: { startsAtUtc: '2026-10-14T10:00:00Z', durationMinutes: 60, seed: 42, timeZone: 'UTC',
    flights: [{ id: 'flight-1', code: 'DEMO-1', gateId: 'gate-1', departureAtUtc: '2026-10-14T11:00:00Z', passengerCapacity: 2 }],
    populations: [], flows: [], staffAllocations: [], events: [], navigation: config } };
const settings: NavigationSettings = { scenarioId: 'scenario-1', revision: 7, passengerProfilesActive: true, config, defaults: config,
  checkpoints: [{ id: 'cp-1', name: 'Control' }], gates: [{ id: 'gate-1', name: 'Poarta' }] };
const result: NavigationResult = { navigationVersion: '1.0', algorithm: 'dijkstra', graphFingerprint: 'a'.repeat(64), populationFingerprint: 'b'.repeat(64),
  isSynthetic: true, modeledCount: 2, routedCount: 2, errorCount: 0, assessed: false, offset: 0, returnedCount: 2, hasMore: false,
  journeys: [1,2].map(index => ({ id: `demo-pax-${index}`, flightId: 'flight-1', gateId: 'gate-1', checkpointId: 'cp-1', routeKey: 'nav-main', error: null,
    walkingMinutes: 2, queueWaitMinutes: null, serviceMinutes: 1, arrivalMinute: 0, gateArrivalMinute: null, boardingCloseMinute: 3, boardingRisk: 'pending' })),
  routes: [{ routeKey: 'nav-main', flightId: 'flight-1', gateId: 'gate-1', checkpointId: 'cp-1', groupKind: 'solo', zoneIds: ['cp-1','hall-1','gate-1'], edgeIds: ['edge-1','edge-2'],
    distanceMeters: 100, baseWalkingMinutes: 2, passengerCount: 2, waypoints: config.graph.nodes.map(node => ({ zoneId: node.id, x: node.x, y: node.y, z: node.z })) }],
  flights: [{ flightId: 'flight-1', gateId: 'gate-1', modeledCount: 2, routedCount: 2, errorCount: 0, safeCount: 0, lateCount: 0, unservedCount: 0, pendingCount: 2, meanWalkingMinutes: 2, meanQueueWaitMinutes: null }] };

describe('Scenario navigation controls', () => {
  let http: HttpTestingController;
  beforeEach(() => { TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] }); http = TestBed.inject(HttpTestingController); });
  afterEach(() => http.verify());
  function component(value = settings) {
    const fixture = TestBed.createComponent(NavigationPanel); fixture.componentRef.setInput('scenario', structuredClone(scenario)); fixture.detectChanges();
    http.expectOne('/api/scenarios/scenario-1/navigation/settings').flush(structuredClone(value)); fixture.detectChanges(); return fixture;
  }
  it('persists corridor closures with revision control and keeps edits after a rejected save', () => {
    const view = component().componentInstance; const changed = vi.fn(); view.changed.subscribe(changed);
    view.closeZone('hall-1', true); http.expectOne('/api/scenarios/scenario-1/navigation/preview').flush(result); view.save();
    const request = http.expectOne('/api/scenarios/scenario-1/navigation/settings');
    expect(request.request.method).toBe('PUT'); expect(request.request.body).toMatchObject({ revision: 7, config: { closedZoneIds: ['hall-1'] } });
    request.flush({}, { status: 409, statusText: 'Conflict' }); expect(view.dirty()).toBe(true); expect(view.busy()).toBe(false);
    view.save(); http.expectOne('/api/scenarios/scenario-1/navigation/settings').flush(scenario);
    expect(changed).toHaveBeenCalledWith(scenario); expect(view.dirty()).toBe(false);
  });
  it('requires passenger profiles and rejects malformed JSON before sending a request', () => {
    const fixture = component({ ...settings, passengerProfilesActive: false }); fixture.componentInstance.save();
    expect(fixture.nativeElement.textContent).toContain('Profiluri de pasageri neconfigurate.');
    http.expectNone(request => request.method === 'PUT'); fixture.destroy();
    const view = component().componentInstance; view.graphJson = '{'; view.save();
    expect(view.message()).toContain('obiect JSON'); http.expectNone(request => request.method === 'PUT');
    const saved = view.config; view.graphJson = JSON.stringify({ ...config, graph: { nodes: [null], edges: [] } }); view.applyJson();
    expect(view.config).toBe(saved); expect(view.message()).toContain('obiect JSON');
  });
  it('explains route choices using outgoing links and does not treat one-way links as bidirectional', () => {
    const fixture = component();
    const view = fixture.componentInstance;
    expect(view.connections('cp-1')).toContain('Coridor principal');
    expect(view.connections('hall-1')).toContain('Poarta');
    expect(view.connections('hall-1')).not.toContain('Control');
    expect(view.connections('gate-1')).toContain('Nu există legături de plecare');

    view.config!.closedEdgeIds = ['edge-1'];
    expect(view.connections('cp-1')).toContain('legătură închisă');
  });
  it('does not equate imported graph resources with scene resources and sends explicit bindings', () => {
    const view = component().componentInstance; view.airportMapId = 'airport-1'; view.importMap();
    http.expectOne('/api/navigation/airports/airport-1').flush({ ...config, checkpointBindings: [{ resourceId: 'external-cp', zoneId: 'cp-1' }], gateBindings: [{ resourceId: 'external-gate', zoneId: 'gate-1' }] });
    expect(view.config!.checkpointBindings).toEqual([]); expect(view.config!.gateBindings).toEqual([]);
    view.bind('checkpointBindings','cp-1','cp-1'); view.bind('gateBindings','gate-1','gate-1'); view.save();
    const request = http.expectOne('/api/scenarios/scenario-1/navigation/settings');
    expect(request.request.body.config.checkpointBindings).toEqual(config.checkpointBindings); request.flush(scenario);
  });
  it('shows planned routes separately from assessed boarding results and uses captured run endpoints', () => {
    const fixture = component(); const view = fixture.componentInstance; view.preview();
    http.expectOne('/api/scenarios/scenario-1/navigation/preview').flush(result); fixture.detectChanges();
     expect(view.renderScene().navigationRoutes).toEqual(result.routes);
    const run: RunDetails = { id: 'run-1', status: 'completed', revision: 3, seed: 42, scenarioRevision: 7, retryOfRunId: null, resultsValid: true, failureReason: null, error: null, metrics: [], snapshots: [] };
    fixture.componentRef.setInput('run', run); fixture.detectChanges(); view.inspectRun();
    http.expectOne('/api/runs/run-1/navigation?offset=0&limit=100').flush({ ...result, assessed: true,
      journeys: result.journeys.map((person,index) => ({ ...person,queueWaitMinutes:index,gateArrivalMinute:3+index,boardingRisk:index ? 'late' : 'safe' })),
      flights: [{ ...result.flights[0], pendingCount: 0, safeCount: 1, lateCount: 1 }] });
    fixture.detectChanges(); expect(view.showingRun()).toBe('run-1'); expect(fixture.nativeElement.textContent).toContain('Rezultat capturat al rulării');
    expect(view.result()!.flights[0].lateCount).toBe(1);
    fixture.componentRef.setInput('run', { ...run, id: 'run-2' }); fixture.detectChanges(); view.next(1);
    http.expectOne('/api/runs/run-1/navigation?offset=1&limit=100').flush({ ...result, assessed: true, offset: 1, returnedCount: 1, journeys: result.journeys.slice(1) });
    view.closeZone('hall-1', true); http.expectOne('/api/scenarios/scenario-1/navigation/preview').flush(result);
  });
  it('calculates the impact of an unsaved closure without changing the scenario', () => {
    const fixture = component(); const view = fixture.componentInstance;
    view.closeZone('hall-1', true);
    const request = http.expectOne('/api/scenarios/scenario-1/navigation/preview');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toMatchObject({ revision: 7, config: { closedZoneIds: ['hall-1'] } });
    request.flush({ ...result, routedCount: 0, errorCount: 2, routes: [] }); fixture.detectChanges();
    expect(view.dirty()).toBe(true);
    expect(view.result()!.errorCount).toBe(2);
    expect(fixture.nativeElement.textContent).toContain('Impactul închiderilor');
    http.expectNone(request => request.method === 'PUT');
  });
  it('cancels obsolete settings reads when the selected scenario changes', () => {
    const fixture = component();
    fixture.componentRef.setInput('scenario', { ...scenario, scenario: { ...scenario.scenario, id: 'scenario-2' } }); fixture.detectChanges();
    const old = http.expectOne('/api/scenarios/scenario-2/navigation/settings');
    fixture.componentRef.setInput('scenario', structuredClone(scenario)); fixture.detectChanges(); expect(old.cancelled).toBe(true);
    http.expectOne('/api/scenarios/scenario-1/navigation/settings').flush(settings);
  });
});

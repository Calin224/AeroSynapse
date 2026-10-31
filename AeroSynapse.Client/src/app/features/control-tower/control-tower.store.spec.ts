import { signal } from '@angular/core';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Subject } from 'rxjs';
import { apiErrorInterceptor } from '../../core/http/api-error.interceptor';
import { OperationsState, OperationsStream } from '../../core/http/operations-stream';
import { ControlTowerStore } from './control-tower.store';
import { ids, towerFixture } from './tower.testing';
import { bottleneckLayout } from '../operational-report/report.testing';

describe('Control Tower selection, live recovery and simulation commands', () => {
  let http: HttpTestingController, store: ControlTowerStore;
  let live: {
    state: ReturnType<typeof signal<OperationsState | null>>;
    updates: Subject<OperationsState>;
    connect: ReturnType<typeof vi.fn>;
  };
  beforeEach(() => {
    const values = new Map<string, string>();
    vi.stubGlobal('localStorage', {
      getItem: (key: string) => values.get(key) ?? null,
      setItem: (key: string, value: string) => values.set(key, value),
      removeItem: (key: string) => values.delete(key),
    });
    vi.useFakeTimers();
    live = {
      state: signal<OperationsState | null>(null),
      updates: new Subject(),
      connect: vi.fn(),
    };
    TestBed.configureTestingModule({
      providers: [
        ControlTowerStore,
        provideHttpClient(withInterceptors([apiErrorInterceptor])),
        provideHttpClientTesting(),
        { provide: OperationsStream, useValue: live },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    store = TestBed.inject(ControlTowerStore);
  });
  afterEach(() => {
    http.verify();
    TestBed.resetTestingModule();
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });
  function load(run = towerFixture().run, currentRevision = 1) {
    const { scenario, layout } = towerFixture();
    scenario.scenario.revision = currentRevision;
    store.open(ids.scenario, run.id);
    http.expectOne('/api/scenarios').flush([scenario.scenario]);
    http.expectOne(`/api/scenarios/${ids.scenario}`).flush(scenario);
    http.expectOne(`/api/scenes/${ids.scene}/layout-versions/${ids.layout}`).flush(layout);
    http.expectOne(`/api/scenarios/${ids.scenario}/runs?limit=100`).flush([run]);
    vi.advanceTimersByTime(0);
    http.expectOne(`/api/scenarios/${ids.scenario}/runs/${run.id}`).flush(run);
  }
  it('uses captured resources for an old run while keeping the latest scenario revision for new launches', () => {
    load(towerFixture().run, 4);
    expect(store.selected()?.scenario.revision).toBe(4);
    expect(store.context()?.scenario.scenario.revision).toBe(1);
    expect(store.metrics().processed).toBe(12);
    expect(store.clock()).toBe('13:06:00');
    store.open(ids.scenario, ids.run);
    http.expectNone('/api/scenarios');
  });
  it('cancels the old run request and immediately clears its cards when scenario selection changes', () => {
    load();
    store.reload();
    const { scenario, layout } = towerFixture();
    http.expectOne('/api/scenarios').flush([scenario.scenario]);
    http.expectOne(`/api/scenarios/${ids.scenario}`).flush(scenario);
    http.expectOne(`/api/scenes/${ids.scene}/layout-versions/${ids.layout}`).flush(layout);
    http.expectOne(`/api/scenarios/${ids.scenario}/runs?limit=100`).flush([towerFixture().run]);
    vi.advanceTimersByTime(0);
    const pending = http.expectOne(`/api/scenarios/${ids.scenario}/runs/${ids.run}`);
    store.open(ids.otherRun, null);
    expect(pending.cancelled).toBe(true);
    expect(store.run()).toBeNull();
    expect(store.metrics().processed).toBeNull();
    http.expectOne('/api/scenarios').flush([]);
    expect(store.message()).toContain('catalogul activ');
  });
  it('rejects a response from another scenario without exposing its metrics', () => {
    load({ ...towerFixture().run, scenarioId: ids.otherRun });
    expect(store.run()).toBeNull();
    expect(store.runError()).toContain('nu corespunde');
    expect(store.metrics().processed).toBeNull();
  });
  it('shows an unrun scenario without fake KPI values', () => {
    const { scenario, layout } = towerFixture();
    store.open(ids.scenario, null);
    http.expectOne('/api/scenarios').flush([scenario.scenario]);
    http.expectOne(`/api/scenarios/${ids.scenario}`).flush(scenario);
    http.expectOne(`/api/scenes/${ids.scene}/layout-versions/${ids.layout}`).flush(layout);
    http.expectOne(`/api/scenarios/${ids.scenario}/runs?limit=100`).flush([]);
    expect(store.context()?.scenario.scenario.id).toBe(ids.scenario);
    expect(store.metrics().queued).toBeNull();
    expect(store.clock()).toBeNull();
    expect(store.canLaunch()).toBe(true);
  });
  it('updates only the selected live run and replaces provisional observations with validated results', () => {
    load(towerFixture({ status: 'running', resultsValid: false, snapshots: [], metrics: [] }).run);
    const progress = {
      runId: ids.run,
      status: 'running',
      simulationMinute: 3,
      revision: 2,
      resultsValid: false,
      snapshot: {
        totalZones: 2,
        zones: [ids.cp, ids.otherCp].map((zoneId) => ({
          zoneId,
          queuedPassengers: 5,
          processedPassengers: 2,
        })),
      },
    };
    const state: OperationsState = {
      scenarioId: ids.scenario,
      sequence: 1,
      resetRequired: false,
      runs: [progress],
      run: progress,
      optimization: null,
    };
    live.state.set({ ...state, scenarioId: ids.otherRun });
    live.updates.next(live.state()!);
    expect(store.metrics().queued).toBeNull();
    live.state.set(state);
    live.updates.next(state);
    expect(store.metrics()).toMatchObject({ queued: 10, processed: 4, meanWait: null });
    expect(store.minute()).toBe(3);
    vi.advanceTimersByTime(2000);
    http.expectOne(`/api/scenarios/${ids.scenario}/runs/${ids.run}`).flush(towerFixture().run);
    expect(store.metrics().processed).toBe(12);
    expect(store.minute()).toBe(6);
    expect(store.status()).toBe('completed');
    expect(store.heatmap()?.source).toBe('stored-run');
    expect(store.metrics().points.map((row) => row.minute)).toEqual([0, 2, 4, 6]);
    live.updates.next(state);
    expect(store.metrics().processed).toBe(12);
  });
  it('keeps the virtual clock monotonic when REST recovery temporarily has no live snapshot', () => {
    const run = towerFixture({
      status: 'running',
      resultsValid: false,
      snapshots: [],
      metrics: [],
    }).run;
    load(run);
    const progress = {
      runId: ids.run,
      status: 'running',
      simulationMinute: 3,
      revision: 2,
      resultsValid: false,
      snapshot: null,
    };
    const state: OperationsState = {
      scenarioId: ids.scenario,
      sequence: 1,
      resetRequired: false,
      runs: [progress],
      run: progress,
      optimization: null,
    };
    live.state.set(state);
    live.updates.next(state);
    expect(store.minute()).toBe(3);
    const recovered = { ...state, run: null, runs: [] };
    live.state.set(recovered);
    live.updates.next(recovered);
    vi.advanceTimersByTime(2000);
    http.expectOne(`/api/scenarios/${ids.scenario}/runs/${ids.run}`).flush(run);
    expect(store.minute()).toBe(3);
    expect(store.clock()).toBe('13:03:00');
  });
  it('retries a launch with the same idempotency key after an ambiguous network response', () => {
    load();
    store.launch();
    const first = http.expectOne(`/api/scenarios/${ids.scenario}/simulations`);
    const key = first.request.headers.get('Idempotency-Key');
    expect(first.request.body).toEqual({ scenarioRevision: 1 });
    first.flush({}, { status: 503, statusText: 'Unavailable' });
    expect(store.retryCommand()).toBe(true);
    expect(store.canLaunch()).toBe(false);
    store.launch();
    http.expectNone(`/api/scenarios/${ids.scenario}/simulations`);
    store.resumeLaunch();
    const retry = http.expectOne(`/api/scenarios/${ids.scenario}/simulations`);
    expect(retry.request.headers.get('Idempotency-Key')).toBe(key);
    retry.flush({
      commandId: key,
      scenarioId: ids.scenario,
      runId: ids.otherRun,
      status: 'pending',
      resultsValid: false,
    });
    expect(store.retryCommand()).toBe(false);
  });
  it('can reconcile a pending command even when its scenario has left the active catalog', () => {
    load();
    store.launch();
    const first = http.expectOne(`/api/scenarios/${ids.scenario}/simulations`);
    const key = first.request.headers.get('Idempotency-Key');
    first.flush({}, { status: 503, statusText: 'Unavailable' });
    store.open(ids.otherRun, null);
    http.expectOne('/api/scenarios').flush([]);
    expect(store.selected()).toBeNull();
    expect(store.pendingScenarioId()).toBe(ids.scenario);
    store.resumeLaunch();
    const retry = http.expectOne(`/api/scenarios/${ids.scenario}/simulations`);
    expect(retry.request.headers.get('Idempotency-Key')).toBe(key);
    retry.flush({}, { status: 404, statusText: 'Not Found' });
    expect(store.pendingScenarioId()).toBeNull();
  });
  it('preserves the last confirmed observation on a temporary REST failure and recovers', () => {
    const run = towerFixture({ status: 'running', resultsValid: false }).run;
    load(run);
    vi.advanceTimersByTime(2000);
    http
      .expectOne(`/api/scenarios/${ids.scenario}/runs/${ids.run}`)
      .flush({}, { status: 503, statusText: 'Unavailable' });
    expect(store.run()?.id).toBe(ids.run);
    expect(store.runError()).toBeTruthy();
    vi.advanceTimersByTime(2000);
    http.expectOne(`/api/scenarios/${ids.scenario}/runs/${ids.run}`).flush(towerFixture().run);
    expect(store.runError()).toBe('');
    expect(store.metrics().processed).toBe(12);
  });
  it('creates a persisted demo from the API layout when the scenario catalog is empty', () => {
    store.open(null, null);
    http.expectOne('/api/scenarios').flush([]);
    const accepted = vi.fn();
    store.accepted.subscribe(accepted);
    store.demo();
    store.demo();
    http.expectOne('/api/scenes').flush([bottleneckLayout().layout.scene]);
    http.expectOne(`/api/scenes/${ids.scene}/layout-versions`).flush(bottleneckLayout());
    const create = http.expectOne('/api/scenarios');
    expect(create.request.body.sceneId).toBe(ids.scene);
    expect(create.request.body.layoutVersionId).toBe(ids.layout);
    expect(create.request.body.inputs.staffAllocations[0].checkpointId).toBe(ids.cp);
    create.flush(towerFixture().scenario);
    expect(accepted).toHaveBeenCalledWith({ scenarioId: ids.scenario, runId: null });
    expect(store.busy()).toBe(false);
  });
  it('loads an existing synthetic demo without creating a duplicate scenario', () => {
    load();
    const accepted = vi.fn();
    store.accepted.subscribe(accepted);
    store.demo();
    expect(accepted).toHaveBeenCalledWith({ scenarioId: ids.scenario, runId: null });
    http.expectNone('/api/scenes');
    http.expectNone((request) => request.method === 'POST');
  });
  it('restores the same command after refresh and removes it only after confirmation', () => {
    const key = 'aerosynapse.control-tower.pending-command.v1';
    const saved = { scenarioId: ids.scenario, revision: 1, key: ids.event };
    window.localStorage.setItem(key, JSON.stringify(saved));
    // A new injector represents a refreshed page; only the persisted key crosses the boundary.
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        ControlTowerStore,
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: OperationsStream, useValue: live },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    store = TestBed.inject(ControlTowerStore);
    load();
    expect(store.retryCommand()).toBe(true);
    expect(store.canLaunch()).toBe(false);
    store.resumeLaunch();
    const request = http.expectOne(`/api/scenarios/${ids.scenario}/simulations`);
    expect(request.request.headers.get('Idempotency-Key')).toBe(saved.key);
    request.flush({
      commandId: saved.key,
      scenarioId: ids.scenario,
      runId: ids.run,
      status: 'pending',
      resultsValid: false,
    });
    expect(window.localStorage.getItem(key)).toBeNull();
  });
});

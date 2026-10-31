import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Operations } from './operations';
import { ScenarioDetails } from './operations.models';

const scenario: ScenarioDetails = {
  scenario: {
    id: 'scenario-1',
    code: 'baseline',
    name: 'Baseline',
    description: '',
    sceneId: 'scene-1',
    layoutVersionId: 'layout-1',
    revision: 1,
  },
  inputs: {
    startsAtUtc: '2026-10-13T06:00:00Z',
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

describe('Persisted scenario and run controls', () => {
  let http: HttpTestingController;
  beforeEach(() => {
    vi.useFakeTimers();
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => {
    http.verify();
    vi.useRealTimers();
  });
  function component() {
    const fixture = TestBed.createComponent(Operations);
    http.expectOne('/api/scenes').flush([{ id: 'scene-1', name: 'Aurora', code: 'AUR' }]);
    http.expectOne('/api/scenarios').flush([scenario.scenario]);
    fixture.detectChanges();
    return fixture;
  }
  it('rejects an out-of-range passenger count before publishing or saving anything', () => {
    const fixture = component();
    const view = fixture.componentInstance;
    view.airportId = 'scene-1';
    view.code = 'baseline';
    view.name = 'Baseline';
    view.passengers = 2147483648;
    view.create();
    http.expectNone((request) => request.method === 'POST');
    expect(view.message()).toContain('pasageri: 1–2147483647');
    expect(view.busy()).toBe(false);
    fixture.destroy();
  });
  it('publishes a layout and saves complete scenario inputs before offering a run', () => {
    const fixture = component();
    const view = fixture.componentInstance;
    view.airportId = 'scene-1';
    view.code = 'baseline';
    view.name = 'Baseline';
    view.create();
    http.expectOne('/api/scenes/scene-1/layout-versions').flush({
      version: { id: 'layout-1' },
      layout: {
        gates: [{ id: 'gate-1' }],
        routes: [{ id: 'route-1', toGateId: 'gate-1' }],
        staff: [{ id: 'staff-1', checkpointId: 'checkpoint-1' }],
        checkpoints: [{ id: 'checkpoint-1' }],
      },
    });
    const saved = http.expectOne('/api/scenarios');
    expect(saved.request.method).toBe('POST');
    expect(saved.request.body.layoutVersionId).toBe('layout-1');
    expect(saved.request.body.inputs.seed).toBe(42);
    expect(saved.request.body.inputs.staffAllocations).toEqual([
      { staffMemberId: 'staff-1', checkpointId: 'checkpoint-1' },
    ]);
    expect(saved.request.body.inputs.flights).toHaveLength(1);
    saved.flush(scenario);
    http.expectOne('/api/scenarios').flush([scenario.scenario]);
    expect(view.selected()?.scenario.id).toBe('scenario-1');
    expect(view.busy()).toBe(false);
  });
  it('polls an accepted run until completion and stops polling after a terminal status', async () => {
    const fixture = component();
    const view = fixture.componentInstance;
    view.select('scenario-1');
    http.expectOne('/api/scenarios/scenario-1').flush(scenario);
    http.expectOne('/api/scenarios/scenario-1/runs').flush([]);
    view.run();
    http
      .expectOne('/api/scenarios/scenario-1/runs')
      .flush({ id: 'run-1', status: 'planned', revision: 1 });
    await vi.advanceTimersByTimeAsync(0);
    http
      .expectOne('/api/runs/run-1')
      .flush({ id: 'run-1', status: 'running', metrics: [], snapshots: [], resultsValid: false });
    await vi.advanceTimersByTimeAsync(1000);
    http
      .expectOne('/api/runs/run-1')
      .flush({ id: 'run-1', status: 'completed', metrics: [], snapshots: [], resultsValid: true });
    await vi.advanceTimersByTimeAsync(5000);
    http.expectNone('/api/runs/run-1');
    expect(view.result()?.status).toBe('completed');
    fixture.destroy();
  });
  it('retries with the failed run revision and cancels pending requests on destruction', async () => {
    const fixture = component();
    const view = fixture.componentInstance;
    view.result.set({
      id: 'failed',
      status: 'failed',
      revision: 3,
      seed: 42,
      scenarioRevision: 1,
      retryOfRunId: null,
      resultsValid: false,
      failureReason: 'offline',
      metrics: [],
      snapshots: [],
      error: { code: 'engine_unavailable', message: 'offline' },
    });
    view.retry();
    const retry = http.expectOne('/api/runs/failed/retry');
    expect(retry.request.body).toEqual({ revision: 3 });
    retry.flush({ id: 'retry-1', status: 'planned', revision: 1 });
    await vi.advanceTimersByTimeAsync(0);
    const pending = http.expectOne('/api/runs/retry-1');
    fixture.destroy();
    expect(pending.cancelled).toBe(true);
  });
});

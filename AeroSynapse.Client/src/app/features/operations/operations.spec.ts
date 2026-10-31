import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { ActivatedRoute, Router, provideRouter } from '@angular/router';
import { Subject } from 'rxjs';
import { SessionState } from '../../core/auth/session-state';
import { convertToParamMap } from '@angular/router';
import { OperationsStream } from '../../core/http/operations-stream';
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
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });
    TestBed.overrideComponent(Operations, {
      set: {
        providers: [
          {
            provide: OperationsStream,
            useValue: {
              connect: vi.fn(),
              trackRun: vi.fn(),
              recover: vi.fn(),
              updates: new Subject(),
              state: signal(null),
              status: signal('polling'),
              error: signal(''),
            },
          },
        ],
      },
    });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => {
    http.verify();
    vi.useRealTimers();
  });
  function component() {
    const fixture = TestBed.createComponent(Operations);
    http.expectOne('/api/scenarios').flush([scenario.scenario]);
    fixture.detectChanges();
    return fixture;
  }
  it('groups the sections into a preparation step and a results step', () => {
    const fixture = component();
    const view = fixture.componentInstance;
    view.selected.set(scenario);
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    const navigation = () =>
      element.querySelector('[aria-label="Secțiuni operaționale"]') as HTMLElement;

    expect(navigation().getAttribute('aria-orientation')).toBe('horizontal');
    expect(navigation().querySelectorAll('[role="tab"]')).toHaveLength(6);
    const results = [...element.querySelectorAll<HTMLButtonElement>('.phases button')].find(
      (button) => button.textContent?.includes('Analizează rezultatele'),
    )!;
    results.click();
    fixture.detectChanges();
    expect(navigation().querySelectorAll('[role="tab"]')).toHaveLength(2);
    const forecast = navigation().querySelector('#tab-forecast') as HTMLButtonElement;
    forecast.click();
    fixture.detectChanges();

    expect(view.tab()).toBe('forecast');
    expect(forecast.getAttribute('aria-selected')).toBe('true');
    http.match(() => true);
    fixture.destroy();
  });
  it('filters the scenario gallery by name or code', () => {
    const fixture = component();
    const view = fixture.componentInstance;
    view.query.set('BASE');
    expect(view.visibleScenarios().map((item) => item.id)).toEqual(['scenario-1']);
    view.query.set('nimic');
    fixture.detectChanges();
    expect(view.visibleScenarios()).toEqual([]);
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Niciun scenariu');
    fixture.destroy();
  });
  it.each(['resourcesBusy', 'staffingBusy', 'runSettingsBusy'] as const)(
    'blocks scenario creation, selection and simulation while %s is active',
    (activity) => {
      const fixture = component();
      const view = fixture.componentInstance;
      view.selected.set(scenario);
      view[activity].set(true);

      view.create();
      view.select('scenario-2');
      view.run();

      http.expectNone(() => true);
      expect(view.selected()).toBe(scenario);
      expect(view.busy()).toBe(false);
      expect(view.live.connect).not.toHaveBeenCalled();
      fixture.destroy();
    },
  );
  it('opens the single guided creation flow and honors read-only access', () => {
    const fixture = component(),
      view = fixture.componentInstance;
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    view.create();
    expect(navigate).not.toHaveBeenCalled();
    TestBed.inject(SessionState).session.set({
      username: 'operator.demo',
      roles: ['Operator'],
      expiresAtUtc: '2099-01-01T00:00:00Z',
    });
    view.create();
    expect(navigate).toHaveBeenCalledWith(['/scenario-studio']);
    http.expectNone((request) => request.method === 'POST');
  });
  it('opens the saved scenario from the completion link', () => {
    const params = new Subject<ReturnType<typeof convertToParamMap>>();
    Object.defineProperty(TestBed.inject(ActivatedRoute), 'queryParamMap', { value: params });
    const fixture = component();
    params.next(convertToParamMap({ scenarioId: 'scenario-1' }));
    http.expectOne('/api/scenarios/scenario-1').flush(scenario);
    http.expectOne('/api/scenarios/scenario-1/runs').flush([]);
    expect(fixture.componentInstance.selected()?.scenario.id).toBe('scenario-1');
    fixture.destroy();
  });
  it.each([null, 'older-run'])(
    'loads the latest run or the run requested in the URL (%s)',
    async (requestedRun) => {
      const fixture = component();
      const view = fixture.componentInstance;
      view.select('scenario-1', requestedRun);
      http.expectOne('/api/scenarios/scenario-1').flush(scenario);
      http.expectOne('/api/scenarios/scenario-1/runs').flush([
        { id: 'latest-run', status: 'completed', scenarioRevision: 1, seed: 42 },
        { id: 'older-run', status: 'completed', scenarioRevision: 1, seed: 42 },
      ]);
      await vi.advanceTimersByTimeAsync(0);
      http.expectOne('/api/scenarios/scenario-1/flight-schedule').flush({
        scenarioId: 'scenario-1',
        revision: 1,
        startsAtUtc: scenario.inputs.startsAtUtc,
        durationMinutes: 60,
        flights: [],
        gates: [],
        isSynthetic: true,
      });
      const expectedRun = requestedRun ?? 'latest-run';
      http.expectOne(`/api/runs/${expectedRun}`).flush({
        id: expectedRun,
        status: 'completed',
        metrics: [],
        snapshots: [],
        resultsValid: false,
      });
      expect(view.result()?.id).toBe(expectedRun);
      fixture.destroy();
    },
  );
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
      .expectOne('/api/scenarios/scenario-1/flight-schedule')
      .flush({
        scenarioId: 'scenario-1',
        revision: 1,
        startsAtUtc: scenario.inputs.startsAtUtc,
        durationMinutes: 60,
        flights: [],
        gates: [],
        isSynthetic: true,
      });
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
  it('cancels pending run requests on destruction', async () => {
    const fixture = component();
    const view = fixture.componentInstance;
    view.refreshRun('run-1');
    await vi.advanceTimersByTimeAsync(0);
    const pending = http.expectOne('/api/runs/run-1');
    fixture.destroy();
    expect(pending.cancelled).toBe(true);
  });
  it('shows live running status and retries a lost terminal REST response while SignalR stays connected', async () => {
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
      .expectOne('/api/scenarios/scenario-1/flight-schedule')
      .flush({
        scenarioId: 'scenario-1',
        revision: 1,
        startsAtUtc: scenario.inputs.startsAtUtc,
        durationMinutes: 60,
        flights: [],
        gates: [],
        isSynthetic: true,
      });
    http
      .expectOne('/api/runs/run-1')
      .flush({
        id: 'run-1',
        status: 'planned',
        revision: 1,
        metrics: [],
        snapshots: [],
        resultsValid: false,
      });
    view.live.status.set('live');
    const running = {
      runId: 'run-1',
      status: 'running',
      revision: 2,
      resultsValid: false,
      simulationMinute: 0,
      snapshot: null,
    };
    const state = {
      scenarioId: 'scenario-1',
      sequence: 1,
      resetRequired: false,
      run: running,
      runs: [running],
      optimization: null,
    };
    view.live.state.set(state);
    view.live.updates.next(state);
    expect(view.result()?.status).toBe('running');
    await vi.advanceTimersByTimeAsync(1000);
    http.expectNone('/api/runs/run-1');
    const completed = {
      ...state,
      sequence: 2,
      run: { ...running, status: 'completed', resultsValid: true },
    };
    view.live.state.set(completed);
    view.live.updates.next(completed);
    await vi.advanceTimersByTimeAsync(0);
    http.expectOne('/api/runs/run-1').flush({}, { status: 503, statusText: 'offline' });
    await vi.advanceTimersByTimeAsync(1000);
    http
      .expectOne('/api/runs/run-1')
      .flush({ id: 'run-1', status: 'completed', metrics: [], snapshots: [], resultsValid: true });
    expect(view.result()?.status).toBe('completed');
    fixture.destroy();
  });
});

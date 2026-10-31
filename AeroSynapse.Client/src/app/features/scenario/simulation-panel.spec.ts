import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { Subject } from 'rxjs';
import { OperationsStream } from '../../core/http/operations-stream';
import { apiErrorInterceptor } from '../../core/http/api-error.interceptor';
import { OPERATION_REQUEST_TIMEOUT_MS } from '../../core/http/operation-request';
import { SimulationExecution, SimulationPanel } from './simulation-panel';

describe('Simulation flow', () => {
  let http: HttpTestingController;
  beforeEach(() => {
    const values = new Map<string, string>();
    vi.stubGlobal('localStorage', {
      getItem: (key: string) => values.get(key) ?? null,
      setItem: (key: string, value: string) => values.set(key, value),
      removeItem: (key: string) => values.delete(key),
    });
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([apiErrorInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    TestBed.overrideComponent(SimulationPanel, {
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
    TestBed.resetTestingModule();
    http.verify({ ignoreCancelled: true });
    vi.unstubAllGlobals();
  });

  function setup() {
    const fixture = TestBed.createComponent(SimulationPanel);
    http
      .expectOne('/api/scenarios')
      .flush([
        { id: '91000000-0000-0000-0000-000000000001', name: 'Persisted scenario', revision: 3 },
      ]);
    fixture.componentInstance.selectedId.set('91000000-0000-0000-0000-000000000001');
    fixture.detectChanges();
    return fixture;
  }
  function reply(key: string, status: SimulationExecution['status']): SimulationExecution {
    return {
      commandId: key,
      runId: '92000000-0000-0000-0000-000000000001',
      scenarioId: '91000000-0000-0000-0000-000000000001',
      status,
      correlationId: JSON.parse(localStorage.getItem('aerosynapse.simulation-command.v1')!)
        .correlationId,
      resultsValid: status === 'completed',
      errorCode: null,
      failureReason: null,
    };
  }

  it('submits a typed command, blocks duplicate clicks, and displays persisted results', async () => {
    const fixture = setup();
    const panel = fixture.componentInstance;
    panel.start();
    const request = http.expectOne(
      '/api/scenarios/91000000-0000-0000-0000-000000000001/simulations',
    );
    const key = request.request.headers.get('Idempotency-Key')!;
    expect(request.request.body).toEqual({ scenarioRevision: 3 });
    expect(request.request.headers.get('X-Correlation-ID')).toBeTruthy();
    panel.start();
    http.expectNone('/api/scenarios/91000000-0000-0000-0000-000000000001/simulations');
    request.flush(reply(key, 'pending'));
    await new Promise((resolve) => setTimeout(resolve, 5));
    http.expectOne(`/api/simulations/${key}`).flush(reply(key, 'completed'));
    http.expectOne('/api/runs/92000000-0000-0000-0000-000000000001/metrics?limit=100').flush({
      items: [
        {
          id: 'metric-1',
          name: 'processed_passengers',
          value: 20,
          unit: 'people',
          zoneId: 'zone-1',
        },
      ],
    });
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Finalizată');
    expect(fixture.nativeElement.textContent).toContain('Pasageri procesați: 20');
    expect(panel.active()).toBe(false);
  });

  it('shows only key engine metrics, translated and ordered by checkpoint', () => {
    const fixture = setup();
    const panel = fixture.componentInstance as unknown as {
      metrics: { set(value: unknown[]): void };
      keyMetrics(): { label: string; value: string }[];
    };
    // The engine now emits camelCase names; technical series such as utilization stay hidden.
    panel.metrics.set([
      {
        id: 'a',
        name: 'meanWaitMinutes',
        value: 2.345,
        unit: 'minutes',
        zoneId: '60000000-0000-0000-0000-000000000103',
      },
      {
        id: 'b',
        name: 'meanWaitMinutes',
        value: 1,
        unit: 'minutes',
        zoneId: '60000000-0000-0000-0000-000000000101',
      },
      {
        id: 'c',
        name: 'utilization',
        value: 0.4,
        unit: 'fraction',
        zoneId: '60000000-0000-0000-0000-000000000101',
      },
      {
        id: 'd',
        name: 'processedPassengers',
        value: 80,
        unit: 'passengers',
        zoneId: '60000000-0000-0000-0000-000000000101',
      },
    ]);
    expect(panel.keyMetrics()).toEqual([
      { id: 'd', label: 'Pasageri procesați · CP-1', value: '80 pasageri' },
      { id: 'b', label: 'Așteptare medie · CP-1', value: '1 min' },
      { id: 'a', label: 'Așteptare medie · CP-3', value: '2.3 min' },
    ]);
  });

  it('preserves the same key and revision when confirmation is lost', async () => {
    const panel = setup().componentInstance;
    panel.start();
    const original = http.expectOne(
      '/api/scenarios/91000000-0000-0000-0000-000000000001/simulations',
    );
    const key = original.request.headers.get('Idempotency-Key')!;
    original.error(new ProgressEvent('error'));
    await new Promise((resolve) => setTimeout(resolve, 5));
    http.expectOne(`/api/simulations/${key}`).flush(reply(key, 'running'));
    panel.resume();
    const replay = http.expectOne(
      '/api/scenarios/91000000-0000-0000-0000-000000000001/simulations',
    );
    expect(replay.request.headers.get('Idempotency-Key')).toBe(key);
    expect(replay.request.body).toEqual({ scenarioRevision: 3 });
    replay.flush(reply(key, 'failed'));
    expect(panel.execution()?.status).toBe('failed');
    http.expectNone('/api/runs/92000000-0000-0000-0000-000000000001/metrics?limit=100');
  });

  it('restores a pending command after navigation or reload', async () => {
    const fixture = setup();
    fixture.componentInstance.start();
    const request = http.expectOne(
      '/api/scenarios/91000000-0000-0000-0000-000000000001/simulations',
    );
    const key = request.request.headers.get('Idempotency-Key')!;
    request.flush(reply(key, 'pending'));
    fixture.destroy();
    const restored = TestBed.createComponent(SimulationPanel);
    http.expectOne('/api/scenarios').flush([]);
    await new Promise((resolve) => setTimeout(resolve, 5));
    http.expectOne(`/api/simulations/${key}`).flush(reply(key, 'failed'));
    expect(restored.componentInstance.command()?.key).toBe(key);
    http.expectNone('/api/scenarios/91000000-0000-0000-0000-000000000001/simulations');
  });
  it('shows running from the stream and retries terminal REST reads without resubmitting the command', async () => {
    vi.useFakeTimers();
    try {
      const fixture = setup();
      const panel = fixture.componentInstance;
      panel.start();
      const request = http.expectOne(
        '/api/scenarios/91000000-0000-0000-0000-000000000001/simulations',
      );
      const key = request.request.headers.get('Idempotency-Key')!;
      request.flush(reply(key, 'pending'));
      await vi.advanceTimersByTimeAsync(0);
      http.expectOne(`/api/simulations/${key}`).flush(reply(key, 'pending'));
      panel.live.status.set('live');
      const run = {
        runId: '92000000-0000-0000-0000-000000000001',
        status: 'running',
        revision: 2,
        resultsValid: false,
        simulationMinute: 1,
        snapshot: null,
      };
      const state = {
        scenarioId: '91000000-0000-0000-0000-000000000001',
        sequence: 1,
        resetRequired: false,
        run,
        runs: [run],
        optimization: null,
      };
      panel.live.state.set(state);
      panel.live.updates.next(state);
      expect(panel.execution()?.status).toBe('running');
      const completed = {
        ...state,
        sequence: 2,
        run: { ...run, status: 'completed', resultsValid: true },
      };
      panel.live.state.set(completed);
      panel.live.updates.next(completed);
      http.expectOne(`/api/simulations/${key}`).flush({}, { status: 503, statusText: 'offline' });
      await vi.advanceTimersByTimeAsync(2000);
      http.expectOne(`/api/simulations/${key}`).flush(reply(key, 'completed'));
      http
        .expectOne('/api/runs/92000000-0000-0000-0000-000000000001/metrics?limit=100')
        .flush({ items: [] });
      expect(panel.execution()?.status).toBe('completed');
      http.expectNone('/api/scenarios/91000000-0000-0000-0000-000000000001/simulations');
      fixture.destroy();
    } finally {
      vi.useRealTimers();
    }
  });
  it.each(['command', 'scenario', 'run', 'correlation', 'validity', 'status'])(
    'rejects a receipt with invalid %s without losing its idempotent retry',
    (failure) => {
      const panel = setup().componentInstance;
      panel.start();
      const request = http.expectOne(
        '/api/scenarios/91000000-0000-0000-0000-000000000001/simulations',
      );
      const key = request.request.headers.get('Idempotency-Key')!;
      const value = reply(key, 'pending');
      if (failure === 'command') value.commandId = crypto.randomUUID();
      if (failure === 'scenario') value.scenarioId = crypto.randomUUID();
      if (failure === 'run') value.runId = 'invalid';
      if (failure === 'correlation') value.correlationId = crypto.randomUUID();
      if (failure === 'validity') value.resultsValid = true;
      if (failure === 'status') value.status = 'unknown' as SimulationExecution['status'];
      request.flush(value);
      expect(panel.execution()).toBeNull();
      expect(panel.error()).toContain('nu corespunde');
      expect(panel.active()).toBe(true);
      panel.resume();
      const retry = http.expectOne(
        '/api/scenarios/91000000-0000-0000-0000-000000000001/simulations',
      );
      expect(retry.request.headers.get('Idempotency-Key')).toBe(key);
      retry.flush(reply(key, 'failed'));
    },
  );
  it('continues REST recovery when SignalR connects without a run frame', async () => {
    vi.useFakeTimers();
    try {
      const fixture = setup(),
        panel = fixture.componentInstance;
      panel.start();
      const request = http.expectOne(
        '/api/scenarios/91000000-0000-0000-0000-000000000001/simulations',
      );
      const key = request.request.headers.get('Idempotency-Key')!;
      request.flush(reply(key, 'pending'));
      await vi.advanceTimersByTimeAsync(0);
      http.expectOne(`/api/simulations/${key}`).flush(reply(key, 'running'));
      panel.live.status.set('live');
      panel.live.state.set(null);
      await vi.advanceTimersByTimeAsync(2000);
      http.expectOne(`/api/simulations/${key}`).flush(reply(key, 'completed'));
      http
        .expectOne('/api/runs/92000000-0000-0000-0000-000000000001/metrics?limit=100')
        .flush({ items: [] });
      expect(panel.execution()?.status).toBe('completed');
      fixture.destroy();
    } finally {
      vi.useRealTimers();
    }
  });
  it('cancels old metrics and clears completed results when the scenario changes', () => {
    const fixture = setup(),
      panel = fixture.componentInstance;
    panel.start();
    const request = http.expectOne(
      '/api/scenarios/91000000-0000-0000-0000-000000000001/simulations',
    );
    request.flush(reply(request.request.headers.get('Idempotency-Key')!, 'completed'));
    const metrics = http.expectOne(
      '/api/runs/92000000-0000-0000-0000-000000000001/metrics?limit=100',
    );
    fixture.componentRef.setInput('scenarioId', '91000000-0000-0000-0000-000000000002');
    fixture.detectChanges();
    expect(metrics.cancelled).toBe(true);
    expect(panel.execution()).toBeNull();
    expect(panel.metrics()).toEqual([]);
    fixture.destroy();
  });
  it('times out a lost launch and allows replay only with the original body and key', async () => {
    vi.useFakeTimers();
    try {
      const fixture = setup(),
        panel = fixture.componentInstance;
      panel.start();
      const request = http.expectOne(
        '/api/scenarios/91000000-0000-0000-0000-000000000001/simulations',
      );
      const key = request.request.headers.get('Idempotency-Key')!;
      await vi.advanceTimersByTimeAsync(OPERATION_REQUEST_TIMEOUT_MS);
      expect(request.cancelled).toBe(true);
      expect(panel.error()).toContain('Timeout');
      await vi.advanceTimersByTimeAsync(1);
      const recovery = http.expectOne(`/api/simulations/${key}`);
      panel.resume();
      expect(recovery.cancelled).toBe(true);
      const retry = http.expectOne(
        '/api/scenarios/91000000-0000-0000-0000-000000000001/simulations',
      );
      expect(retry.request.body).toEqual(request.request.body);
      expect(retry.request.headers.get('Idempotency-Key')).toBe(key);
      retry.flush(reply(key, 'failed'));
      fixture.destroy();
    } finally {
      vi.useRealTimers();
    }
  });
  it('keeps the host selection synchronized and prevents scenario changes during a pending command', () => {
    const fixture = setup(),
      panel = fixture.componentInstance;
    const selected: string[] = [],
      working: boolean[] = [];
    panel.scenarioSelected.subscribe((value) => selected.push(value));
    panel.working.subscribe((value) => working.push(value));
    panel.select('91000000-0000-0000-0000-000000000002');
    expect(selected).toEqual(['91000000-0000-0000-0000-000000000002']);
    panel.selectedId.set('91000000-0000-0000-0000-000000000001');
    panel.start();
    fixture.detectChanges();
    expect(working.at(-1)).toBe(true);
    const request = http.expectOne(
      '/api/scenarios/91000000-0000-0000-0000-000000000001/simulations',
    );
    panel.select('91000000-0000-0000-0000-000000000002');
    expect(panel.selectedId()).toBe('91000000-0000-0000-0000-000000000001');
    request.flush(reply(request.request.headers.get('Idempotency-Key')!, 'failed'));
    fixture.detectChanges();
    expect(working.at(-1)).toBe(false);
    fixture.destroy();
  });
  it('renders a host-selected scenario after its options arrive asynchronously', async () => {
    const fixture = TestBed.createComponent(SimulationPanel);
    fixture.componentRef.setInput('scenarioId', '91000000-0000-0000-0000-000000000001');
    fixture.detectChanges();
    http
      .expectOne('/api/scenarios')
      .flush([
        { id: '91000000-0000-0000-0000-000000000001', name: 'Persisted scenario', revision: 3 },
      ]);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    const chosen = fixture.nativeElement.querySelector('#simulation-scenario');
    expect(chosen.dataset.scenarioId).toBe('91000000-0000-0000-0000-000000000001');
    expect(chosen.textContent).toContain('Persisted scenario');
    fixture.destroy();
  });
});

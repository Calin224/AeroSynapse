import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { TestBed } from '@angular/core/testing';
import { RunDetails } from './operations.models';
import { QueueForecastPanel } from './queue-forecast-panel';
import { QueueForecastResult, QueuePredictionResult } from './queue-forecast.models';
import { OPERATION_REQUEST_TIMEOUT_MS } from '../../core/http/operation-request';
import { OperationsRun } from '../../core/http/operations-stream';

const run: RunDetails = {
  id: 'run-1',
  seed: 42,
  scenarioRevision: 1,
  status: 'completed',
  revision: 3,
  resultsValid: true,
  failureReason: null,
  retryOfRunId: null,
  metrics: [],
  snapshots: [],
  error: null,
  inputSnapshotJson: JSON.stringify({
    engine: 'simpy-kpi-1.0',
    snapshotIntervalMinutes: 5,
    scenario: { inputs: { durationMinutes: 60 } },
    layout: { layout: { checkpoints: [{ id: 'control-1', name: 'Control Nord' }] } },
  }),
};
function response(): QueueForecastResult {
  const points = Array.from({ length: 15 }, (_, index) => ({
    minute: index + 1,
    arrivals: 5,
    capacity: 2,
    queuePassengers: 5 + 3 * index,
    availableStaff: 2,
  }));
  return {
    runId: run.id,
    kind: 'simulated_estimate',
    formula: 'Q[t+1]=max(0,Q[t]+arrivals-capacity)',
    fromMinute: 0,
    toMinute: 15,
    periodStartUtc: '2026-10-17T08:00:00Z',
    periodEndUtc: '2026-10-17T08:15:00Z',
    queueUnit: 'passengers',
    flowUnit: 'passengers/minute',
    timeUnit: 'simulated minutes',
    queueThresholdPassengers: 20,
    arrivalSource: 'planned_passenger_profiles',
    observationSource: 'captured_snapshot',
    checkpoints: [
      {
        checkpointId: 'control-1',
        initialQueuePassengers: 2,
        serviceRatePerStaffMinute: 1,
        serviceRateSource: 'sampled_service_mean',
        points,
        alert: { minute: 7, queuePassengers: 23, thresholdPassengers: 20 },
        mitigation: { extraStaff: 3, peakQueuePassengers: 2 },
      },
    ],
  };
}

describe('Simulated queue forecast', () => {
  let http: HttpTestingController;
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });
    http = TestBed.inject(HttpTestingController);
  });
  // Every validated forecast is followed by a research prediction; tests about predictions match it themselves.
  afterEach(() => {
    http.match((request) => request.url.includes('/queue-prediction'));
    http.verify();
  });
  function component(value: RunDetails | null = run, calibrated = true) {
    const fixture = TestBed.createComponent(QueueForecastPanel);
    if (calibrated) {
      fixture.componentInstance.alertsEnabled = true;
      fixture.componentInstance.threshold = 20;
    }
    fixture.componentRef.setInput('run', value);
    fixture.detectChanges();
    return fixture;
  }
  it('posts calibrated parameters and distinguishes observed snapshots from forecasts with period and units', () => {
    const fixture = component();
    const view = fixture.componentInstance;
    view.forecast();
    const call = http.expectOne('/api/runs/run-1/queue-forecast');
    expect(call.request.body).toEqual({
      fromMinute: 0,
      horizonMinutes: 15,
      queueThresholdPassengers: 20,
    });
    call.flush(response());
    fixture.detectChanges();
    const text = fixture.nativeElement.textContent;
    expect(text).toContain('FORECAST · simulated estimate');
    expect(text).toContain('Observație / snapshot la minutul 0');
    expect(text).toContain('Control Nord');
    expect(text).toContain('2026-10-17 08:00–2026-10-17 08:15 UTC');
    expect(text).toContain('pasageri/minut');
    expect(text).toContain('depășește pragul 20 pasageri (≈23 pasageri)');
    expect(fixture.nativeElement.querySelector('path.forecast-line')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('path.observed-line')).not.toBeNull();
    expect(text).toContain('+3 operatori');
    expect(text).toContain('mențin vârful la 2 pasageri');
    fixture.destroy();
  });
  it('requires an explicit calibrated threshold instead of inventing a default alert limit', () => {
    const fixture = component(run, false);
    const view = fixture.componentInstance;
    expect(view.alertsEnabled).toBe(false);
    expect(view.threshold).toBeNull();
    expect(view.options()?.queueThresholdPassengers).toBeNull();
    view.alertsEnabled = true;
    view.forecast();
    http.expectNone('/api/runs/run-1/queue-forecast');
    fixture.destroy();
  });
  function progress(minute: number): OperationsRun {
    return {
      runId: run.id,
      status: 'running',
      revision: 2,
      resultsValid: false,
      simulationMinute: minute,
      snapshot: {
        zones: [{ zoneId: 'control-1', queuedPassengers: minute, processedPassengers: 0 }],
        totalZones: 1,
      },
    };
  }
  function liveResponse(minute: number): QueueForecastResult {
    const value = response();
    return {
      ...value,
      observationSource: 'live_simulation',
      fromMinute: minute,
      toMinute: minute + 15,
      checkpoints: value.checkpoints.map((row) => ({
        ...row,
        points: row.points.map((point) => ({ ...point, minute: point.minute + minute })),
        alert: row.alert ? { ...row.alert, minute: row.alert.minute + minute } : null,
      })),
    };
  }
  it('forecasts while a simulation is running and refreshes from new observed minutes', () => {
    const fixture = component({ ...run, status: 'running', resultsValid: false });
    fixture.componentRef.setInput('active', true);
    fixture.componentRef.setInput('progress', progress(3));
    fixture.detectChanges();
    const first = http.expectOne('/api/runs/run-1/queue-forecast/live');
    expect(first.request.body).toEqual({ horizonMinutes: 15, queueThresholdPassengers: 20 });
    first.flush(liveResponse(4));
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('LIVE · minut 3');
    expect(fixture.nativeElement.textContent).toContain('progresul observat al simulării în curs');
    expect(fixture.nativeElement.textContent).toContain('2 operatori × 1 pasageri/min/operator');
    fixture.componentRef.setInput('progress', progress(5));
    fixture.detectChanges();
    http.expectOne('/api/runs/run-1/queue-forecast/live').flush(liveResponse(5));
    fixture.detectChanges();
    expect(fixture.componentInstance.result()?.fromMinute).toBe(5);
    // Live frames are drawn as observed history before the forecast origin.
    const selected = fixture.componentInstance.selected()!;
    expect(selected.observed.map((sample) => sample.minute)).toEqual([3, 5]);
    expect(selected.actual).toEqual([]);
    fixture.destroy();
  });
  it('coalesces live updates during an outstanding request and stops refreshing in a hidden panel', () => {
    const fixture = component({ ...run, status: 'running', resultsValid: false });
    fixture.componentRef.setInput('active', true);
    fixture.componentRef.setInput('progress', progress(1));
    fixture.detectChanges();
    const first = http.expectOne('/api/runs/run-1/queue-forecast/live');
    fixture.componentRef.setInput('progress', progress(2));
    fixture.detectChanges();
    http.expectNone('/api/runs/run-1/queue-forecast/live');
    expect(first.cancelled).toBe(false);
    first.flush(liveResponse(1));
    fixture.detectChanges();
    http.expectOne('/api/runs/run-1/queue-forecast/live').flush(liveResponse(2));
    fixture.detectChanges();
    fixture.componentRef.setInput('active', false);
    fixture.componentRef.setInput('progress', progress(3));
    fixture.detectChanges();
    http.expectNone('/api/runs/run-1/queue-forecast/live');
    fixture.destroy();
  });
  it('invalidates an existing estimate immediately when its threshold is edited', async () => {
    const fixture = component();
    const view = fixture.componentInstance;
    await fixture.whenStable();
    view.forecast();
    http.expectOne('/api/runs/run-1/queue-forecast').flush(response());
    fixture.detectChanges();
    const field: HTMLInputElement = fixture.nativeElement.querySelector('input[name=threshold]');
    field.value = '15';
    field.dispatchEvent(new Event('input', { bubbles: true }));
    fixture.detectChanges();
    await fixture.whenStable();
    expect(view.result()).toBeNull();
    expect(view.threshold).toBe(15);
    fixture.destroy();
  });
  it('sends an explicit disabled threshold and shows that alerts are disabled', () => {
    const fixture = component();
    const view = fixture.componentInstance;
    view.alertsEnabled = false;
    view.forecast();
    const call = http.expectOne('/api/runs/run-1/queue-forecast');
    expect(call.request.body.queueThresholdPassengers).toBeNull();
    const result = response();
    result.queueThresholdPassengers = null;
    result.checkpoints[0].alert = null;
    result.checkpoints[0].mitigation = null;
    call.flush(result);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Alertele sunt dezactivate.');
    expect(fixture.nativeElement.querySelector('line.threshold')).toBeNull();
    fixture.destroy();
  });
  it.each([-1, 0.5, 100001, NaN])('rejects invalid threshold %s without a request', (threshold) => {
    const fixture = component();
    const view = fixture.componentInstance;
    view.threshold = threshold;
    view.forecast();
    http.expectNone('/api/runs/run-1/queue-forecast');
    expect(view.options()).toBeNull();
    fixture.destroy();
  });
  it('uses the captured cadence and keeps the full forecast within the captured period', () => {
    const fixture = component();
    const view = fixture.componentInstance;
    expect(view.minutes()).toEqual([0, 5, 10, 15, 20, 25, 30, 35, 40, 45]);
    view.fromMinute = 45;
    view.changeHorizon(30);
    expect(view.fromMinute).toBe(30);
    expect(view.minutes()).toEqual([0, 5, 10, 15, 20, 25, 30]);
    view.fromMinute = 1;
    view.forecast();
    http.expectNone('/api/runs/run-1/queue-forecast');
    fixture.destroy();
  });
  it('cancels an obsolete request when another run is selected and clears old forecasts', () => {
    const fixture = component();
    const view = fixture.componentInstance;
    view.forecast();
    const call = http.expectOne('/api/runs/run-1/queue-forecast');
    fixture.componentRef.setInput('run', { ...run, id: 'run-2' });
    fixture.detectChanges();
    expect(call.cancelled).toBe(true);
    expect(view.result()).toBeNull();
    expect(view.loading()).toBe(false);
    fixture.destroy();
  });
  it('rejects mismatched results and allows recovery after service errors', () => {
    const fixture = component();
    const view = fixture.componentInstance;
    view.forecast();
    http.expectOne('/api/runs/run-1/queue-forecast').flush({ ...response(), runId: 'other' });
    expect(view.result()).toBeNull();
    expect(view.message()).toContain('nu corespunde');
    view.forecast();
    http
      .expectOne('/api/runs/run-1/queue-forecast')
      .flush({}, { status: 502, statusText: 'Unavailable' });
    expect(view.loading()).toBe(false);
    expect(view.message()).toBeTruthy();
    view.forecast();
    http.expectOne('/api/runs/run-1/queue-forecast').flush(response());
    expect(view.result()).not.toBeNull();
    fixture.destroy();
  });
  it('opens a finished run before its busiest moment and evaluates the forecast against later snapshots', () => {
    const fixture = component();
    fixture.componentRef.setInput('active', true);
    fixture.detectChanges();
    const queues: Record<number, number> = {
      0: 0,
      5: 2,
      10: 6,
      15: 12,
      20: 20,
      25: 30,
      30: 14,
      35: 4,
      40: 1,
      45: 0,
      50: 0,
      55: 0,
      60: 0,
    };
    http.expectOne('/api/runs/run-1/kpis?limit=50000').flush({
      snapshots: Object.entries(queues).map(([minute, queuedPassengers]) => ({
        runId: run.id,
        zoneId: 'control-1',
        minute: +minute,
        queuedPassengers,
      })),
    });
    expect(fixture.componentInstance.fromMinute).toBe(15);
    const call = http.expectOne('/api/runs/run-1/queue-forecast');
    expect(call.request.body).toEqual({
      fromMinute: 15,
      horizonMinutes: 15,
      queueThresholdPassengers: 20,
    });
    const value = response();
    value.fromMinute = 15;
    value.toMinute = 30;
    value.checkpoints[0].initialQueuePassengers = 12;
    value.checkpoints[0].points = value.checkpoints[0].points.map((point) => ({
      ...point,
      minute: point.minute + 15,
    }));
    value.checkpoints[0].alert = { minute: 22, queuePassengers: 23, thresholdPassengers: 20 };
    call.flush(value);
    fixture.detectChanges();
    const view = fixture.componentInstance.selected()!;
    expect(view.observed.map((sample) => sample.minute)).toEqual([0, 5, 10, 15]);
    expect(view.actual.map((sample) => sample.minute)).toEqual([20, 25, 30]);
    expect(view.backtestError).toBeCloseTo(
      (Math.abs(20 - 17) + Math.abs(30 - 32) + Math.abs(14 - 47)) / 3,
    );
    const text = fixture.nativeElement.textContent;
    expect(text).toContain('Observat ulterior · doar pentru evaluare');
    expect(text).toContain('Depășire în 7 minute');
    expect(fixture.nativeElement.querySelectorAll('circle.actual').length).toBe(3);
    fixture.destroy();
  });
  function predicted(
    forecast: QueueForecastResult,
    deterministic = forecast.checkpoints[0].points.at(-1)!.queuePassengers,
  ): QueuePredictionResult {
    return {
      runId: forecast.runId,
      kind: 'simulated_estimate',
      validity: 'synthetic_only: no operational accuracy is claimed without authorised real data',
      fromMinute: forecast.fromMinute,
      toMinute: forecast.toMinute,
      queueThresholdPassengers: forecast.queueThresholdPassengers,
      observationSource: forecast.observationSource,
      predictor: {
        kind: 'ml',
        modelVersion: 'us038-residual-cqr-calibrated-2.0+0123456789abcdef',
        estimator: 'hgb_shallow',
        datasetVersion: 'us038-synthetic-2.0',
        datasetHash: 'a'.repeat(64),
        trainingDataSource: 'synthetic_simpy_simulation',
        formula: null,
        fallbackReason: null,
      },
      checkpoints: [
        {
          checkpointId: 'control-1',
          deterministicQueuePassengers: deterministic,
          predictedQueuePassengers: deterministic + 2,
          predictionInterval: { lowerPassengers: 30, upperPassengers: 60, nominalCoverage: 0.8 },
          exceedanceProbability: 0.97,
        },
      ],
    };
  }
  it('asks the research predictor with the same inputs only after a validated forecast and shows it beside the forecast', () => {
    const fixture = component();
    const view = fixture.componentInstance;
    view.forecast();
    http.expectNone('/api/runs/run-1/queue-prediction');
    http.expectOne('/api/runs/run-1/queue-forecast').flush(response());
    const call = http.expectOne('/api/runs/run-1/queue-prediction');
    expect(call.request.body).toEqual({
      fromMinute: 0,
      horizonMinutes: 15,
      queueThresholdPassengers: 20,
    });
    call.flush(predicted(response()));
    fixture.detectChanges();
    expect(view.prediction()?.predictor.kind).toBe('ml');
    expect(fixture.nativeElement.textContent).toContain('Predictor ML · comparație de cercetare');
    expect(fixture.nativeElement.textContent).toContain('97%');
    fixture.destroy();
  });
  it('rejects a prediction whose deterministic baseline differs from the displayed forecast', () => {
    const fixture = component();
    const view = fixture.componentInstance;
    view.forecast();
    http.expectOne('/api/runs/run-1/queue-forecast').flush(response());
    http.expectOne('/api/runs/run-1/queue-prediction').flush(predicted(response(), 1));
    fixture.detectChanges();
    expect(view.prediction()).toBeNull();
    expect(view.predictionState()).toBe('failed');
    expect(fixture.nativeElement.textContent).toContain(
      'forecast-ul determinist de mai sus rămâne valid',
    );
    expect(view.result()).not.toBeNull();
    fixture.destroy();
  });
  it('drops a live prediction for another minute as stale and clears it when parameters change', () => {
    const fixture = component({ ...run, status: 'running', resultsValid: false });
    fixture.componentRef.setInput('active', true);
    fixture.componentRef.setInput('progress', progress(3));
    fixture.detectChanges();
    http.expectOne('/api/runs/run-1/queue-forecast/live').flush(liveResponse(3));
    const call = http.expectOne('/api/runs/run-1/queue-prediction/live');
    expect(call.request.body).toEqual({ horizonMinutes: 15, queueThresholdPassengers: 20 });
    call.flush(predicted(liveResponse(4)));
    expect(fixture.componentInstance.prediction()).toBeNull();
    expect(fixture.componentInstance.predictionState()).toBe('idle');
    fixture.componentInstance.invalidate();
    expect(fixture.componentInstance.predictionState()).toBe('idle');
    fixture.destroy();
  });
  it.each([null, { ...run, resultsValid: false }, { ...run, inputSnapshotJson: '{}' }])(
    'never forecasts without compatible observations',
    (value) => {
      const fixture = component(value);
      fixture.componentInstance.forecast();
      http.expectNone('/api/runs/run-1/queue-forecast');
      fixture.destroy();
    },
  );
  it('releases a stalled forecast on timeout and permits retry with the selected parameters', async () => {
    vi.useFakeTimers();
    try {
      const fixture = component(),
        view = fixture.componentInstance;
      view.forecast();
      const first = http.expectOne('/api/runs/run-1/queue-forecast');
      await vi.advanceTimersByTimeAsync(OPERATION_REQUEST_TIMEOUT_MS);
      expect(first.cancelled).toBe(true);
      expect(view.loading()).toBe(false);
      expect(view.message()).toContain('Timeout');
      view.forecast();
      const retry = http.expectOne('/api/runs/run-1/queue-forecast');
      expect(retry.request.body).toEqual(first.request.body);
      retry.flush(response());
      fixture.destroy();
    } finally {
      vi.useRealTimers();
    }
  });
});

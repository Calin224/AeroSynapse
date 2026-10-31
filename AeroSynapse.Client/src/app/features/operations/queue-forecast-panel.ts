import { DatePipe, DecimalPipe } from '@angular/common';
import {
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { finalize, Subscription, timeout } from 'rxjs';
import { OPERATION_REQUEST_TIMEOUT_MS, operationError } from '../../core/http/operation-request';
import { ApiClient } from '../../core/http/api-client';
import { OperationsRun } from '../../core/http/operations-stream';
import { KpiResult } from './kpi.models';
import { QueueForecastChart } from './queue-forecast-chart';
import { QueueForecastRadar } from './queue-forecast-radar';
import { QueueForecastTimeline } from './queue-forecast-timeline';
import { QueuePredictionCard } from './queue-prediction-card';
import { RunDetails } from './operations.models';
import {
  QueueForecastOptions,
  QueueForecastResult,
  QueuePredictionResult,
} from './queue-forecast.models';
import {
  CheckpointOutlook,
  QueueHistory,
  busiestMinute,
  fromSnapshots,
  outlooks,
  withObservation,
} from './queue-forecast.view';

const HISTORICAL_REFRESH_MS = 250;

@Component({
  selector: 'app-queue-forecast-panel',
  imports: [
    FormsModule,
    RouterLink,
    DecimalPipe,
    DatePipe,
    QueueForecastChart,
    QueueForecastRadar,
    QueueForecastTimeline,
    QueuePredictionCard,
  ],
  templateUrl: './queue-forecast-panel.html',
  styleUrl: './queue-forecast-panel.css',
})
export class QueueForecastPanel {
  readonly run = input<RunDetails | null>(null);
  readonly progress = input<OperationsRun | null>(null);
  readonly active = input(false);
  readonly disabled = input(false);
  readonly loading = signal(false);
  readonly message = signal('');
  readonly result = signal<QueueForecastResult | null>(null);
  /** Research predictor, requested only after a validated forecast and checked against it. */
  readonly prediction = signal<QueuePredictionResult | null>(null);
  readonly predictionState = signal<'idle' | 'loading' | 'failed'>('idle');
  readonly capture = signal<{
    duration: number;
    interval: number;
    names: Record<string, string>;
  } | null>(null);
  readonly horizonMinutes = signal(15);
  /** Observed queues only: live progress frames, or the captured KPI snapshots of a finished run. */
  readonly history = signal<QueueHistory>({});
  readonly historyState = signal<'idle' | 'loading' | 'ready' | 'failed'>('idle');
  readonly selectedId = signal<string | null>(null);
  readonly horizons = [15, 20, 25, 30];
  readonly isLive = computed(
    () =>
      this.run()?.status === 'running' &&
      (this.progress()?.runId !== this.run()?.id || this.progress()?.status === 'running'),
  );
  readonly latestMinute = computed(() =>
    this.progress()?.runId === this.run()?.id ? (this.progress()?.simulationMinute ?? null) : null,
  );
  readonly minutes = computed(() => {
    const capture = this.capture();
    if (!capture) return [];
    const count = Math.max(
      0,
      Math.floor((capture.duration - this.horizonMinutes()) / capture.interval) + 1,
    );
    return Array.from({ length: count }, (_, index) => index * capture.interval);
  });
  readonly outlooks = computed(() => {
    const result = this.result();
    return result ? outlooks(result, this.history()) : [];
  });
  readonly selected = computed(
    () =>
      this.outlooks().find((row) => row.id === this.selectedId()) ?? this.outlooks().at(0) ?? null,
  );
  readonly summary = computed(() => {
    const rows = this.outlooks(),
      result = this.result();
    if (!result || !rows.length) return null;
    const breaches = rows.filter((row) => row.risk === 'breach');
    const staffed = rows
      .map((row) => row.series.mitigation?.extraStaff)
      .filter((value): value is number => value !== null && value !== undefined);
    return {
      breaches: breaches.length,
      total: rows.length,
      firstBreach: breaches.length
        ? Math.min(...breaches.map((row) => row.series.alert!.minute))
        : null,
      peak: rows.reduce((best, row) => (row.peak.queue > best.peak.queue ? row : best)),
      arrivals: rows.reduce((sum, row) => sum + row.arrivals, 0),
      extraStaff: staffed.reduce((sum, value) => sum + value, 0),
      unrecoverable: rows.filter(
        (row) => row.series.mitigation && row.series.mitigation.extraStaff === null,
      ).length,
      backtest: this.backtest(rows),
    };
  });
  private readonly context = computed(
    () => `${this.run()?.id}/${this.run()?.status}/${this.run()?.inputSnapshotJson}`,
  );
  private readonly parametersRevision = signal(0);
  private readonly api = inject(ApiClient);
  private pending?: Subscription;
  private predictionRequest?: Subscription;
  private historyRequest?: Subscription;
  private refreshTimer?: ReturnType<typeof setTimeout>;
  private lastAutoKey = '';
  fromMinute = 0;
  threshold: number | null = null;
  alertsEnabled = false;

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.pending?.unsubscribe();
      this.predictionRequest?.unsubscribe();
      this.historyRequest?.unsubscribe();
      clearTimeout(this.refreshTimer);
    });
    effect(() => {
      this.context();
      const live = this.isLive();
      untracked(() => {
        const run = this.run();
        this.invalidate();
        this.capture.set(null);
        this.fromMinute = 0;
        this.lastAutoKey = '';
        this.historyRequest?.unsubscribe();
        this.history.set({});
        this.historyState.set('idle');
        this.selectedId.set(null);
        if (!run || (!run.resultsValid && !live)) return;
        try {
          const captured = JSON.parse(run.inputSnapshotJson ?? '{}');
          const inputs = captured.scenario?.inputs;
          if (captured.engine !== 'simpy-kpi-1.0' || inputs?.passengerGeneration?.flow) {
            this.message.set(
              'Prognoza necesită observații KPI, fără fluxuri alternative de pasageri.',
            );
            return;
          }
          const duration = inputs?.durationMinutes;
          const interval = captured.snapshotIntervalMinutes ?? 1;
          if (
            !Number.isInteger(duration) ||
            duration < 1 ||
            duration > 10080 ||
            ![1, 2, 3, 4, 5].includes(interval)
          )
            throw new Error();
          this.capture.set({
            duration,
            interval,
            names: Object.fromEntries(
              (captured.layout?.layout?.checkpoints ?? []).map(
                (cp: { id: string; name: string }) => [cp.id, cp.name],
              ),
            ),
          });
        } catch {
          this.message.set('Intrările capturate ale rulării nu sunt disponibile.');
        }
      });
    });
    // Live progress frames become the observed history drawn before the forecast origin.
    effect(() => {
      const progress = this.progress(),
        run = this.run();
      if (
        !progress ||
        progress.runId !== run?.id ||
        progress.simulationMinute === null ||
        !progress.snapshot ||
        !this.isLive()
      )
        return;
      untracked(() =>
        this.history.update((history) =>
          withObservation(history, progress.simulationMinute!, progress.snapshot!.zones),
        ),
      );
    });
    // A finished run opens on its captured KPI series, positioned just before the busiest moment.
    effect(() => {
      if (
        !this.active() ||
        this.isLive() ||
        !this.capture() ||
        this.disabled() ||
        this.historyState() !== 'idle'
      )
        return;
      untracked(() => this.loadHistory());
    });
    effect(() => {
      const minute = this.latestMinute();
      const revision = this.parametersRevision();
      const key = `${this.run()?.id}/${minute}/${revision}`;
      if (
        !this.active() ||
        !this.isLive() ||
        !this.capture() ||
        this.disabled() ||
        this.loading() ||
        minute === null ||
        this.lastAutoKey === key
      )
        return;
      untracked(() => {
        if (!this.options()) {
          this.result.set(null);
          return;
        }
        this.lastAutoKey = key;
        this.forecast();
      });
    });
  }

  invalidate() {
    this.pending?.unsubscribe();
    this.result.set(null);
    this.message.set('');
    this.predictionRequest?.unsubscribe();
    this.prediction.set(null);
    this.predictionState.set('idle');
  }
  parametersChanged() {
    this.invalidate();
    this.parametersRevision.update((value) => value + 1);
    clearTimeout(this.refreshTimer);
    if (!this.isLive() && this.active())
      this.refreshTimer = setTimeout(() => this.forecast(), HISTORICAL_REFRESH_MS);
  }
  changeHorizon(value: number) {
    this.horizonMinutes.set(value);
    if (!this.minutes().includes(this.fromMinute)) this.fromMinute = this.minutes().at(-1) ?? 0;
    this.parametersChanged();
  }
  changeOrigin(value: number) {
    if (value === this.fromMinute) return;
    this.fromMinute = value;
    this.parametersChanged();
  }
  options(): QueueForecastOptions | null {
    const capture = this.capture(),
      origin = this.isLive() ? (this.latestMinute() ?? 0) : this.fromMinute;
    if (
      !capture ||
      (!this.isLive() && !this.minutes().includes(origin)) ||
      origin + this.horizonMinutes() > capture.duration ||
      !Number.isInteger(this.horizonMinutes()) ||
      this.horizonMinutes() < 15 ||
      this.horizonMinutes() > 30 ||
      (this.alertsEnabled && !this.validThreshold())
    )
      return null;
    return {
      fromMinute: origin,
      horizonMinutes: this.horizonMinutes(),
      queueThresholdPassengers: this.alertsEnabled ? this.threshold : null,
    };
  }
  validThreshold() {
    return (
      this.threshold !== null &&
      Number.isInteger(this.threshold) &&
      this.threshold >= 0 &&
      this.threshold <= 100000
    );
  }
  forecast() {
    const run = this.run(),
      options = this.options(),
      live = this.isLive();
    clearTimeout(this.refreshTimer);
    if (!run || (!run.resultsValid && !live) || this.disabled() || this.loading() || !options)
      return;
    this.pending?.unsubscribe();
    this.message.set('');
    if (!live) this.result.set(null);
    this.loading.set(true);
    const body = live
      ? {
          horizonMinutes: options.horizonMinutes,
          queueThresholdPassengers: options.queueThresholdPassengers,
        }
      : options;
    this.pending = this.api
      .post<QueueForecastResult>(`runs/${run.id}/queue-forecast${live ? '/live' : ''}`, body)
      .pipe(
        timeout(OPERATION_REQUEST_TIMEOUT_MS),
        finalize(() => this.loading.set(false)),
      )
      .subscribe({
        next: (result) => {
          if (
            result.runId !== run.id ||
            result.kind !== 'simulated_estimate' ||
            (!live && result.fromMinute !== options.fromMinute) ||
            result.observationSource !== (live ? 'live_simulation' : 'captured_snapshot') ||
            result.toMinute - result.fromMinute !== options.horizonMinutes ||
            result.queueThresholdPassengers !== options.queueThresholdPassengers
          ) {
            this.message.set(
              'Prognoza primită nu corespunde rulării și parametrilor selectați. Reîncearcă.',
            );
            return;
          }
          this.result.set(result);
          this.predict(run, body, live, result);
        },
        error: (error) => {
          this.message.set(operationError(error));
        },
      });
  }
  predict(run: RunDetails, body: object, live: boolean, forecast: QueueForecastResult) {
    this.predictionRequest?.unsubscribe();
    this.prediction.set(null);
    this.predictionState.set('loading');
    this.predictionRequest = this.api
      .post<QueuePredictionResult>(`runs/${run.id}/queue-prediction${live ? '/live' : ''}`, body)
      .pipe(timeout(OPERATION_REQUEST_TIMEOUT_MS))
      .subscribe({
        next: (prediction) => {
          // Live origins move every minute: an answer for another minute is stale, not wrong; the next refresh replaces it.
          if (live && prediction?.fromMinute !== forecast.fromMinute) {
            this.predictionState.set('idle');
            return;
          }
          this.predictionState.set(this.correlates(prediction, forecast) ? 'idle' : 'failed');
          if (this.predictionState() === 'idle') this.prediction.set(prediction);
        },
        error: () => this.predictionState.set('failed'),
      });
  }
  private correlates(prediction: QueuePredictionResult | null, forecast: QueueForecastResult) {
    if (
      !prediction ||
      prediction.runId !== forecast.runId ||
      prediction.fromMinute !== forecast.fromMinute ||
      prediction.toMinute !== forecast.toMinute ||
      prediction.queueThresholdPassengers !== forecast.queueThresholdPassengers ||
      prediction.observationSource !== forecast.observationSource ||
      prediction.checkpoints.length !== forecast.checkpoints.length
    )
      return false;
    return forecast.checkpoints.every(
      (row) =>
        prediction.checkpoints.find((item) => item.checkpointId === row.checkpointId)
          ?.deterministicQueuePassengers === row.points.at(-1)?.queuePassengers,
    );
  }
  loadHistory() {
    const run = this.run();
    if (!run?.resultsValid) return;
    this.historyRequest?.unsubscribe();
    this.historyState.set('loading');
    this.historyRequest = this.api
      .get<KpiResult>(`runs/${run.id}/kpis?limit=50000`)
      .pipe(timeout(OPERATION_REQUEST_TIMEOUT_MS))
      .subscribe({
        next: (report) => {
          this.history.set(
            fromSnapshots(report.snapshots.filter((frame) => frame.runId === run.id)),
          );
          this.historyState.set('ready');
          const peak = busiestMinute(this.history()),
            allowed = this.minutes();
          if (peak !== null && allowed.length)
            this.fromMinute =
              [...allowed]
                .reverse()
                .find((minute) => minute <= peak - Math.ceil(this.horizonMinutes() / 2)) ??
              allowed[0];
          this.forecast();
        },
        error: () => {
          this.historyState.set('failed');
          this.forecast();
        },
      });
  }
  select(id: string) {
    this.selectedId.set(id);
  }
  name(id: string) {
    return this.capture()?.names[id] ?? `Checkpoint ${id.slice(0, 8)}`;
  }
  windowStart() {
    return this.result()?.fromMinute ?? this.latestMinute() ?? 0;
  }
  private backtest(rows: CheckpointOutlook[]) {
    const samples = rows.reduce((sum, row) => sum + row.actual.length, 0);
    if (!samples) return null;
    const error =
      rows.reduce((sum, row) => sum + (row.backtestError ?? 0) * row.actual.length, 0) / samples;
    return { samples, error };
  }
}

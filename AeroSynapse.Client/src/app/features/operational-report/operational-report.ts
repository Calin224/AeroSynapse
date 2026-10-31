import { DecimalPipe } from '@angular/common';
import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  Observable,
  Subscription,
  catchError,
  forkJoin,
  from,
  map,
  of,
  switchMap,
} from 'rxjs';
import { ApiClient } from '../../core/http/api-client';
import { SimulationContext } from '../../core/simulation-context';
import { RunPicker } from '../../shared/ui/run-picker';
import { RunComparison } from '../operations/comparison.models';
import { RunDetails } from '../operations/operations.models';
import { prepareReplay, ReplayEnvelope } from '../replay/replay.models';
import {
  REPORT_METRICS,
  MetricKey,
  ReportPair,
  ReportRun,
  compatible,
  reportChart,
  reportZones,
  summaryRows,
  temporalRows,
  validRunId,
} from './report.models';

@Component({
  selector: 'app-operational-report',
  imports: [FormsModule, DecimalPipe, RunPicker],
  templateUrl: './operational-report.html',
  styleUrl: './operational-report.css',
})
export class OperationalReport {
  private readonly api = inject(ApiClient);
  private readonly simulation = inject(SimulationContext);
  private readonly destroy = inject(DestroyRef);
  private pending?: Subscription;
  readonly pair = signal<ReportPair | null>(null);
  readonly loading = signal(false);
  readonly message = signal('');
  readonly zoneId = signal('');
  readonly metricKey = signal<MetricKey>('queuedPassengers');
  readonly page = signal(0);
  readonly metrics = REPORT_METRICS;
  readonly zones = computed(() => (this.pair() ? reportZones(this.pair()!) : []));
  readonly metric = computed(() => REPORT_METRICS.find((row) => row.key === this.metricKey())!);
  readonly summary = computed(() => (this.pair() ? summaryRows(this.pair()!, this.zoneId()) : []));
  readonly rows = computed(() =>
    this.pair() ? temporalRows(this.pair()!, this.zoneId(), this.metric()) : [],
  );
  readonly visibleRows = computed(() =>
    this.rows().slice(this.page() * 50, (this.page() + 1) * 50),
  );
  readonly chart = computed(() =>
    this.pair() ? reportChart(this.pair()!, this.zoneId(), this.metric()) : null,
  );
  readonly comparable = computed(() => (this.pair() ? compatible(this.pair()!) : false));
  readonly runs = computed(() =>
    this.pair()
      ? [this.pair()!.baseline, ...(this.pair()!.candidate ? [this.pair()!.candidate!] : [])]
      : [],
  );
  baselineId = '';
  candidateId = '';
  constructor() {
    this.destroy.onDestroy(() => this.pending?.unsubscribe());
    inject(ActivatedRoute)
      .queryParamMap.pipe(takeUntilDestroyed(this.destroy))
      .subscribe((query) => {
        const baseline = query.get('baselineRunId');
        if (baseline) {
          this.baselineId = baseline;
          this.candidateId = query.get('candidateRunId') ?? '';
          this.load();
        }
      });
  }
  selectionChanged() {
    this.pending?.unsubscribe();
    this.pair.set(null);
    this.zoneId.set('');
    this.page.set(0);
    this.message.set('');
    this.loading.set(false);
  }
  filterChanged() {
    this.page.set(0);
  }
  private readRun(id: string): Observable<ReportRun> {
    return this.api.get<RunDetails>(`runs/${id}`).pipe(
      switchMap((run) => {
        if (run.id !== id) throw new Error('Răspunsul nu corespunde runId-ului solicitat.');
        const result: ReportRun = {
          id,
          status: run.status,
          resultsValid: run.resultsValid,
          failureReason: run.failureReason ?? run.error?.message ?? null,
          replay: null,
          message: '',
        };
        if (run.status !== 'completed' || !run.resultsValid) return of(result);
        return this.api.get<ReplayEnvelope>(`runs/${id}/replay`).pipe(
          switchMap((value) => from(prepareReplay(value, id))),
          map((replay) => ({ ...result, replay })),
          catchError((error) => of({ ...result, message: this.describe(error) })),
        );
      }),
      catchError((error) =>
        of({
          id,
          status: 'indisponibil',
          resultsValid: false,
          failureReason: null,
          replay: null,
          message: this.describe(error),
        }),
      ),
    );
  }
  load() {
    const baseline = this.baselineId.trim().toLowerCase(),
      candidate = this.candidateId.trim().toLowerCase();
    this.selectionChanged();
    if (
      !validRunId(baseline) ||
      (candidate && (!validRunId(candidate) || candidate === baseline))
    ) {
      this.message.set(
        'Introdu un runId UUID valid pentru Înainte și, opțional, un runId diferit pentru După.',
      );
      return;
    }
    this.baselineId = baseline;
    this.candidateId = candidate;
    this.loading.set(true);
    const comparison = candidate
      ? this.api.get<RunComparison>(`runs/${baseline}/compare/${candidate}`).pipe(
          map((value) => {
            if (value.baseline.id !== baseline || value.candidate.id !== candidate)
              throw new Error('Comparația nu corespunde selecției de runId.');
            return { value, error: '' };
          }),
          catchError((error) => of({ value: null, error: this.describe(error) })),
        )
      : of({ value: null, error: '' });
    this.pending = forkJoin({
      baseline: this.readRun(baseline),
      candidate: candidate ? this.readRun(candidate) : of(null),
      comparison,
    }).subscribe((value) => {
      this.pair.set({
        baseline: value.baseline,
        candidate: value.candidate,
        comparison: value.comparison.value,
        comparisonError: value.comparison.error,
      });
      const baselineReplay = value.baseline.replay;
      if (baselineReplay)
        this.simulation.remember(
          baselineReplay.data.captured.scenario.scenario.id,
          value.baseline.id,
        );
      const preferred =
        value.baseline.replay?.data.captured.scenario.inputs.staffAllocations[0]?.checkpointId;
      this.zoneId.set(
        this.zones().find((zone) => zone.id === preferred)?.id ?? this.zones()[0]?.id ?? '',
      );
      this.loading.set(false);
    });
  }
  private describe(error: unknown) {
    return error instanceof Error ? error.message : 'Datele de simulare nu sunt disponibile.';
  }
}

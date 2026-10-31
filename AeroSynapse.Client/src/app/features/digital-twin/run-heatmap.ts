import {
  Component,
  computed,
  DestroyRef,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { forkJoin, Subscription, switchMap, timeout } from 'rxjs';
import { OPERATION_REQUEST_TIMEOUT_MS, operationError } from '../../core/http/operation-request';
import { ApiClient } from '../../core/http/api-client';
import { OperationsRun } from '../../core/http/operations-stream';
import { RunDetails } from '../operations/operations.models';
import { DigitalTwin } from './digital-twin';
import { appendProgress, capturedHeatmapRun, mergeHeatmapRuns } from './heatmap-adapters';
import { comparable, HeatmapDataset, HeatmapRun } from './heatmap-model';
import { TwinScene } from './twin-model';

interface Recommendation {
  recommendation: { baselineRunId: string };
  recommendedRunId: string;
}
@Component({
  selector: 'app-run-heatmap',
  imports: [DigitalTwin],
  template: `
    <section class="run-heatmap" aria-label="Aglomerația run-ului în 3D">
      <header>
        <h3>Aglomerație în 3D</h3>
        <button type="button" [disabled]="loading() || !run()?.resultsValid" (click)="compare()">
          {{ loading() ? 'Se încarcă comparația…' : 'Compară baseline / optimizat' }}
        </button>
      </header>
      @if (error()) {
        <p role="status">{{ error() }}</p>
      }
      @if (dataset(); as data) {
        <app-digital-twin [scene]="scene" [heatmap]="data" />
      } @else {
        <p>Intrările capturate ale run-ului nu sunt disponibile pentru maparea capacității.</p>
      }
    </section>
  `,
  styles: `
    header {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      justify-content: space-between;
      gap: 10px;
    }
    p {
      font-size: 12px;
      color: var(--muted);
    }
  `,
})
export class RunHeatmap {
  readonly run = input<RunDetails | null>(null);
  readonly progress = input<OperationsRun | null>(null);
  readonly scene: TwinScene = { aircraft: [] };
  protected readonly loading = signal(false);
  protected readonly error = signal('');
  private readonly observed = signal<HeatmapRun | null>(null);
  private readonly comparison = signal<HeatmapDataset | null>(null);
  protected readonly dataset = computed(
    () => this.comparison() ?? (this.observed() ? { baseline: this.observed()! } : null),
  );
  private readonly api = inject(ApiClient);
  private readonly destroy = inject(DestroyRef);
  private request?: Subscription;
  private generation = 0;
  private context = '';
  constructor() {
    effect(() => {
      const run = this.run();
      if (run?.id !== this.context) {
        this.context = run?.id ?? '';
        ++this.generation;
        this.request?.unsubscribe();
        this.comparison.set(null);
        this.loading.set(false);
        this.error.set('');
        this.observed.set(null);
      }
      const captured = run ? capturedHeatmapRun(run) : null;
      this.observed.set(captured ? mergeHeatmapRuns(untracked(this.observed), captured) : null);
    });
    effect(() => {
      const current = this.observed(),
        progress = this.progress();
      if (!current || !progress) return;
      const next = appendProgress(current, progress);
      if (next !== current) this.observed.set(next);
    });
  }
  protected compare(): void {
    const run = this.run();
    if (!run?.resultsValid || this.loading()) return;
    const generation = ++this.generation;
    this.loading.set(true);
    this.error.set('');
    this.request = this.api
      .get<Recommendation[]>(`runs/${run.id}/recommendations`)
      .pipe(
        switchMap((items) => {
          const recommendation = items[0];
          if (!recommendation)
            throw new Error('Nu există o recomandare finalizată pentru acest run.');
          return forkJoin({
            baseline: this.api.get<RunDetails>(
              `runs/${recommendation.recommendation.baselineRunId}`,
            ),
            optimized: this.api.get<RunDetails>(`runs/${recommendation.recommendedRunId}`),
          });
        }),
        timeout(OPERATION_REQUEST_TIMEOUT_MS),
        takeUntilDestroyed(this.destroy),
      )
      .subscribe({
        next: ({ baseline, optimized }) => {
          if (generation !== this.generation) return;
          this.loading.set(false);
          const base = capturedHeatmapRun(baseline),
            candidate = capturedHeatmapRun(optimized);
          const dataset =
            base && candidate
              ? {
                  baseline: mergeHeatmapRuns(this.observed(), base),
                  optimized: mergeHeatmapRuns(this.observed(), candidate),
                }
              : null;
          if (!dataset || !comparable(dataset)) {
            this.error.set('Comparația necesită rezultate valide pe aceeași versiune de layout.');
            return;
          }
          this.comparison.set(dataset);
        },
        error: (error: unknown) => {
          if (generation !== this.generation) return;
          this.loading.set(false);
          this.error.set(operationError(error));
        },
      });
  }
}

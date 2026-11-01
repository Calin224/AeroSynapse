import { Component, DestroyRef, computed, inject, signal, viewChild } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import { Observable, Subscription, catchError, forkJoin, from, map, of, switchMap } from 'rxjs';
import { ApiClient } from '../../core/http/api-client';
import { SimulationContext } from '../../core/simulation-context';
import { RunPicker, statusLabels } from '../../shared/ui/run-picker';
import { RunComparison } from '../operations/comparison.models';
import { RunDetails } from '../operations/operations.models';
import { prepareReplay, ReplayEnvelope } from '../replay/replay.models';
import {
  ReportRun,
  checkpointResults,
  checkpointRows,
  comparisonProblem,
  kpiCards,
  minutes,
  num,
  people,
  queueChart,
  summary,
  totals,
  validRunId,
  verdict,
} from './report.models';

@Component({
  selector: 'app-operational-report',
  imports: [FormsModule, RunPicker],
  templateUrl: './operational-report.html',
  styleUrl: './operational-report.css',
})
export class OperationalReport {
  private readonly api = inject(ApiClient);
  private readonly simulation = inject(SimulationContext);
  private pending?: Subscription;
  private pendingCompare?: Subscription;
  protected readonly num = num;
  protected readonly minutes = minutes;
  protected readonly people = people;
  readonly picker = viewChild(RunPicker);
  readonly runId = signal('');
  readonly run = signal<ReportRun | null>(null);
  readonly loading = signal(false);
  readonly compareId = signal('');
  readonly compared = signal<{ run: ReportRun; problem: string } | null>(null);
  readonly comparing = signal(false);
  readonly queueOpen = signal(false);

  readonly replay = computed(() => this.run()?.replay ?? null);
  readonly results = computed(() => (this.replay() ? checkpointResults(this.replay()!) : []));
  readonly totals = computed(() => totals(this.results()));
  readonly summary = computed(() => summary(this.totals(), this.results().length));
  readonly verdict = computed(() => verdict(this.totals()));
  readonly rows = computed(() => checkpointRows(this.results()));
  readonly otherReplay = computed(() => {
    const compared = this.compared();
    return compared && !compared.problem ? compared.run.replay : null;
  });
  readonly cards = computed(() => {
    const run = this.totals(),
      other = this.otherReplay();
    return run ? kpiCards(run, other ? totals(checkpointResults(other)) : null) : [];
  });
  readonly chart = computed(() =>
    this.replay() ? queueChart(this.replay()!, this.otherReplay()) : null,
  );
  /** Other finished runs of the scenario shown in the picker, plus a run named by the link. */
  readonly choices = computed(() => {
    const picker = this.picker();
    const list = (picker?.runs() ?? [])
      .filter((run) => picker!.usable(run) && run.id !== this.runId())
      .map((run) => ({ id: run.id, label: `${picker!.when(run)} · ${run.id.slice(0, 8)}` }));
    const linked = this.compareId();
    return linked && !list.some((run) => run.id === linked)
      ? [...list, { id: linked, label: `Rularea din link · ${linked.slice(0, 8)}` }]
      : list;
  });

  constructor() {
    const destroy = inject(DestroyRef);
    destroy.onDestroy(() => {
      this.pending?.unsubscribe();
      this.pendingCompare?.unsubscribe();
    });
    inject(ActivatedRoute)
      .queryParamMap.pipe(takeUntilDestroyed(destroy))
      .subscribe((query) => {
        const id = query.get('baselineRunId')?.trim().toLowerCase();
        if (id) this.open(id, query.get('candidateRunId')?.trim().toLowerCase() ?? '');
      });
  }

  open(id: string, compareWith = '') {
    this.reset();
    this.runId.set(id);
    if (!validRunId(id)) {
      this.run.set({ id, status: '', problem: 'Linkul nu conține o rulare validă.', replay: null });
      return;
    }
    this.loading.set(true);
    this.pending = this.loadRun(id).subscribe((run) => {
      this.loading.set(false);
      this.run.set(run);
      if (run.replay) this.simulation.remember(run.replay.data.run.scenarioId, id);
      if (run.replay && compareWith) this.compare(compareWith);
    });
  }

  /** A scenario chosen by hand clears the report, so the picker preselects its newest run. */
  scenarioChanged() {
    this.reset();
    this.runId.set('');
  }

  compare(id: string) {
    this.pendingCompare?.unsubscribe();
    this.compareId.set(id);
    this.compared.set(null);
    this.comparing.set(false);
    const base = this.runId();
    if (!id) return;
    if (!validRunId(id) || id === base) {
      const problem = 'Alege o altă rulare finalizată pentru comparație.';
      this.compared.set({ run: { id, status: '', problem, replay: null }, problem });
      return;
    }
    this.comparing.set(true);
    this.pendingCompare = forkJoin({
      run: this.loadRun(id),
      check: this.api
        .get<RunComparison>(`runs/${base}/compare/${id}`)
        .pipe(catchError(() => of(null))),
    }).subscribe(({ run, check }) => {
      this.comparing.set(false);
      this.compared.set({ run, problem: comparisonProblem(base, run, check) });
    });
  }

  private reset() {
    this.pending?.unsubscribe();
    this.pendingCompare?.unsubscribe();
    this.run.set(null);
    this.loading.set(false);
    this.compareId.set('');
    this.compared.set(null);
    this.comparing.set(false);
  }

  private loadRun(id: string): Observable<ReportRun> {
    const without = (status: string, problem: string): ReportRun => ({
      id,
      status,
      problem,
      replay: null,
    });
    return this.api.get<RunDetails>(`runs/${id}`).pipe(
      switchMap((run) => {
        if (run.id !== id) throw new Error('Serverul a trimis altă rulare decât cea cerută.');
        const reason = run.failureReason ?? run.error?.message;
        if (run.status === 'failed')
          return of(without(run.status, `Rularea a eșuat${reason ? `: ${reason}` : '.'}`));
        if (run.status !== 'completed')
          return of(
            without(
              run.status,
              `Rularea nu s-a terminat (${(statusLabels[run.status] ?? run.status).toLowerCase()}).`,
            ),
          );
        if (!run.resultsValid) return of(without(run.status, 'Rularea nu are rezultate valide.'));
        return this.api.get<ReplayEnvelope>(`runs/${id}/replay`).pipe(
          switchMap((envelope) => from(prepareReplay(envelope, id))),
          map((replay): ReportRun => ({ id, status: run.status, problem: '', replay })),
        );
      }),
      catchError((error) =>
        of(
          without(
            'unavailable',
            error instanceof Error ? error.message : 'Datele rulării nu pot fi încărcate.',
          ),
        ),
      ),
    );
  }
}

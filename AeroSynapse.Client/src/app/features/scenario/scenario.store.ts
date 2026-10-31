import { inject, Injectable } from '@angular/core';
import { catchError, map, Observable, of, startWith, switchMap, tap } from 'rxjs';
import { SimulationContext } from '../../core/simulation-context';
import { ApiClient } from '../../core/http/api-client';
import { ApiError } from '../../core/http/api-error.interceptor';
import { createDataResource } from '../../shared/data-resource';
import { RunDetails, RunSummary, ScenarioSummary } from '../operations/operations.models';
import { presentable, RunOverview, runOverview } from '../control-tower/run-overview';

export type ScenarioPreview =
  | { status: 'loading' }
  | { status: 'ready'; overview: RunOverview; runCount: number }
  | { status: 'none'; runCount: number }
  | { status: 'error'; message: string };

/** Saved scenarios with the selected run, or the newest validated run when none was requested. */
@Injectable()
export class ScenarioStore {
  private readonly api = inject(ApiClient);
  private readonly simulation = inject(SimulationContext);
  readonly resource = createDataResource(
    () => this.api.get<ScenarioSummary[]>('scenarios'),
    (data) => data.length === 0,
  );

  preview(scenarioId: string, runId: string | null = null): Observable<ScenarioPreview> {
    return this.api.get<RunSummary[]>(`scenarios/${scenarioId}/runs?limit=100`).pipe(
      switchMap((runs) => {
        this.simulation.remember(scenarioId);
        const latest = runId ? runs.find((run) => run.id === runId) : runs.find(presentable);
        if (runId && !latest)
          return of<ScenarioPreview>({
            status: 'error',
            message: 'Rularea selectată nu aparține scenariului sau nu mai este disponibilă.',
          });
        if (!latest) return of<ScenarioPreview>({ status: 'none', runCount: runs.length });
        return this.api.get<RunDetails>(`scenarios/${scenarioId}/runs/${latest.id}`).pipe(
          tap((details) => {
            if (details.id !== latest.id || details.scenarioId !== scenarioId)
              throw new Error('Rularea primită nu corespunde scenariului și selecției.');
            this.simulation.remember(scenarioId, details.id);
          }),
          map((details): ScenarioPreview => {
            const overview = runOverview(details);
            return overview
              ? { status: 'ready', overview, runCount: runs.length }
              : {
                  status: 'error',
                  message: 'Rularea selectată nu are rezultate valide și intrări capturate.',
                };
          }),
        );
      }),
      catchError((error: unknown) =>
        of<ScenarioPreview>({
          status: 'error',
          message: error instanceof ApiError ? error.message : 'Rularea nu poate fi încărcată.',
        }),
      ),
      startWith<ScenarioPreview>({ status: 'loading' }),
    );
  }
}

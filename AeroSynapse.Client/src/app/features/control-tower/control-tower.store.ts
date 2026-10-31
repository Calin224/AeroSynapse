import { computed, DestroyRef, inject, Injectable, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  EMPTY,
  Subject,
  Subscription,
  catchError,
  exhaustMap,
  forkJoin,
  map,
  of,
  switchMap,
  takeWhile,
  throwError,
  timer,
  timeout,
  TimeoutError,
} from 'rxjs';
import { ApiClient } from '../../core/http/api-client';
import { SimulationContext } from '../../core/simulation-context';
import { ApiError } from '../../core/http/api-error.interceptor';
import { ResponseFeedback } from '../../core/http/response-feedback';
import { OPERATION_REQUEST_TIMEOUT_MS, operationError } from '../../core/http/operation-request';
import { OperationsRun, OperationsStream } from '../../core/http/operations-stream';
import { appendProgress, mergeHeatmapRuns } from '../digital-twin/heatmap-adapters';
import type { HeatmapRun } from '../digital-twin/heatmap-model';
import { validRunId } from '../operational-report/report.models';
import type {
  RunDetails,
  RunSummary,
  ScenarioDetails,
  ScenarioSummary,
  SceneSummary,
} from '../operations/operations.models';
import { SimulationExecution, simulationReceiptProblem } from '../operations/simulation-command';
import { makeDraft, PRESETS, StudioLayout } from '../scenario-studio/studio.models';
import {
  activeRun,
  capturedContext,
  runHeatmap,
  simulationMinute,
  TowerContext,
  towerIncidents,
  towerMetrics,
  towerScene,
  virtualTime,
} from './tower.models';

/** One page-scoped selection owns the viewport, cards, incidents, resources and outgoing links. */
@Injectable()
export class ControlTowerStore {
  readonly online = inject(ResponseFeedback).online;
  private readonly api = inject(ApiClient);
  private readonly simulation = inject(SimulationContext);
  private readonly destroy = inject(DestroyRef);
  readonly live = inject(OperationsStream);
  private readonly selections = new Subject<{ scenarioId: string | null; runId: string | null }>();
  private readonly watch = new Subject<string | null>();
  private requested = { scenarioId: null as string | null, runId: null as string | null };
  private action?: Subscription;
  private readonly command = signal<{ scenarioId: string; revision: number; key: string } | null>(
    null,
  );
  private readonly commandStorageKey = 'aerosynapse.control-tower.pending-command.v1';
  readonly scenarios = signal<ScenarioSummary[]>([]);
  readonly selected = signal<ScenarioDetails | null>(null);
  readonly selectedLayout = signal<StudioLayout | null>(null);
  readonly context = signal<TowerContext | null>(null);
  readonly runs = signal<RunSummary[]>([]);
  readonly run = signal<RunDetails | null>(null);
  readonly selectedRunId = signal<string | null>(null);
  readonly heatmap = signal<HeatmapRun | null>(null);
  readonly loading = signal(false);
  readonly runLoading = signal(false);
  readonly busy = signal(false);
  readonly message = signal('');
  readonly runError = signal('');
  readonly actionError = signal('');
  private readonly observedMinute = signal<number | null>(null);
  readonly progress = computed<OperationsRun | null>(() => {
    const state = this.live.state(),
      run = this.run();
    if (
      !state ||
      state.scenarioId !== this.selected()?.scenario.id ||
      !run ||
      !activeRun(run.status)
    )
      return null;
    const current =
      state.run?.runId === run.id ? state.run : state.runs.find((row) => row.runId === run.id);
    // A late running event cannot replace a terminal REST response.
    return current ?? null;
  });
  readonly status = computed(() => this.progress()?.status ?? this.run()?.status ?? '');
  readonly metrics = computed(() => towerMetrics(this.run(), this.heatmap()));
  readonly minute = this.observedMinute.asReadonly();
  readonly clock = computed(() => virtualTime(this.context(), this.minute()));
  readonly incidents = computed(() => towerIncidents(this.context(), this.minute()));
  // Only a shift plan makes the staffing depend on the minute; fixed rosters keep one scene.
  readonly scene = computed(() => {
    const context = this.context();
    return towerScene(
      context,
      this.heatmap(),
      context?.scenario.inputs.staffShifts ? this.minute() : null,
    );
  });
  readonly population = computed(
    () =>
      this.context()?.scenario.inputs.populations.reduce((sum, row) => sum + row.count, 0) ?? null,
  );
  readonly canLaunch = computed(
    () =>
      this.online() &&
      !!this.selected() &&
      !this.loading() &&
      !this.busy() &&
      !this.runLoading() &&
      !this.command() &&
      !this.runs().some((row) => activeRun(row.status)),
  );
  readonly retryCommand = computed(
    () => !!this.command() && this.command()?.scenarioId === this.selected()?.scenario.id,
  );
  readonly pendingScenarioId = computed(() => this.command()?.scenarioId ?? null);
  readonly accepted = new Subject<{ scenarioId: string; runId: string | null }>();

  constructor() {
    try {
      const command = JSON.parse(window.localStorage.getItem(this.commandStorageKey) ?? 'null');
      if (
        command &&
        validRunId(command.scenarioId) &&
        validRunId(command.key) &&
        Number.isSafeInteger(command.revision) &&
        command.revision > 0
      )
        this.command.set(command);
    } catch {
      /* Optional storage; an unavailable browser store must not block the dashboard. */
    }
    this.destroy.onDestroy(() => {
      this.action?.unsubscribe();
      this.accepted.complete();
    });
    this.selections
      .pipe(
        switchMap((selection) => {
          this.reset();
          this.requested = selection;
          this.loading.set(true);
          return this.api.get<ScenarioSummary[]>('scenarios').pipe(
            switchMap((rows) => {
              this.scenarios.set(rows);
              if (selection.scenarioId && !validRunId(selection.scenarioId))
                throw new Error('Identificatorul scenariului este invalid.');
              if (selection.runId && !validRunId(selection.runId))
                throw new Error('Identificatorul rulării este invalid.');
              if (selection.runId && !selection.scenarioId)
                throw new Error('Selectează scenariul căruia îi aparține rularea.');
              const id = selection.scenarioId ?? rows[0]?.id;
              if (!id) return of(null);
              if (!rows.some((row) => row.id === id))
                throw new Error('Scenariul selectat nu se află în catalogul activ.');
              return this.api.get<ScenarioDetails>(`scenarios/${id}`).pipe(
                switchMap((scenario) => {
                  if (scenario.scenario.id !== id)
                    throw new Error('Scenariul primit nu corespunde selecției.');
                  return forkJoin({
                    scenario: of(scenario),
                    layout: this.api.get<StudioLayout>(
                      `scenes/${scenario.scenario.sceneId}/layout-versions/${scenario.scenario.layoutVersionId}`,
                    ),
                    runs: this.api.get<RunSummary[]>(`scenarios/${id}/runs?limit=100`),
                  });
                }),
              );
            }),
            timeout(OPERATION_REQUEST_TIMEOUT_MS),
            catchError((error) => {
              this.message.set(this.describe(error));
              return of(null);
            }),
          );
        }),
        takeUntilDestroyed(this.destroy),
      )
      .subscribe((value) => {
        this.loading.set(false);
        if (!value) return;
        if (
          value.layout.version.id !== value.scenario.scenario.layoutVersionId ||
          value.layout.layout.scene.id !== value.scenario.scenario.sceneId
        ) {
          this.message.set('Layout-ul primit nu corespunde scenariului.');
          return;
        }
        this.selected.set(value.scenario);
        this.simulation.remember(value.scenario.scenario.id);
        this.selectedLayout.set(value.layout);
        this.context.set(value);
        this.runs.set(value.runs);
        const runId = this.requested.runId ?? value.runs[0]?.id ?? null;
        this.requested = { scenarioId: value.scenario.scenario.id, runId };
        this.live.connect(value.scenario.scenario.id, runId);
        this.watch.next(runId);
        this.accepted.next({ scenarioId: value.scenario.scenario.id, runId });
      });
    this.watch
      .pipe(
        switchMap((id) => {
          this.runLoading.set(!!id);
          this.selectedRunId.set(id);
          if (!id) return EMPTY;
          const scenarioId = this.selected()!.scenario.id;
          return timer(0, 2000).pipe(
            exhaustMap(() =>
              this.api.get<RunDetails>(`scenarios/${scenarioId}/runs/${id}`).pipe(
                timeout(OPERATION_REQUEST_TIMEOUT_MS),
                map((run) => {
                  if (run.id !== id) throw new Error('Rularea primită nu corespunde selecției.');
                  const context = capturedContext(run, scenarioId);
                  return { run, context };
                }),
                catchError((error) => {
                  this.runLoading.set(false);
                  this.runError.set(this.describe(error));
                  return (error instanceof ApiError &&
                    ![0, 408, 429].includes(error.status) &&
                    error.status < 500) ||
                    !(error instanceof ApiError || error instanceof TimeoutError)
                    ? throwError(() => error)
                    : EMPTY;
                }),
              ),
            ),
            takeWhile((value) => activeRun(value.run.status), true),
            catchError(() => EMPTY),
          );
        }),
        takeUntilDestroyed(this.destroy),
      )
      .subscribe(({ run, context }) => {
        this.runLoading.set(false);
        this.runError.set('');
        this.run.set(run);
        this.simulation.remember(context.scenario.scenario.id, run.id);
        this.context.set(context);
        const captured = runHeatmap(run);
        this.heatmap.set(
          captured && activeRun(run.status) ? mergeHeatmapRuns(this.heatmap(), captured) : captured,
        );
        this.observeMinute();
        this.runs.update((rows) => [run, ...rows.filter((row) => row.id !== run.id)]);
      });
    this.live.updates.pipe(takeUntilDestroyed(this.destroy)).subscribe((state) => {
      if (state.scenarioId !== this.selected()?.scenario.id) return;
      this.runs.update((rows) =>
        rows.map((row) => {
          const live = state.runs.find((current) => current.runId === row.id);
          return live && live.revision >= row.revision
            ? { ...row, status: live.status, revision: live.revision }
            : row;
        }),
      );
      const progress = this.progress(),
        heatmap = this.heatmap();
      if (progress && heatmap && activeRun(this.run()?.status ?? '')) {
        this.heatmap.set(appendProgress(heatmap, progress));
        this.observeMinute();
      }
    });
  }

  open(scenarioId: string | null, runId: string | null) {
    if (
      this.selected() &&
      this.requested.scenarioId === scenarioId &&
      this.requested.runId === runId
    )
      return;
    this.selections.next({ scenarioId, runId });
  }
  reload() {
    this.selections.next(this.requested);
  }
  private observeMinute() {
    const next = simulationMinute(this.run(), this.progress(), this.heatmap());
    if (next !== null) this.observedMinute.update((previous) => Math.max(previous ?? 0, next));
  }
  private reset() {
    this.watch.next(null);
    this.live.connect(null);
    this.run.set(null);
    this.heatmap.set(null);
    this.selected.set(null);
    this.selectedLayout.set(null);
    this.context.set(null);
    this.runs.set([]);
    this.message.set('');
    this.runError.set('');
    this.observedMinute.set(null);
    this.action?.unsubscribe();
    this.busy.set(false);
    this.actionError.set('');
  }

  /**
   * Reuses a persisted synthetic scenario. A fresh installation uses the existing Studio preset.
   * Another preset, such as the 8-hour working day, reuses only a scenario created from it.
   */
  demo(preset: 'principal' | 'workday' = 'principal') {
    if (!this.online() || this.busy() || this.loading()) return;
    const name = PRESETS.find((item) => item.id === preset)!.name;
    const existing = this.scenarios().find(
      (row) => row.isSynthetic && (preset === 'principal' || row.name === name),
    );
    if (existing) {
      this.accepted.next({ scenarioId: existing.id, runId: null });
      return;
    }
    this.busy.set(true);
    this.actionError.set('');
    this.action = this.api
      .get<(SceneSummary & { isSynthetic: boolean })[]>('scenes')
      .pipe(
        switchMap((scenes) => {
          const scene = scenes.find((row) => row.isSynthetic);
          if (!scene)
            throw new Error(
              'Nu există un layout sintetic. Inițializează datele demo ale aplicației.',
            );
          return this.api.post<StudioLayout>(`scenes/${scene.id}/layout-versions`);
        }),
        switchMap((layout) =>
          this.api.post<ScenarioDetails>('scenarios', makeDraft(layout, preset)),
        ),
      )
      .subscribe({
        next: (scenario) => {
          this.busy.set(false);
          this.scenarios.update((rows) => [...rows, scenario.scenario]);
          this.accepted.next({ scenarioId: scenario.scenario.id, runId: null });
        },
        error: (error) => {
          this.busy.set(false);
          this.actionError.set(this.describe(error));
        },
      });
  }

  launch() {
    if (!this.canLaunch()) return;
    const scenario = this.selected()!.scenario;
    this.saveCommand({
      scenarioId: scenario.id,
      revision: scenario.revision,
      key: crypto.randomUUID(),
    });
    this.resumeLaunch();
  }
  resumeLaunch() {
    const command = this.command();
    if (!command || this.busy()) return;
    this.busy.set(true);
    this.actionError.set('');
    this.action = this.api
      .post<SimulationExecution>(
        `scenarios/${command.scenarioId}/simulations`,
        { scenarioRevision: command.revision },
        { 'Idempotency-Key': command.key },
      )
      .pipe(timeout(OPERATION_REQUEST_TIMEOUT_MS))
      .subscribe({
        next: (value) => {
          this.busy.set(false);
          const problem = simulationReceiptProblem(value, command);
          if (problem) {
            this.actionError.set(problem);
            return;
          }
          this.saveCommand(null);
          this.simulation.remember(value.scenarioId, value.runId);
          this.accepted.next({ scenarioId: value.scenarioId, runId: value.runId });
        },
        error: (error) => {
          this.busy.set(false);
          if (error instanceof ApiError && [400, 404, 409, 422].includes(error.status))
            this.saveCommand(null);
          this.actionError.set(this.describe(error));
        },
      });
  }
  returnToCommand() {
    const command = this.command();
    if (command) this.accepted.next({ scenarioId: command.scenarioId, runId: null });
  }
  private saveCommand(command: { scenarioId: string; revision: number; key: string } | null) {
    this.command.set(command);
    try {
      if (command) window.localStorage.setItem(this.commandStorageKey, JSON.stringify(command));
      else window.localStorage.removeItem(this.commandStorageKey);
    } catch {
      /* The in-memory command still protects retries when browser storage is disabled. */
    }
  }
  private describe(error: unknown) {
    return operationError(error);
  }
}

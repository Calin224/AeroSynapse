import { DecimalPipe } from '@angular/common';
import {
  afterNextRender,
  Component,
  computed,
  DestroyRef,
  effect,
  ElementRef,
  inject,
  Injector,
  signal,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { finalize, of, Subscription, switchMap, timeout } from 'rxjs';
import { ApiClient } from '../../core/http/api-client';
import { ApiError } from '../../core/http/api-error.interceptor';
import { OPERATION_REQUEST_TIMEOUT_MS, operationError } from '../../core/http/operation-request';
import { OperationsStream } from '../../core/http/operations-stream';
import { ControlTowerStore } from '../control-tower/control-tower.store';
import { activeRun, runStatus } from '../control-tower/tower.models';
import { isOperational, ScenarioEvent } from '../operations/events.models';
import type { ScenarioDetails } from '../operations/operations.models';
import { StaffingPanel } from '../operations/staffing-panel';
import { EvaluationSummaryView } from './evaluation-summary';
import { FlightTimelineView } from './flight-timeline';
import type { StaffingEvaluationDetails } from '../operations/staffing-evaluation.models';
import {
  describeSurge,
  IncidentDesigner,
  incidentCheckpoint,
  incidentProblems,
  newSurge,
  surgePayload,
} from '../scenario-studio/incident-designer';
import {
  approvalProblem,
  evaluationProblem,
  evaluationIdentityProblem,
} from '../operations/staffing-evaluation.guards';
import {
  EvaluationStage,
  evaluationSummary,
  flightTimeline,
  shiftPlanView,
  surgeStartMinute,
  WorkflowStage,
  WORKFLOW_STEPS,
} from './workflow.models';

interface EventCommand {
  scenarioId: string;
  revision: number;
  event: ScenarioEvent;
}

@Component({
  selector: 'app-operator-workflow',
  imports: [
    RouterLink,
    DecimalPipe,
    IncidentDesigner,
    StaffingPanel,
    FlightTimelineView,
    EvaluationSummaryView,
  ],
  providers: [ControlTowerStore, OperationsStream],
  templateUrl: './operator-workflow.component.html',
  styleUrl: './operator-workflow.component.css',
})
export class OperatorWorkflowComponent {
  private readonly element = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);
  readonly tower = inject(ControlTowerStore);
  private readonly api = inject(ApiClient);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly destroy = inject(DestroyRef);
  readonly steps = WORKFLOW_STEPS;
  readonly stage = signal<WorkflowStage>('event');
  readonly draft = signal<ScenarioEvent | null>(null);
  readonly savedEventId = signal<string | null>(null);
  readonly eventBusy = signal(false);
  readonly childBusy = signal(false);
  readonly eventUncertain = signal(false);
  readonly message = signal('');
  readonly evaluation = signal<StaffingEvaluationDetails | null>(null);
  readonly comparisonRead = signal<string | null>(null);
  readonly approved = signal(false);
  readonly historyOnly = signal(false);
  readonly statusLabel = runStatus;
  private eventRequest?: Subscription;
  private eventCommand: EventCommand | null = null;
  private contextId = '';
  private readonly evaluationSourceRunId = signal<string | null>(null);
  readonly draftProblems = computed(() => {
    const event = this.draft(),
      scenario = this.tower.selected(),
      layout = this.tower.selectedLayout();
    return event && scenario && layout
      ? incidentProblems(event, scenario.inputs, layout)
      : ['Lipsește contextul incidentului.'];
  });
  readonly events = computed(
    () =>
      this.tower
        .selected()
        ?.inputs.events.filter(
          (row) => row.type.startsWith('incident.') || isOperational(row.type),
        ) ?? [],
  );
  readonly hasEvent = computed(() => this.events().some((row) => row.id === this.savedEventId()));
  readonly runReady = computed(() => {
    const run = this.tower.run(),
      scenario = this.tower.selected();
    return (
      !!run &&
      !!scenario &&
      this.hasEvent() &&
      !this.draft() &&
      !this.eventUncertain() &&
      !this.eventBusy() &&
      !this.tower.loading() &&
      !this.tower.runLoading() &&
      run.status === 'completed' &&
      run.resultsValid &&
      run.scenarioId === scenario.scenario.id &&
      run.scenarioRevision === scenario.scenario.revision &&
      run.seed === scenario.inputs.seed &&
      this.tower.context()?.scenario.scenario.layoutVersionId ===
        scenario.scenario.layoutVersionId &&
      !!this.tower.context()?.scenario.inputs.events.some((row) => row.id === this.savedEventId())
    );
  });
  readonly evaluating = computed(() =>
    ['pending', 'running'].includes(this.evaluation()?.batch.batch.status ?? ''),
  );
  readonly locked = computed(
    () =>
      this.eventBusy() ||
      this.childBusy() ||
      this.tower.busy() ||
      this.evaluating() ||
      activeRun(this.tower.status()),
  );
  readonly evaluationStage = computed<EvaluationStage>(() =>
    ['compare', 'review'].includes(this.stage()) ? (this.stage() as EvaluationStage) : 'optimize',
  );
  readonly canCompare = computed(() => {
    const value = this.evaluation(),
      scenario = this.tower.selected();
    return (
      !!value &&
      !!scenario &&
      (this.historyOnly() || this.evaluationSourceRunId() === this.tower.run()?.id) &&
      !this.evaluating() &&
      (this.approved() ||
        !(this.historyOnly()
          ? evaluationIdentityProblem(value, scenario)
          : evaluationProblem(value, scenario)))
    );
  });
  readonly stageIndex = computed(() => WORKFLOW_STEPS.findIndex((row) => row.id === this.stage()));
  /** The surge being drafted, else the saved one this flow is about. */
  readonly surge = computed(
    () => this.draft() ?? this.events().find((row) => row.id === this.savedEventId()) ?? null,
  );
  readonly timeline = computed(() => {
    const scenario = this.tower.selected(),
      layout = this.tower.selectedLayout();
    return scenario && layout ? flightTimeline(scenario.inputs, layout, this.surge()) : null;
  });
  readonly runProgress = computed(() => {
    const duration = this.tower.context()?.scenario.inputs.durationMinutes,
      minute = this.tower.minute();
    return duration && minute !== null && minute !== undefined
      ? Math.min(100, Math.max(0, (minute / duration) * 100))
      : 0;
  });
  readonly summary = computed(() => {
    const value = this.evaluation();
    return value && !this.evaluating() ? evaluationSummary(value) : null;
  });
  readonly shiftPlan = computed(() => {
    const value = this.evaluation(),
      scenario = this.tower.selected(),
      staff = this.tower.selectedLayout()?.layout.staff ?? [];
    return value && scenario && !this.evaluating()
      ? shiftPlanView(value, scenario.inputs, new Map(staff.map((m) => [m.id, m.displayName])))
      : null;
  });
  readonly reviewProblem = computed(() => {
    const value = this.evaluation(),
      scenario = this.tower.selected();
    return value && scenario
      ? approvalProblem(value, scenario)
      : 'Sunt necesare optimizarea și finalizarea simulărilor asociate.';
  });

  constructor() {
    let previousStage = this.stage();
    effect(() => {
      const stage = this.stage();
      if (stage === previousStage) return;
      previousStage = stage;
      afterNextRender(
        () => {
          if (this.stage() === stage)
            this.element.nativeElement.querySelector<HTMLElement>('[data-workflow-title]')?.focus();
        },
        { injector: this.injector },
      );
    });
    this.destroy.onDestroy(() => this.eventRequest?.unsubscribe());
    this.tower.accepted.pipe(takeUntilDestroyed()).subscribe((value) => {
      const params = this.route.snapshot.queryParamMap;
      if (params.get('scenarioId') === value.scenarioId && params.get('runId') === value.runId)
        return;
      void this.router.navigate([], {
        relativeTo: this.route,
        queryParams: value,
        queryParamsHandling: 'merge',
        replaceUrl: true,
      });
    });
    this.route.queryParamMap
      .pipe(takeUntilDestroyed())
      .subscribe((params) => this.tower.open(params.get('scenarioId'), params.get('runId')));
    effect(() => {
      const scenario = this.tower.selected();
      if (!scenario) return;
      untracked(() => {
        if (this.contextId !== scenario.scenario.id) {
          this.eventRequest?.unsubscribe();
          this.eventCommand = null;
          this.eventUncertain.set(false);
          this.contextId = scenario.scenario.id;
          this.draft.set(null);
          this.resetEvaluation();
          const restoredId = this.route.snapshot.queryParamMap.get('eventId');
          this.savedEventId.set(
            scenario.inputs.events.some((row) => row.id === restoredId) ? restoredId : null,
          );
          this.stage.set(this.savedEventId() ? 'simulate' : 'event');
          this.message.set('');
        }
      });
    });
  }
  allowed(stage: WorkflowStage) {
    if (stage === 'event') return !!this.tower.selected();
    if (stage === 'simulate') return this.hasEvent() && !this.draft() && !this.eventCommand;
    if (stage === 'optimize') return this.runReady() && !this.approved();
    if (stage === 'compare') return (this.runReady() || this.historyOnly()) && this.canCompare();
    return (
      this.approved() ||
      (this.historyOnly() &&
        this.canCompare() &&
        this.comparisonRead() === this.evaluation()?.batch.batch.id) ||
      (this.runReady() &&
        !this.reviewProblem() &&
        this.comparisonRead() === this.evaluation()?.batch.batch.id)
    );
  }
  go(stage: WorkflowStage) {
    if (this.allowed(stage) && !this.eventBusy() && !this.childBusy() && !this.tower.busy()) {
      // A note like "Incident salvat" belongs to the step it came from.
      if (stage !== this.stage()) this.message.set('');
      this.stage.set(stage);
      if (stage === 'optimize') this.historyOnly.set(false);
    }
  }
  selectScenario(event: Event) {
    if (this.locked() || !this.canLeave()) return;
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { scenarioId: (event.target as HTMLSelectElement).value },
    });
  }
  selectRun(event: Event) {
    if (this.locked()) return;
    this.resetEvaluation();
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { runId: (event.target as HTMLSelectElement).value },
      queryParamsHandling: 'merge',
    });
  }
  addEvent() {
    const scenario = this.tower.selected(),
      layout = this.tower.selectedLayout();
    if (!scenario || !layout || this.locked() || this.eventCommand) return;
    this.draft.set(newSurge(scenario.inputs, layout));
    this.message.set('');
    this.resetEvaluation();
  }
  /** Picking a flight on the timeline starts or retargets the draft surge. */
  pickFlight(flightId: string) {
    const scenario = this.tower.selected(),
      layout = this.tower.selectedLayout();
    if (!scenario || !layout || this.locked() || this.eventUncertain() || this.eventCommand) return;
    if (!this.draft()) this.addEvent();
    const event = this.draft(),
      flight = scenario.inputs.flights.find((row) => row.id === flightId);
    if (!event || !flight) return;
    const duration = surgePayload(event)?.durationMinutes ?? 5;
    const start =
      Date.parse(scenario.inputs.startsAtUtc) +
      surgeStartMinute(scenario.inputs, flight.id, duration) * 60000;
    this.draft.set({
      ...event,
      flightId: flight.id,
      gateId: flight.gateId,
      checkpointId: incidentCheckpoint(scenario.inputs, layout, flight.id),
      occurredAtUtc: Number.isFinite(start) ? new Date(start).toISOString() : event.occurredAtUtc,
    });
  }
  describe(event: ScenarioEvent) {
    const scenario = this.tower.selected();
    return scenario ? describeSurge(event, scenario.inputs) : event.id;
  }
  useEvent(id: string) {
    if (
      this.locked() ||
      this.draft() ||
      this.eventCommand ||
      !this.events().some((row) => row.id === id)
    )
      return;
    this.savedEventId.set(id);
    this.resetEvaluation();
    this.stage.set('simulate');
    this.rememberEvent(id);
  }
  saveEvent() {
    const scenario = this.tower.selected(),
      event = this.draft();
    if (
      !this.tower.online() ||
      !scenario ||
      !event ||
      !surgePayload(event) ||
      this.draftProblems().length ||
      this.locked() ||
      this.eventCommand
    )
      return;
    this.eventCommand = {
      scenarioId: scenario.scenario.id,
      revision: scenario.scenario.revision,
      event: structuredClone(event),
    };
    this.writeEvent();
  }
  private writeEvent() {
    const command = this.eventCommand;
    if (!command || this.eventBusy()) return;
    this.eventBusy.set(true);
    this.message.set('');
    this.eventRequest = this.api
      .post<ScenarioDetails>(`scenarios/${command.scenarioId}/events`, {
        revision: command.revision,
        event: command.event,
      })
      .pipe(
        timeout(OPERATION_REQUEST_TIMEOUT_MS),
        finalize(() => this.eventBusy.set(false)),
      )
      .subscribe({
        next: (scenario) => this.acceptEvent(scenario, command),
        error: (error) => {
          if (error instanceof ApiError && [400, 404, 422].includes(error.status)) {
            this.eventCommand = null;
            this.eventUncertain.set(false);
          } else this.eventUncertain.set(true);
          const validation =
            error instanceof ApiError ? Object.values(error.fieldErrors).flat().join(' ') : '';
          this.message.set(validation || operationError(error));
        },
      });
  }
  reconcileEvent() {
    const command = this.eventCommand;
    if (!command || this.eventBusy()) return;
    this.eventBusy.set(true);
    this.message.set('');
    this.eventRequest = this.api
      .get<ScenarioDetails>(`scenarios/${command.scenarioId}`)
      .pipe(
        timeout(OPERATION_REQUEST_TIMEOUT_MS),
        switchMap((scenario) => {
          if (scenario.scenario.id !== command.scenarioId)
            throw new Error('Scenariul primit nu corespunde comenzii.');
          if (scenario.inputs.events.some((row) => row.id === command.event.id))
            return of(scenario);
          else if (scenario.scenario.revision === command.revision) {
            return this.api
              .post<ScenarioDetails>(`scenarios/${command.scenarioId}/events`, {
                revision: command.revision,
                event: command.event,
              })
              .pipe(timeout(OPERATION_REQUEST_TIMEOUT_MS));
          } else {
            this.eventCommand = null;
            this.eventUncertain.set(false);
            throw new Error(
              'Scenariul s-a modificat, iar incidentul nu este salvat. Actualizează contextul și verifică din nou formularul.',
            );
          }
        }),
        finalize(() => this.eventBusy.set(false)),
      )
      .subscribe({
        next: (scenario) => this.acceptEvent(scenario, command),
        error: (error) => this.message.set(operationError(error)),
      });
  }
  private acceptEvent(scenario: ScenarioDetails, command: EventCommand) {
    const saved = scenario.inputs.events.find((row) => row.id === command.event.id);
    const a = saved ? surgePayload(saved) : null,
      b = surgePayload(command.event);
    if (
      scenario.scenario.id !== command.scenarioId ||
      !saved ||
      !a ||
      !b ||
      saved.type !== command.event.type ||
      Date.parse(saved.occurredAtUtc) !== Date.parse(command.event.occurredAtUtc) ||
      saved.flightId !== command.event.flightId ||
      saved.checkpointId !== command.event.checkpointId ||
      saved.gateId !== command.event.gateId ||
      a.durationMinutes !== b.durationMinutes ||
      a.share !== b.share
    ) {
      this.eventUncertain.set(true);
      this.message.set(
        'Confirmarea incidentului nu corespunde parametrilor transmiși. Este necesară reverificarea comenzii.',
      );
      return;
    }
    this.savedEventId.set(saved.id);
    this.eventCommand = null;
    this.eventUncertain.set(false);
    this.draft.set(null);
    this.resetEvaluation();
    this.stage.set('simulate');
    this.message.set(
      `Incident salvat · revizia ${scenario.scenario.revision}. Următoarea simulare va captura această configurație.`,
    );
    this.rememberEvent(saved.id);
    this.tower.reload();
  }
  private rememberEvent(id: string) {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { eventId: id },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }
  simulate() {
    if (this.allowed('simulate') && !this.locked()) {
      this.resetEvaluation();
      this.tower.launch();
    }
  }
  evaluated(value: StaffingEvaluationDetails | null) {
    this.evaluationSourceRunId.set(value ? (this.tower.run()?.id ?? null) : null);
    this.evaluation.set(value);
    if (this.comparisonRead() !== value?.batch.batch.id) this.comparisonRead.set(null);
  }
  confirmComparison() {
    if (!this.canCompare() || (!this.historyOnly() && this.reviewProblem()) || this.childBusy())
      return;
    this.comparisonRead.set(this.evaluation()!.batch.batch.id);
    this.message.set('');
    this.stage.set('review');
  }
  approvedScenario(value: ScenarioDetails) {
    if (value.scenario.id !== this.tower.selected()?.scenario.id) return;
    this.approved.set(true);
    this.tower.selected.set(value);
    this.tower.scenarios.update((rows) =>
      rows.map((row) => (row.id === value.scenario.id ? value.scenario : row)),
    );
    this.message.set(
      'Aprobare confirmată și auditată. Configurația nouă are altă revizie; rulările afișate rămân istorice.',
    );
  }
  canLeave() {
    return (
      (!this.draft() && !this.eventCommand && !this.childBusy() && !this.evaluating()) ||
      window.confirm(
        'Există modificări nesalvate sau o operație în curs. Confirmați părăsirea fluxului. Operațiile acceptate pot continua pe server.',
      )
    );
  }
  openHistory() {
    if (this.locked() || this.draft() || this.eventCommand || !this.tower.selected()) return;
    this.historyOnly.set(true);
    this.stage.set('compare');
    this.comparisonRead.set(null);
  }
  private resetEvaluation() {
    this.historyOnly.set(false);
    this.evaluation.set(null);
    this.comparisonRead.set(null);
    this.approved.set(false);
    this.evaluationSourceRunId.set(null);
  }
}

import { SimulationJourney } from '../../shared/ui/simulation-journey';
import { SimulationContext } from '../../core/simulation-context';
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  EMPTY,
  Subject,
  Subscription,
  catchError,
  exhaustMap,
  finalize,
  map,
  switchMap,
  takeWhile,
  timer,
  timeout,
} from 'rxjs';
import { ApiError } from '../../core/http/api-error.interceptor';
import { OPERATION_REQUEST_TIMEOUT_MS, operationError } from '../../core/http/operation-request';
import { validRunId } from '../operational-report/report.models';
import { ApiClient } from '../../core/http/api-client';
import { createDataResource } from '../../shared/data-resource';
import { DataState } from '../../shared/ui/data-state';
import { BatchDetails, BatchSummary } from '../batches/batches.models';
import { RunComparison } from '../operations/comparison.models';
import { ScenarioDetails, ScenarioSummary } from '../operations/operations.models';
import { RunComparisonReport } from '../operations/run-comparison-report';
import { StudioLayout } from '../scenario-studio/studio.models';

@Component({
  selector: 'app-compare-workbench',
  imports: [FormsModule, RouterLink, DataState, RunComparisonReport, SimulationJourney],
  templateUrl: './compare-workbench.html',
  styleUrl: './compare-workbench.css',
})
export class CompareWorkbench {
  private readonly simulation = inject(SimulationContext);
  private readonly api = inject(ApiClient);
  private readonly destroy = inject(DestroyRef);
  private readonly watch = new Subject<string | null>();
  private comparisonRequest?: Subscription;
  private comparedBatch: string | null = null;
  private command: {
    key: string;
    id: string;
    scenarioId: string;
    body: {
      commandId: string;
      revision: number;
      name: string;
      checkpointId: string;
      staffCount: number;
      timeLimitSeconds: number;
    };
  } | null = null;
  readonly pendingCommand = signal(false);
  readonly scenarios = createDataResource(
    () => this.api.get<ScenarioSummary[]>('scenarios').pipe(timeout(OPERATION_REQUEST_TIMEOUT_MS)),
    (rows) => !rows.length,
  );
  readonly source = signal<{ scenario: ScenarioDetails; layout: StudioLayout } | null>(null);
  readonly detail = signal<BatchDetails | null>(null);
  readonly report = signal<RunComparison | null>(null);
  readonly busy = signal(false);
  readonly comparing = signal(false);
  readonly message = signal('');
  readonly pollingError = signal('');
  scenarioId = '';
  checkpointId = '';
  name = 'Comparație de personal';
  staffCount = 0;
  timeLimitSeconds = 60;

  constructor() {
    this.destroy.onDestroy(() => this.comparisonRequest?.unsubscribe());
    this.watch
      .pipe(
        switchMap((id) =>
          id === null
            ? EMPTY
            : timer(0, 1000).pipe(
                exhaustMap(() =>
                  this.api.get<BatchDetails>(`simulation-batches/${id}`).pipe(
                    timeout(OPERATION_REQUEST_TIMEOUT_MS),
                    map((value) => {
                      if (!this.belongs(value, id))
                        throw new Error(
                          'Manifestul primit nu aparține perechii și scenariului selectat.',
                        );
                      return value;
                    }),
                    catchError((error) => {
                      this.pollingError.set(
                        operationError(error) +
                          ' Se afișează ultima stare confirmată; reconectare automată…',
                      );
                      return EMPTY;
                    }),
                  ),
                ),
                takeWhile(
                  (value) =>
                    ['pending', 'running'].includes(value.batch.status) ||
                    value.replications.some((row) => row.remoteCancellationPending),
                  true,
                ),
              ),
        ),
        takeUntilDestroyed(this.destroy),
      )
      .subscribe((value) => {
        this.pollingError.set('');
        this.detail.set(value);
        if (
          !['pending', 'running'].includes(value.batch.status) &&
          this.paired(value) &&
          this.comparedBatch !== value.batch.id
        )
          this.compare();
      });
    const scenarioId = inject(ActivatedRoute).snapshot.queryParamMap.get('scenarioId');
    if (scenarioId) {
      this.scenarioId = scenarioId;
      this.select();
    }
  }
  select() {
    if (this.busy() || this.pendingCommand()) return;
    this.watch.next(null);
    this.comparisonRequest?.unsubscribe();
    this.comparing.set(false);
    this.source.set(null);
    this.detail.set(null);
    this.report.set(null);
    this.message.set('');
    this.pollingError.set('');
    this.command = null;
    this.comparedBatch = null;
    if (!this.scenarioId) return;
    const scenarioId = this.scenarioId;
    this.busy.set(true);
    this.api
      .get<ScenarioDetails>(`scenarios/${scenarioId}`)
      .pipe(
        switchMap((scenario) =>
          this.api
            .get<StudioLayout>(
              `scenes/${scenario.scenario.sceneId}/layout-versions/${scenario.scenario.layoutVersionId}`,
            )
            .pipe(map((layout) => ({ layout, scenario }))),
        ),
        timeout(OPERATION_REQUEST_TIMEOUT_MS),
        finalize(() => this.busy.set(false)),
        takeUntilDestroyed(this.destroy),
      )
      .subscribe({
        next: (value) => {
          if (
            value.scenario.scenario.id !== scenarioId ||
            value.layout.version.id !== value.scenario.scenario.layoutVersionId ||
            value.layout.layout.scene.id !== value.scenario.scenario.sceneId
          ) {
            this.message.set('Scenariul și layout-ul primite nu corespund selecției.');
            return;
          }
          this.source.set(value);
          this.simulation.remember(scenarioId);
          this.checkpointId =
            value.scenario.inputs.staffAllocations[0]?.checkpointId ??
            value.layout.layout.checkpoints[0]?.id ??
            '';
          this.staffCount =
            this.currentCount() < this.availableCount()
              ? this.currentCount() + 1
              : this.currentCount();
          this.restoreCommand();
        },
        error: (error) => this.error(error),
      });
  }
  status(value: string) {
    return (
      (
        {
          pending: 'În așteptare',
          planned: 'În așteptare',
          running: 'Se calculează',
          completed: 'Finalizat',
          completed_with_failures: 'Finalizat parțial',
          failed: 'Calcul nereușit',
          cancelled: 'Oprit',
          timed_out: 'Timp de calcul depășit',
        } as Record<string, string>
      )[value] ?? value
    );
  }
  zoneNames() {
    const layout = this.source()?.layout.layout;
    // Check-in desks are compared beside the controls when both runs simulated them.
    return Object.fromEntries(
      [...(layout?.checkpoints ?? []), ...(layout?.checkInAreas ?? [])].map((zone) => [
        zone.id,
        zone.name,
      ]),
    );
  }
  checkpointChanged() {
    this.staffCount =
      this.currentCount() < this.availableCount() ? this.currentCount() + 1 : this.currentCount();
  }
  /** The scenario's check-in desks, when it simulates them; they are a post like the controls. */
  checkInPost() {
    const source = this.source();
    const id = source?.scenario.inputs.checkIn?.areaId;
    return source?.layout.layout.checkInAreas?.find((area) => area.id === id) ?? null;
  }
  isCheckIn() {
    return !!this.checkpointId && this.checkInPost()?.id === this.checkpointId;
  }
  /** Names of the people at the chosen post, as the scenario's team calls them. */
  team() {
    const source = this.source();
    if (!source) return [];
    const operators = source.scenario.inputs.operators ?? [];
    if (this.isCheckIn())
      return operators.filter((person) => person.role === 'checkIn').map((person) => person.name);
    const names = new Map(operators.map((person) => [person.id, person.name]));
    const slots = new Map(
      source.layout.layout.staff.map((member) => [member.id, member.displayName]),
    );
    return source.scenario.inputs.staffAllocations
      .filter((row) => row.checkpointId === this.checkpointId)
      .map((row) => names.get(row.staffMemberId) ?? slots.get(row.staffMemberId) ?? 'Operator');
  }
  currentCount() {
    if (this.isCheckIn()) return this.source()?.scenario.inputs.checkIn?.staffCount ?? 0;
    return (
      this.source()?.scenario.inputs.staffAllocations.filter(
        (row) => row.checkpointId === this.checkpointId,
      ).length ?? 0
    );
  }
  availableCount() {
    const source = this.source();
    if (!source) return 0;
    if (this.isCheckIn()) return this.checkInPost()!.deskCount;
    const checkpoint = source.layout.layout.checkpoints.find((row) => row.id === this.checkpointId);
    const allocated = new Set(
      source.scenario.inputs.staffAllocations.map((row) => row.staffMemberId),
    );
    // A scenario with its own team adds only its unassigned security operators, not the layout's spare slots.
    const operators = source.scenario.inputs.operators;
    const team =
      operators &&
      new Set(operators.filter((person) => person.role === 'security').map((person) => person.id));
    return (
      this.currentCount() +
      source.layout.layout.staff.filter(
        (row) =>
          row.terminalId === checkpoint?.terminalId &&
          !allocated.has(row.id) &&
          (!team || team.has(row.id)),
      ).length
    );
  }
  create() {
    const source = this.source();
    if (!source || this.busy()) return;
    if (
      !(
        this.isCheckIn() ||
        source.layout.layout.checkpoints.some((row) => row.id === this.checkpointId)
      ) ||
      !Number.isInteger(this.staffCount) ||
      this.staffCount < (this.isCheckIn() ? 1 : 0) ||
      this.staffCount > this.availableCount() ||
      !this.name.trim() ||
      this.name.length > 160 ||
      !Number.isInteger(this.timeLimitSeconds) ||
      this.timeLimitSeconds < 1 ||
      this.timeLimitSeconds > 3600
    ) {
      this.message.set(
        `Alege un post și ${this.isCheckIn() ? 1 : 0}–${this.availableCount()} persoane, un nume de 1–160 caractere și o limită de 1–3600 secunde.`,
      );
      return;
    }
    const payload = {
      revision: source.scenario.scenario.revision,
      name: this.name.trim(),
      checkpointId: this.checkpointId,
      staffCount: this.staffCount,
      timeLimitSeconds: this.timeLimitSeconds,
    };
    const key = JSON.stringify({ scenarioId: source.scenario.scenario.id, ...payload });
    if (this.command && this.command.key !== key) {
      this.message.set(
        'O comandă are rezultatul neconfirmat. Reverifică aceeași comandă înainte să schimbi parametrii.',
      );
      return;
    }
    if (!this.command) {
      const id = crypto.randomUUID();
      this.command = {
        key,
        id,
        scenarioId: source.scenario.scenario.id,
        body: { commandId: id, ...payload },
      };
      this.persistCommand();
    }
    this.retry();
  }
  retry() {
    const command = this.command;
    if (!command || this.busy()) return;
    this.watch.next(null);
    this.comparisonRequest?.unsubscribe();
    this.report.set(null);
    this.detail.set(null);
    this.comparing.set(false);
    this.comparedBatch = null;
    this.busy.set(true);
    this.message.set('');
    this.api
      .post<BatchDetails>(`scenarios/${command.scenarioId}/what-if`, command.body)
      .pipe(
        timeout(OPERATION_REQUEST_TIMEOUT_MS),
        finalize(() => this.busy.set(false)),
        takeUntilDestroyed(this.destroy),
      )
      .subscribe({
        next: (value) => {
          if (
            !this.belongs(value, command.id) ||
            value.variants.find((row) => row.position === 0)?.scenarioRevision !==
              command.body.revision
          ) {
            this.message.set(
              'Confirmarea nu corespunde comenzii what-if. Reverifică aceeași comandă.',
            );
            return;
          }
          this.clearCommand();
          this.scenarios.reload();
          this.open(value.batch.id, value);
        },
        error: (error) => {
          if (error instanceof ApiError && [400, 404, 409, 422].includes(error.status))
            this.clearCommand();
          this.error(error);
        },
      });
  }
  open(id: string, initial: BatchDetails | null = null) {
    if ((this.busy() && initial === null) || this.pendingCommand()) return;
    if (initial && !this.belongs(initial, id)) {
      this.message.set('Perechea nu aparține scenariului selectat.');
      return;
    }
    this.comparisonRequest?.unsubscribe();
    this.comparing.set(false);
    this.report.set(null);
    this.detail.set(initial);
    this.comparedBatch = null;
    this.pollingError.set('');
    this.watch.next(id);
  }
  private belongs(value: BatchDetails, id: string) {
    if (
      !value ||
      value.batch?.id !== id ||
      !Array.isArray(value.variants) ||
      !Array.isArray(value.replications) ||
      !Array.isArray(value.seeds) ||
      value.variants.some((row) => !row || typeof row.id !== 'string') ||
      value.replications.some((row) => !row || typeof row.runId !== 'string')
    )
      return false;
    const baseline = value.variants.filter((row) => row.position === 0);
    return (
      value.batch.id === id &&
      baseline.length === 1 &&
      baseline[0].scenarioId === this.source()?.scenario.scenario.id
    );
  }
  paired(value: BatchDetails) {
    if (
      value.seeds.length !== 1 ||
      value.variants.length !== 2 ||
      value.replications.length !== 2 ||
      new Set(value.variants.map((row) => row.id)).size !== 2 ||
      new Set(value.replications.map((row) => row.runId)).size !== 2 ||
      ![0, 1].every((position) => value.variants.some((row) => row.position === position))
    )
      return false;
    return value.variants.every((variant) => {
      const matches = value.replications.filter((row) => row.variantId === variant.id);
      return (
        matches.length === 1 && matches[0].seedIndex === 0 && matches[0].seed === value.seeds[0]
      );
    });
  }
  roles() {
    const value = this.detail();
    if (!value || !this.paired(value)) return [];
    return [...value.variants]
      .sort((a, b) => a.position - b.position)
      .map((variant) => ({
        variant,
        role: variant.position === 0 ? 'Personalul actual' : 'Varianta nouă',
        replica: value.replications.find((row) => row.variantId === variant.id)!,
      }));
  }
  compare() {
    const value = this.detail(),
      roles = this.roles();
    if (
      !value ||
      roles.length !== 2 ||
      roles.some((row) => ['planned', 'pending', 'running'].includes(row.replica.runStatus))
    )
      return;
    const baseline = roles[0].replica.runId,
      candidate = roles[1].replica.runId,
      batchId = value.batch.id;
    this.comparisonRequest?.unsubscribe();
    this.comparing.set(true);
    this.report.set(null);
    this.comparedBatch = batchId;
    this.comparisonRequest = this.api
      .get<RunComparison>(`runs/${baseline}/compare/${candidate}`)
      .pipe(
        timeout(OPERATION_REQUEST_TIMEOUT_MS),
        finalize(() => this.comparing.set(false)),
        takeUntilDestroyed(this.destroy),
      )
      .subscribe({
        next: (report) => {
          if (
            this.detail()?.batch.id !== batchId ||
            report.baseline.id !== baseline ||
            report.candidate.id !== candidate
          ) {
            this.message.set('Raportul nu corespunde perechii selectate. Reîncarcă comparația.');
            return;
          }
          this.report.set(report);
          if (report.baseline.scenarioId === this.source()?.scenario.scenario.id)
            this.simulation.remember(report.baseline.scenarioId!, baseline);
        },
        error: (error) => this.error(error),
      });
  }
  cancel() {
    const value = this.detail();
    if (!value || this.busy() || !['pending', 'running'].includes(value.batch.status)) return;
    this.busy.set(true);
    this.api
      .post<BatchSummary>(`simulation-batches/${value.batch.id}/cancel`)
      .pipe(
        timeout(OPERATION_REQUEST_TIMEOUT_MS),
        finalize(() => this.busy.set(false)),
        takeUntilDestroyed(this.destroy),
      )
      .subscribe({
        next: () => this.watch.next(value.batch.id),
        error: (error) => this.error(error),
      });
  }
  private storageKey() {
    return `aerosynapse.what-if-command.v1.${this.source()?.scenario.scenario.id}`;
  }
  private persistCommand() {
    this.pendingCommand.set(true);
    try {
      localStorage.setItem(this.storageKey(), JSON.stringify(this.command));
    } catch {
      /* Optional persistence. */
    }
  }
  private clearCommand() {
    this.command = null;
    this.pendingCommand.set(false);
    try {
      localStorage.removeItem(this.storageKey());
    } catch {
      /* Optional persistence. */
    }
  }
  private restoreCommand() {
    try {
      const value = JSON.parse(localStorage.getItem(this.storageKey()) ?? 'null');
      if (
        !value ||
        !validRunId(value.id) ||
        value.body?.commandId !== value.id ||
        value.scenarioId !== this.source()?.scenario.scenario.id ||
        !Number.isSafeInteger(value.body.revision) ||
        value.body.revision < 1
      )
        return;
      const { commandId, ...parameters } = value.body;
      if (JSON.stringify({ scenarioId: value.scenarioId, ...parameters }) !== value.key) return;
      this.command = value;
      this.pendingCommand.set(true);
      this.message.set(
        'Comandă what-if restaurată. Reverifică aceeași comandă pentru a confirma perechea creată.',
      );
    } catch {
      /* Unavailable or corrupt storage must not block the page. */
    }
  }
  private error(error: unknown) {
    this.message.set(operationError(error));
  }
}

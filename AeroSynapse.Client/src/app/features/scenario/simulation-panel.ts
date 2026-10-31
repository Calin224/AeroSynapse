import {
  Component,
  DestroyRef,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { catchError, EMPTY, exhaustMap, of, Subscription, timer, timeout } from 'rxjs';
import { ApiClient } from '../../core/http/api-client';
import { SimulationContext } from '../../core/simulation-context';
import { OPERATION_REQUEST_TIMEOUT_MS, operationError } from '../../core/http/operation-request';
import { validRunId } from '../operational-report/report.models';
import { SimulationExecution, simulationReceiptProblem } from '../operations/simulation-command';
export type { SimulationExecution } from '../operations/simulation-command';
import { ApiError } from '../../core/http/api-error.interceptor';
import { createDataResource } from '../../shared/data-resource';
import { OperationsStream } from '../../core/http/operations-stream';
import { TERMINAL_MAP_ZONES } from '../digital-twin/terminal-zones';

interface SavedScenario {
  id: string;
  name: string;
  revision: number;
}
interface SavedCommand {
  scenarioId: string;
  scenarioRevision: number;
  key: string;
  correlationId: string;
}
interface RunMetric {
  id: string;
  name: string;
  value: number;
  unit: string;
  zoneId: string;
}

const KEY_METRICS: Record<string, string> = {
  processedPassengers: 'Pasageri procesați',
  meanWaitMinutes: 'Așteptare medie',
  p95WaitMinutes: 'Așteptare pentru 95% dintre pasageri',
  maximumQueue: 'Coadă maximă',
};
const KEY_ORDER = Object.keys(KEY_METRICS);
const UNITS: Record<string, string> = {
  minutes: 'min',
  passengers: 'pasageri',
  people: 'pasageri',
};
/** The engine has emitted both snake_case and camelCase metric names. */
const metricKey = (name: string) =>
  name.replace(/_([a-z])/g, (_, letter: string) => letter.toUpperCase());

@Component({
  selector: 'app-simulation-panel',
  providers: [OperationsStream],
  // Headless launcher: the Control Tower deck renders its state through viewChild.
  template: '',
})
export class SimulationPanel {
  private readonly simulation = inject(SimulationContext);
  private readonly api = inject(ApiClient);
  private readonly destroyRef = inject(DestroyRef);
  readonly live = inject(OperationsStream);
  private poll?: Subscription;
  private receiptRequest?: Subscription;
  private metricsRequest?: Subscription;
  private readonly storageKey = 'aerosynapse.simulation-command.v1';
  readonly scenarios = createDataResource(
    () => this.api.get<SavedScenario[]>('scenarios'),
    (items) => items.length === 0,
  );
  readonly selectedId = signal('');
  protected chosenScenario(): SavedScenario | undefined {
    const provided = this.scenario();
    if (provided?.id === this.selectedId()) return provided;
    const state = this.scenarios.state();
    return state.status === 'ready'
      ? state.data.find((item) => item.id === this.selectedId())
      : undefined;
  }
  /** Optional selection owned by the host page; ignored while a simulation is in flight. */
  readonly scenarioId = input<string | null>(null);
  /** Prevent new launches while the workspace loads or observes another active run. */
  readonly disabled = input(false);
  readonly scenario = input<SavedScenario | null>(null);
  /** Emits the run id once a launched simulation completes with valid results. */
  readonly finished = output<string>();
  /** A validated receipt lets the workspace observe this run while it is still running. */
  readonly accepted = output<SimulationExecution>();
  readonly scenarioSelected = output<string>();
  readonly working = output<boolean>();
  private announced = '';
  readonly command = signal<SavedCommand | null>(null);
  readonly execution = signal<SimulationExecution | null>(null);
  readonly metrics = signal<RunMetric[]>([]);
  readonly submitting = signal(false);
  readonly error = signal('');

  constructor() {
    effect(() => {
      const id = this.scenarioId();
      const active = this.active();
      untracked(() => {
        this.working.emit(active);
        if (active && this.command()) this.scenarioSelected.emit(this.command()!.scenarioId);
        else if (id && this.selectedId() !== id) this.changeSelection(id);
      });
    });
    this.live.updates.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((state) => {
      const execution = this.execution();
      const current = state.run;
      if (
        !execution ||
        current?.runId !== execution.runId ||
        state.scenarioId !== execution.scenarioId
      )
        return;
      if (current.status === 'running' && execution.status === 'pending')
        this.execution.set({ ...execution, status: 'running' });
      if (current.status !== execution.status && ['completed', 'failed'].includes(current.status))
        this.readCommand(execution.commandId);
    });
    try {
      const saved: unknown = JSON.parse(localStorage.getItem(this.storageKey) ?? 'null');
      if (
        saved &&
        typeof saved === 'object' &&
        'key' in saved &&
        'scenarioId' in saved &&
        'scenarioRevision' in saved &&
        'correlationId' in saved &&
        typeof saved.key === 'string' &&
        typeof saved.scenarioId === 'string' &&
        typeof saved.correlationId === 'string' &&
        validRunId(saved.key) &&
        validRunId(saved.scenarioId) &&
        validRunId(saved.correlationId) &&
        Number.isSafeInteger(saved.scenarioRevision) &&
        Number(saved.scenarioRevision) > 0
      ) {
        this.command.set(saved as SavedCommand);
        this.selectedId.set((saved as SavedCommand).scenarioId);
        this.live.connect((saved as SavedCommand).scenarioId);
        this.watch((saved as SavedCommand).key);
      }
    } catch {
      /* Storage can be disabled by the browser. */
    }
  }

  active() {
    return (
      this.submitting() ||
      (this.command() !== null &&
        (!this.execution() || ['pending', 'running'].includes(this.execution()!.status)))
    );
  }
  select(id: string) {
    if (this.active()) return;
    this.changeSelection(id);
    this.scenarioSelected.emit(id);
  }
  private changeSelection(id: string) {
    this.poll?.unsubscribe();
    this.receiptRequest?.unsubscribe();
    this.metricsRequest?.unsubscribe();
    this.readingCommand = false;
    this.selectedId.set(id);
    this.execution.set(null);
    this.command.set(null);
    this.metrics.set([]);
    this.error.set('');
    this.live.connect(null);
  }

  start() {
    if (this.disabled() || this.active()) return;
    const scenario = this.chosenScenario();
    if (!scenario) return;
    this.metricsRequest?.unsubscribe();
    const command = {
      scenarioId: scenario.id,
      scenarioRevision: scenario.revision,
      key: crypto.randomUUID(),
      correlationId: crypto.randomUUID(),
    };
    this.command.set(command);
    this.live.connect(command.scenarioId);
    this.execution.set(null);
    this.metrics.set([]);
    try {
      localStorage.setItem(this.storageKey, JSON.stringify(command));
    } catch {
      /* Optional persistence. */
    }
    this.resume();
  }

  resume() {
    const command = this.command();
    if (!command || this.submitting()) return;
    this.poll?.unsubscribe();
    this.receiptRequest?.unsubscribe();
    this.readingCommand = false;
    this.error.set('');
    this.submitting.set(true);
    this.api
      .post<SimulationExecution>(
        `scenarios/${command.scenarioId}/simulations`,
        { scenarioRevision: command.scenarioRevision },
        { 'Idempotency-Key': command.key, 'X-Correlation-ID': command.correlationId },
      )
      .pipe(timeout(OPERATION_REQUEST_TIMEOUT_MS), takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (run) => {
          this.submitting.set(false);
          if (this.receive(run) && ['pending', 'running'].includes(run.status))
            this.watch(command.key);
        },
        error: (error: unknown) => {
          this.submitting.set(false);
          if (error instanceof ApiError && [400, 404, 409, 422].includes(error.status)) {
            this.command.set(null);
            try {
              localStorage.removeItem(this.storageKey);
            } catch {
              /* Optional persistence. */
            }
            this.error.set(
              'Comanda a fost respinsă. Actualizează scenariile și selectează un scenariu valid.',
            );
            this.scenarios.reload();
            return;
          }
          this.error.set(
            operationError(error) + ' Verificăm aceeași comandă; poți relua verificarea.',
          );
          this.watch(command.key);
        },
      });
  }

  private watch(key: string) {
    this.poll?.unsubscribe();
    this.poll = timer(0, 2000)
      .pipe(
        exhaustMap(() =>
          this.readingCommand
            ? EMPTY
            : this.api.get<SimulationExecution>(`simulations/${key}`).pipe(
                timeout(OPERATION_REQUEST_TIMEOUT_MS),
                catchError((error) => {
                  // An unknown key was never accepted; the idempotent re-send launches or rejects it.
                  if (error instanceof ApiError && error.status === 404) {
                    this.resume();
                    return EMPTY;
                  }
                  this.error.set(
                    operationError(error) + ' Comanda este păstrată pentru reconectare.',
                  );
                  return of(null);
                }),
              ),
        ),
        takeUntilDestroyed(this.destroyRef),
      )
      .subscribe((run) => {
        if (run) this.receive(run);
      });
  }

  private receive(run: SimulationExecution) {
    const command = this.command();
    if (!command) return false;
    const problem = simulationReceiptProblem(run, command, this.execution()?.runId);
    if (problem) {
      this.error.set(problem);
      return false;
    }
    const current = this.execution();
    // REST and SignalR can arrive out of order; a terminal receipt remains authoritative.
    if (
      current &&
      ['completed', 'failed'].includes(current.status) &&
      run.status !== current.status
    )
      return false;
    if (current?.status === 'running' && run.status === 'pending') return true;
    this.error.set('');
    this.execution.set(run);
    this.simulation.remember(run.scenarioId, run.runId);
    this.live.trackRun(run.runId);
    if (current?.runId !== run.runId) this.accepted.emit(run);
    if (run.status === 'completed' || run.status === 'failed') {
      this.poll?.unsubscribe();
      try {
        localStorage.removeItem(this.storageKey);
      } catch {
        /* Optional persistence. */
      }
      if (run.resultsValid) this.loadMetrics(run.runId);
      if (run.status === 'completed' && run.resultsValid && this.announced !== run.runId) {
        this.announced = run.runId;
        this.finished.emit(run.runId);
      }
    }
    return true;
  }

  private readingCommand = false;
  private readCommand(key: string) {
    if (this.readingCommand) return;
    this.readingCommand = true;
    this.receiptRequest = this.api
      .get<SimulationExecution>(`simulations/${key}`)
      .pipe(timeout(OPERATION_REQUEST_TIMEOUT_MS), takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (run) => {
          this.readingCommand = false;
          this.receive(run);
        },
        error: () => {
          this.readingCommand = false;
          this.error.set('Rezultatul final va fi verificat din nou la următoarea actualizare.');
        },
      });
  }

  private loadMetrics(runId: string) {
    this.metricsRequest?.unsubscribe();
    this.metricsRequest = this.api
      .get<{ items: RunMetric[] }>(`runs/${runId}/metrics?limit=100`)
      .pipe(timeout(OPERATION_REQUEST_TIMEOUT_MS), takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (result) => this.metrics.set(result.items),
        error: () =>
          this.error.set(
            'Simularea este finalizată, dar rezultatele nu pot fi încărcate momentan.',
          ),
      });
  }
  protected readonly zoneNames = Object.fromEntries(
    TERMINAL_MAP_ZONES.filter(
      (zone) => zone.zoneType === 'checkpoint' || zone.zoneType === 'checkIn',
    ).map((zone) => [zone.zoneId, zone.code]),
  );
  /** The handful of outcomes a newcomer needs, translated; the full metric set stays in reports. */
  protected keyMetrics(): { id: string; label: string; value: string }[] {
    return this.metrics()
      .map((metric) => ({ metric, key: metricKey(metric.name) }))
      .filter(({ key }) => key in KEY_METRICS)
      .sort(
        (a, b) =>
          KEY_ORDER.indexOf(a.key) - KEY_ORDER.indexOf(b.key) ||
          (this.zoneNames[a.metric.zoneId] ?? '').localeCompare(
            this.zoneNames[b.metric.zoneId] ?? '',
          ),
      )
      .map(({ metric, key }) => {
        const zone = this.zoneNames[metric.zoneId];
        return {
          id: metric.id,
          label: KEY_METRICS[key] + (zone ? ` · ${zone}` : ''),
          value: `${Math.round(metric.value * 10) / 10} ${UNITS[metric.unit] ?? metric.unit}`,
        };
      });
  }
}

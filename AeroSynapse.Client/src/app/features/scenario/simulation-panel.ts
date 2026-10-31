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
import { DataState } from '../../shared/ui/data-state';
import { OperationsStream } from '../../core/http/operations-stream';
import { OperationsProgress } from '../../shared/ui/operations-progress';
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
  imports: [DataState, OperationsProgress],
  providers: [OperationsStream],
  template: `
    <section class="panel simulation-panel">
      <header class="panel-heading">
        <div>
          <h2 id="simulation-heading" tabindex="-1">Rulează o simulare</h2>
        </div>
      </header>
      <div class="panel-body">
        @let state = scenarios.state();
        @if (state.status === 'ready') {
          @let chosen = chosenScenario();
          <div class="launcher">
            <!-- The scenario list on the page is the only picker; this panel runs that choice. -->
            <p id="simulation-scenario" [attr.data-scenario-id]="selectedId()">
              @if (chosen) {
                Scenariu: <strong>{{ chosen.name }}</strong> · revizia {{ chosen.revision }}
              } @else {
                Alege un scenariu din listă.
              }
            </p>
            <button class="btn-primary" [disabled]="!selectedId() || active()" (click)="start()">
              Pornește simularea
            </button>
          </div>
        } @else {
          <app-data-state
            [status]="state.status"
            label="scenariile salvate"
            [message]="state.status === 'error' ? state.message : ''"
            emptyMessage="Nu există scenarii salvate pentru simulare."
            (retry)="scenarios.reload()"
          />
        }
        <div aria-live="polite">
          @if (execution(); as run) {
            <p>
              Stare: <strong>{{ statusLabel(run.status) }}</strong>
            </p>
            <details class="simulation-reference">
              <summary>Detalii tehnice</summary>
              Rulare {{ run.runId }} · Correlation ID {{ run.correlationId }}
            </details>
            <app-operations-progress
              [run]="live.state()?.run ?? null"
              [connection]="live.status()"
              [optimization]="live.state()?.optimization ?? null"
              [zoneNames]="zoneNames"
            />
            @if (run.failureReason) {
              <p class="notice" role="alert">{{ run.failureReason }}</p>
            }
            @if (run.resultsValid) {
              @for (metric of keyMetrics(); track metric.id) {
                <p>{{ metric.label }}: {{ metric.value }}</p>
              }
            }
          } @else {
            @if (submitting()) {
              <p>Stare: În așteptare</p>
            }
            <!-- Before any run there is nothing to follow; an idle hint would read as an error. -->
            @if (submitting() || live.status() !== 'idle') {
              <app-operations-progress
                [run]="live.state()?.run ?? null"
                [connection]="live.status()"
                [optimization]="live.state()?.optimization ?? null"
                [zoneNames]="zoneNames"
              />
            }
          }
          @if (error()) {
            <p class="notice" role="alert">{{ error() }}</p>
          }
        </div>
        @if (command() && error()) {
          <button [disabled]="submitting()" (click)="resume()">
            Continuă verificarea simulării
          </button>
        }
      </div>
    </section>
  `,
  styles: `
    :host {
      display: block;
    }
    .launcher {
      display: grid;
      gap: 8px;
    }
    .launcher p {
      margin: 0;
      font-size: 13px;
      color: var(--text-muted);
    }
    [aria-live] > * + * {
      margin-top: 10px;
    }
    strong {
      color: var(--text);
    }
    .simulation-reference,
    small {
      color: var(--text-muted);
      font-size: 12px;
      overflow-wrap: anywhere;
      font-family: var(--font-mono);
    }
  `,
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
    const state = this.scenarios.state();
    return state.status === 'ready'
      ? state.data.find((item) => item.id === this.selectedId())
      : undefined;
  }
  /** Optional selection owned by the host page; ignored while a simulation is in flight. */
  readonly scenarioId = input<string | null>(null);
  /** Emits the run id once a launched simulation completes with valid results. */
  readonly finished = output<string>();
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
    if (this.active()) return;
    const state = this.scenarios.state();
    const scenario =
      state.status === 'ready'
        ? state.data.find((item) => item.id === this.selectedId())
        : undefined;
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
  statusLabel(status: SimulationExecution['status']) {
    return {
      pending: 'În așteptare',
      running: 'În execuție',
      completed: 'Finalizată',
      failed: 'Eșuată',
    }[status];
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

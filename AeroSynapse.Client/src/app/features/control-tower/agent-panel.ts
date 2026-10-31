import { Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NgTemplateOutlet } from '@angular/common';
import { RouterLink } from '@angular/router';
import { finalize, of, switchMap, timer, catchError, Subject } from 'rxjs';
import { ApiClient } from '../../core/http/api-client';
import { WriteAccess } from '../../core/auth/write-access';
import { SessionState } from '../../core/auth/session-state';
import type { ScenarioAgent, ScenarioDetails } from '../operations/operations.models';

export interface AgentOption {
  id: string;
  kind: string;
  text: string;
  toCheckpointId?: string;
}
export interface AgentDecision {
  sequence: number;
  minute: number;
  point: string;
  kind: string;
  optionId: string;
  text: string;
  reason: string;
  modelStatus: string;
  model: string;
  options: AgentOption[];
}
export interface AgentImpactSide {
  meanWaitMinutes: number | null;
  maxP95WaitMinutes: number | null;
  maxQueue: number;
  queueClearedMinute: number | null;
}
/** What the agent changed in a finished run, against the same day simulated without its decisions. */
export interface AgentImpact {
  changes: string[];
  withoutAgent: AgentImpactSide;
  withAgent: AgentImpactSide;
}
export interface AgentStatus {
  modelConfigured: boolean;
  model: string;
}

const unanswered: Record<string, string> = {
  unconfigured: 'Model AI neconfigurat.',
  timeout: 'Modelul nu a răspuns la timp.',
  invalid_response: 'Răspunsul modelului nu a fost valid.',
  unavailable: 'Modelul AI nu a fost disponibil.',
  budget_exhausted: 'Limita de apeluri către model a fost atinsă.',
  quota_exhausted: 'Contul Gemini nu mai are credit sau a atins limita de cereri.',
  single_option: 'O singură variantă posibilă, aplicată fără model.',
};
const fallback = 'Regulă de rezervă activă.';

/** Reserve operators the agent may call in when nobody can be moved. */
export const DEFAULT_RESERVE_OPERATORS = 10;

/** The reserve the agent may call in; one that moves operators and was saved without it gets the default (as the API does). */
export function reserveOf(agent: ScenarioAgent) {
  return agent.reserveOperators ?? (agent.staffMoves ? DEFAULT_RESERVE_OPERATORS : 0);
}

/** What the agent may do in a scenario, by default everything the scenario allows. */
export function defaultAgent(value: ScenarioDetails, controls: number): ScenarioAgent | null {
  const config: ScenarioAgent = {
    staffMoves: controls >= 2 && !value.inputs.staffShifts,
    checkInDesks: !!value.inputs.checkIn,
    gateChoices: !!value.inputs.journey,
    // Often enough to catch a surge before its queue peaks; operators still need transferMinutes to arrive.
    decisionIntervalMinutes: 5,
    transferMinutes: 5,
    reserveOperators: value.inputs.staffShifts ? 0 : DEFAULT_RESERVE_OPERATORS,
  };
  return config.staffMoves || config.checkInDesks || config.gateChoices || config.reserveOperators
    ? config
    : null;
}

/** The live AI agent: turn it on for the scenario and follow its decisions while the run advances. */
@Component({
  selector: 'app-agent-panel',
  imports: [WriteAccess, RouterLink, NgTemplateOutlet],
  template: `
    <section class="panel agent" aria-labelledby="agent-title">
      <header class="panel-title">
        <h2 id="agent-title">Agent AI</h2>
        <span>{{ decisions().length }} decizii</span>
      </header>
      <div class="agent-meta">
        <label class="agent-switch" appWriteAccess>
          <input
            type="checkbox"
            [checked]="!!scenario()?.inputs?.agent"
            [disabled]="saving() || !scenario() || running()"
            (change)="toggle($event)"
          />
          Activ
        </label>
        @if (scenario()?.inputs?.agent; as agent) {
          <p class="agent-scope">
            {{ scope(agent) }} ·
            <label class="agent-interval"
              >la fiecare
              <select
                [disabled]="saving() || running() || !canWrite()"
                (change)="every($event, agent)"
              >
                @for (minutes of intervals(agent); track minutes) {
                  <option [value]="minutes" [selected]="minutes === agent.decisionIntervalMinutes">
                    {{ minutes }}
                  </option>
                }
              </select>
              min simulate</label
            >
          </p>
          @if (status() && !status()!.modelConfigured) {
            <p class="agent-warning" role="status">
              Model AI neconfigurat. {{ fallbackNote }}
            </p>
          } @else if (issue()) {
            <p class="agent-warning" role="status">{{ issue() }}</p>
          }
        }
        @if (message()) {
          <p class="agent-warning" role="alert">{{ message() }}</p>
        }
      </div>
      @if (impact(); as result) {
        <div class="agent-result" role="status" aria-label="Rezultatul agentului">
          <p class="verdict">{{ verdict(result) }}</p>
          <table>
            <caption>
              Aceeași zi și același seed, simulate și fără deciziile agentului
            </caption>
            <thead>
              <tr>
                <th scope="col"><span class="visually-hidden">Indicator</span></th>
                <th scope="col">Fără agent</th>
                <th scope="col">Cu agent</th>
              </tr>
            </thead>
            <tbody>
              <tr>
                <th scope="row">Așteptare medie</th>
                <td>{{ minutes(result.withoutAgent.meanWaitMinutes) }}</td>
                <td>{{ minutes(result.withAgent.meanWaitMinutes) }}</td>
              </tr>
              <tr>
                <th scope="row">P95 maxim</th>
                <td>{{ minutes(result.withoutAgent.maxP95WaitMinutes) }}</td>
                <td>{{ minutes(result.withAgent.maxP95WaitMinutes) }}</td>
              </tr>
              <tr>
                <th scope="row">Coada maximă</th>
                <td>{{ result.withoutAgent.maxQueue }}</td>
                <td>{{ result.withAgent.maxQueue }}</td>
              </tr>
              <tr>
                <th scope="row">Coada golită la</th>
                <td>{{ cleared(result.withoutAgent) }}</td>
                <td>{{ cleared(result.withAgent) }}</td>
              </tr>
            </tbody>
          </table>
        </div>
      } @else if (impactLoading()) {
        <p class="agent-result empty" role="status">Se compară cu aceeași zi fără agent…</p>
      }
      <div class="panel-scroll" tabindex="0" aria-live="polite" aria-label="Deciziile agentului">
        @for (decision of actions(); track decision.sequence) {
          <ng-container *ngTemplateOutlet="row; context: { $implicit: decision }" />
        }
        @if (quiet().length) {
          <details>
            <summary>{{ quiet().length }} verificări fără schimbare</summary>
            @for (decision of quiet(); track decision.sequence) {
              <ng-container *ngTemplateOutlet="row; context: { $implicit: decision }" />
            }
          </details>
        }
        @if (!decisions().length) {
          <p class="empty">
            {{
              scenario()?.inputs?.agent
                ? 'Nicio decizie.'
                : 'Agent inactiv.'
            }}
          </p>
        }
      </div>
      <ng-template #row let-decision>
        <article
          [attr.data-kind]="decision.kind"
          [attr.title]="decision.reason ? null : explain(decision.modelStatus) || null"
        >
          <div>
            <strong>{{ clock(decision.minute) }} · {{ decision.text }}</strong>
            <small>{{
              decision.kind === 'none' ? 'fără acțiune' : byRule(decision) ? 'regulă' : 'aplicat'
            }}</small>
          </div>
          @if (decision.reason || decision.modelStatus === 'single_option') {
            <p>{{ decision.reason || explain(decision.modelStatus) }}</p>
          }
          @if (onMap(decision); as place) {
            <a routerLink="." [queryParams]="place">Arată pe hartă la {{ clock(place.minute) }}</a>
          }
        </article>
      </ng-template>
    </section>
  `,
  styleUrl: './tower-panel.css',
  styles: `
    .agent-meta {
      display: grid;
      gap: 4px;
      padding: 10px 14px;
      border-bottom: 1px solid var(--border);
    }
    .agent-switch {
      display: flex;
      gap: 8px;
      align-items: center;
      font-size: 12px;
      font-weight: 600;
    }
    .agent-scope,
    .agent-warning {
      margin: 0;
      font-size: 12px;
      line-height: 1.4;
      color: var(--text-muted);
    }
    .agent-warning {
      color: var(--warning-text, #866025);
    }
    .agent-interval select {
      width: auto;
      padding: 0 4px;
      font-size: 12px;
    }
    .panel-scroll article[data-kind='none'] strong {
      font-weight: 500;
    }
    .panel-scroll article[data-kind='none'] small {
      color: var(--text-muted);
    }
    .panel-scroll article:not([data-kind='none']) small {
      color: var(--accent);
    }
    .visually-hidden {
      position: absolute;
      width: 1px;
      height: 1px;
      overflow: hidden;
      clip: rect(0 0 0 0);
      white-space: nowrap;
    }
    .agent-result {
      display: grid;
      gap: 6px;
      margin: 0;
      padding: 10px 14px;
      border-bottom: 1px solid var(--border);
    }
    .verdict {
      margin: 0;
      font-size: 13px;
      font-weight: 600;
      line-height: 1.4;
    }
    .agent-result table {
      width: 100%;
      border-collapse: collapse;
      font-size: 12px;
      font-variant-numeric: tabular-nums;
    }
    .agent-result caption {
      caption-side: bottom;
      padding-top: 4px;
      text-align: left;
      color: var(--text-muted);
      font-size: 11px;
    }
    .agent-result th,
    .agent-result td {
      padding: 3px 0;
      border-bottom: 1px solid var(--border);
      text-align: right;
    }
    .agent-result th[scope='row'] {
      text-align: left;
      font-weight: 500;
    }
    .agent-result td:last-child {
      font-weight: 600;
    }
    details summary {
      padding: 9px 0;
      font-size: 12px;
      color: var(--text-muted);
      cursor: pointer;
    }
    .panel-scroll article a {
      display: inline-block;
      margin-top: 3px;
      font-size: 12px;
    }
  `,
})
export class AgentPanel {
  private readonly api = inject(ApiClient);
  readonly canWrite = inject(SessionState).canWrite;
  readonly scenario = input<ScenarioDetails | null>(null);
  readonly controls = input(0);
  readonly runId = input<string | null>(null);
  /** The selected run was captured with the agent, so it has decisions to follow. */
  readonly tracked = input(false);
  readonly running = input(false);
  readonly changed = output<void>();
  readonly status = signal<AgentStatus | null>(null);
  readonly decisions = signal<AgentDecision[]>([]);
  readonly saving = signal(false);
  readonly message = signal('');
  readonly actions = computed(() => this.decisions().filter((d) => d.kind !== 'none'));
  readonly quiet = computed(() => this.decisions().filter((d) => d.kind === 'none'));
  readonly impact = signal<AgentImpact | null>(null);
  readonly impactLoading = signal(false);
  private impactFor: string | null = null;
  /** Why the model stayed silent at the latest decision, shown once instead of under every step. */
  readonly issue = computed(() => {
    const last = this.decisions().at(-1);
    if (!last || last.modelStatus === 'chosen' || last.modelStatus === 'single_option') return '';
    return `${this.explain(last.modelStatus)} ${fallback}`;
  });
  readonly fallbackNote = fallback;
  private readonly watch = new Subject<{ runId: string | null; running: boolean }>();
  private readonly clockFormat = computed(() => {
    const value = this.scenario();
    try {
      return new Intl.DateTimeFormat('ro-RO', {
        hour: '2-digit',
        minute: '2-digit',
        timeZone: value?.inputs.timeZone,
      });
    } catch {
      return new Intl.DateTimeFormat('ro-RO', {
        hour: '2-digit',
        minute: '2-digit',
        timeZone: 'UTC',
      });
    }
  });

  constructor() {
    // Nothing is requested until the agent is on for the scenario or the selected run.
    let asked = false;
    effect(() => {
      if (asked || !(this.scenario()?.inputs?.agent || this.tracked())) return;
      asked = true;
      this.api
        .get<AgentStatus>('agent/status')
        .pipe(catchError(() => of(null)))
        .subscribe((value) => this.status.set(value));
    });
    // Read the decisions once per run, and every two seconds while it runs.
    this.watch
      .pipe(
        switchMap(({ runId, running }) => {
          this.decisions.set([]);
          if (!runId) return of([] as AgentDecision[]);
          return (running ? timer(0, 2000) : timer(0)).pipe(
            switchMap(() =>
              this.api
                .get<AgentDecision[]>(`runs/${runId}/agent-decisions`)
                .pipe(catchError(() => of(null))),
            ),
          );
        }),
        takeUntilDestroyed(),
      )
      .subscribe((rows) => {
        if (rows) this.decisions.set(rows);
      });
    effect(() =>
      this.watch.next({ runId: this.tracked() ? this.runId() : null, running: this.running() }),
    );
    // Once a run the agent acted in has finished, compare it once with the same day without the agent.
    effect(() => {
      const runId = this.tracked() ? this.runId() : null;
      const ready = !!runId && !this.running() && this.actions().length > 0;
      if (runId !== this.impactFor) {
        this.impactFor = null;
        this.impact.set(null);
      }
      if (!ready || this.impactFor === runId) return;
      this.impactFor = runId;
      this.impactLoading.set(true);
      this.api
        .get<AgentImpact | null>(`runs/${runId}/agent-impact`)
        .pipe(
          catchError(() => of(null)),
          finalize(() => this.impactLoading.set(false)),
        )
        .subscribe((value) => {
          if (this.impactFor === runId) this.impact.set(value);
        });
    });
  }

  /** The result in one line: what the agent did, and what it did to the average wait. */
  verdict(result: AgentImpact) {
    const what = `Agentul: ${result.changes.join('; ')}.`;
    const before = result.withoutAgent.meanWaitMinutes,
      after = result.withAgent.meanWaitMinutes;
    if (before === null || after === null) return what;
    if (after < before)
      return `${what} Așteptarea medie a scăzut de la ${before.toFixed(1)} la ${after.toFixed(1)} min (−${Math.round((1 - after / before) * 100)}%).`;
    return `${what} Așteptarea medie nu a scăzut (${before.toFixed(1)} → ${after.toFixed(1)} min).`;
  }
  minutes(value: number | null) {
    return value === null ? '—' : `${value.toFixed(1)} min`;
  }
  cleared(side: AgentImpactSide) {
    if (side.maxQueue === 0) return '—';
    return side.queueClearedMinute === null ? 'nu se golește' : this.clock(side.queueClearedMinute);
  }

  clock(minute: number) {
    const start = this.scenario()?.inputs.startsAtUtc;
    return start
      ? this.clockFormat().format(new Date(Date.parse(start) + minute * 60000))
      : `min ${minute}`;
  }
  /** Where and when a staffing decision shows on the map: its control, once the operators serve there. */
  onMap(decision: AgentDecision) {
    const zoneId = decision.options.find(
      (option) => option.id === decision.optionId,
    )?.toCheckpointId;
    const scenarioId = this.scenario()?.scenario.id;
    if (!zoneId || !scenarioId) return null;
    const transfer = this.scenario()?.inputs.agent?.transferMinutes ?? 0;
    return { scenarioId, runId: this.runId(), zoneId, minute: decision.minute + 1 + transfer };
  }

  /** Taken by the deterministic rule because the model gave no answer. */
  byRule(decision: AgentDecision) {
    return decision.modelStatus !== 'chosen' && decision.modelStatus !== 'single_option';
  }
  explain(status: string) {
    return unanswered[status] ?? '';
  }
  scope(agent: ScenarioAgent) {
    return [
      agent.staffMoves ? 'mută operatori' : '',
      reserveOf(agent) ? `cheamă până la ${reserveOf(agent)} operatori din rezervă` : '',
      agent.checkInDesks ? 'deschide ghișee' : '',
      agent.gateChoices ? 'alege porțile la întârzieri' : '',
    ]
      .filter(Boolean)
      .join(', ');
  }

  /** The usual steps, plus the saved one if it is another. */
  intervals(agent: ScenarioAgent) {
    return [...new Set([5, 10, 15, 30, agent.decisionIntervalMinutes])].sort((a, b) => a - b);
  }

  toggle(event: Event) {
    const value = this.scenario();
    const box = event.target as HTMLInputElement;
    if (!value) return;
    const agent = box.checked ? defaultAgent(value, this.controls()) : null;
    if (box.checked && !agent) {
      box.checked = false;
      this.message.set(
        'Scenariul nu are ce să controleze agentul: e nevoie de personal fix (fără ture), de check-in sau de călătorii.',
      );
      return;
    }
    this.save(agent, () => (box.checked = !box.checked));
  }

  every(event: Event, agent: ScenarioAgent) {
    const select = event.target as HTMLSelectElement;
    this.save(
      { ...agent, decisionIntervalMinutes: Number(select.value) },
      () => (select.value = String(agent.decisionIntervalMinutes)),
    );
  }

  private save(agent: ScenarioAgent | null, undo: () => void) {
    const value = this.scenario();
    if (!value) return;
    const { code, name, description, sceneId, layoutVersionId, revision } = value.scenario;
    this.saving.set(true);
    this.message.set('');
    this.api
      .put<ScenarioDetails>(`scenarios/${value.scenario.id}`, {
        code,
        name,
        description,
        sceneId,
        layoutVersionId,
        revision,
        inputs: { ...value.inputs, agent },
      })
      .pipe(finalize(() => this.saving.set(false)))
      .subscribe({
        next: () => this.changed.emit(),
        error: (error: unknown) => {
          undo();
          this.message.set(
            error instanceof Error
              ? error.message
              : 'Setarea agentului nu a fost salvată. Reîncearcă.',
          );
        },
      });
  }
}

import {
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { catchError, forkJoin, map, of, Subscription } from 'rxjs';
import { ApiClient } from '../../core/http/api-client';
import type { RunSummary, ScenarioSummary } from '../../features/operations/operations.models';

type PickerRun = RunSummary & { startedAtUtc?: string | null; engineMilliseconds?: number };

export interface PickedRun {
  runId: string;
  scenarioId: string;
  scenarioName: string;
}

const RUNS_PER_SCENARIO = 8;
const relative = new Intl.RelativeTimeFormat('ro', { numeric: 'auto' });
export const statusLabels: Record<string, string> = {
  completed: 'Finalizată',
  failed: 'Eșuată',
  running: 'În curs',
  queued: 'În așteptare',
  pending: 'În așteptare',
  planned: 'Planificată',
  cancelled: 'Anulată',
};

/** "acum 5 minute", "ieri"… for an ISO timestamp; empty when the value is missing. */
export function fromNow(iso?: string | null, now = Date.now()) {
  const at = iso ? Date.parse(iso) : 0;
  if (!at) return '';
  const minutes = Math.round((at - now) / 60000);
  if (Math.abs(minutes) < 60) return relative.format(minutes, 'minute');
  const hours = Math.round(minutes / 60);
  if (Math.abs(hours) < 48) return relative.format(hours, 'hour');
  return relative.format(Math.round(hours / 24), 'day');
}

/**
 * Chooses a stored run visually: scenario first, then one of its latest runs as a card.
 */
@Component({
  selector: 'app-run-picker',
  imports: [FormsModule],
  template: `
    <div class="picker" [attr.aria-busy]="runs() === null">
      <label class="scenario">
        <span>{{ label() }}</span>
        <select
          [ngModel]="scenarioId()"
          (ngModelChange)="chooseScenario($event)"
          [disabled]="disabled() || !scenarios().length"
        >
          @for (scenario of scenarios(); track scenario.id) {
            <option [value]="scenario.id">{{ scenario.name }} · {{ scenario.code }}</option>
          }
        </select>
      </label>
      @if (error()) {
        <p class="notice" role="alert">{{ error() }}</p>
      }
      <div class="runs" role="group" [attr.aria-label]="'Rulări · ' + scenarioName()">
        @if (runs(); as list) {
          @for (run of list; track run.id) {
            <button
              type="button"
              class="run"
              [class.selected]="run.id === selected()"
              [attr.aria-pressed]="run.id === selected()"
              [disabled]="disabled() || !usable(run)"
              (click)="choose(run)"
            >
              <span class="run-top">
                <span class="dot" [attr.data-status]="run.status" aria-hidden="true"></span>
                <span class="status">{{ statusLabel(run) }}</span>
                @if ($first && usable(run)) {
                  <span class="latest">cea mai nouă</span>
                }
              </span>
              <strong>Seed {{ run.seed }} · rev. {{ run.scenarioRevision }}</strong>
              <small>{{ when(run) }} · {{ run.id.slice(0, 8) }}</small>
            </button>
          } @empty {
            <p class="empty">Nicio rulare disponibilă.</p>
          }
        } @else {
          @for (placeholder of [1, 2, 3]; track placeholder) {
            <span class="run skeleton" aria-hidden="true"></span>
          }
        }
      </div>
    </div>
  `,
  styles: `
    :host {
      display: block;
      min-width: 0;
    }
    .picker {
      display: grid;
      gap: 12px;
    }
    .scenario {
      display: grid;
      gap: 6px;
      max-width: 420px;
      font-size: 12px;
      font-weight: 500;
      color: var(--text-muted);
    }
    .runs {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(190px, 1fr));
      gap: 8px;
    }
    .run {
      display: grid;
      gap: 4px;
      min-height: 84px;
      padding: 12px 14px;
      text-align: left;
      border: 1px solid var(--border);
      border-radius: var(--radius);
      background: var(--panel);
      transition: none;
    }
    .run:hover:not(:disabled) {
      border-color: var(--accent-line);
      background: var(--panel);
      box-shadow: none;
    }
    .run.selected {
      border-color: var(--accent);
      background: var(--accent-soft);
      box-shadow: inset 0 0 0 1px var(--accent);
    }
    .run strong {
      font-size: 13px;
      font-weight: 600;
    }
    .run small {
      color: var(--text-muted);
      font-family: var(--font-mono);
      font-size: 11px;
    }
    .run-top {
      display: flex;
      align-items: center;
      gap: 6px;
      font-size: 11px;
      color: var(--text-muted);
    }
    .dot {
      width: 7px;
      height: 7px;
      border-radius: 50%;
      background: var(--text-faint);
    }
    .dot[data-status='completed'] {
      background: var(--success);
      box-shadow: none;
    }
    .dot[data-status='failed'] {
      background: var(--danger);
    }
    .dot[data-status='running'],
    .dot[data-status='queued'] {
      background: var(--live);
      animation: pulse 1.2s ease-in-out infinite;
    }
    .latest {
      margin-left: auto;
      padding: 1px 6px;
      border-radius: var(--radius);
      background: var(--accent);
      color: var(--on-accent);
      font-size: 10px;
      font-weight: 600;
    }
    .skeleton {
      background: linear-gradient(
          100deg,
          transparent 30%,
          var(--surface-hover) 50%,
          transparent 70%
        )
        var(--surface-raised);
      background-size: 200% 100%;
      animation: shimmer 1.3s linear infinite;
    }
    .empty {
      grid-column: 1 / -1;
      margin: 0;
      padding: 14px;
      border: 1px dashed var(--border-strong);
      border-radius: var(--radius);
      color: var(--text-muted);
      font-size: 13px;
    }
    @keyframes shimmer {
      to {
        background-position: -200% 0;
      }
    }
    @keyframes pulse {
      50% {
        opacity: 0.35;
      }
    }
  `,
})
export class RunPicker {
  private readonly api = inject(ApiClient);
  readonly label = input('Scenariu');
  readonly selected = input<string | null>(null);
  readonly preferredScenarioId = input<string | null>(null);
  /** Picks the newest completed run once loaded, unless a run is already selected. */
  readonly autoSelect = input(false);
  readonly disabled = input(false);
  readonly picked = output<PickedRun>();
  readonly scenarios = signal<ScenarioSummary[]>([]);
  readonly scenarioId = signal('');
  readonly runs = signal<PickerRun[] | null>(null);
  readonly error = signal('');
  readonly scenarioName = computed(
    () => this.scenarios().find((scenario) => scenario.id === this.scenarioId())?.name ?? '',
  );
  private pending?: Subscription;

  constructor() {
    effect(() => {
      const id = this.preferredScenarioId();
      if (id && this.scenarios().some((scenario) => scenario.id === id)) {
        untracked(() => {
          if (this.scenarioId() !== id) this.chooseScenario(id);
        });
      }
    });
    inject(DestroyRef).onDestroy(() => this.pending?.unsubscribe());
    // Each scenario's newest run decides which scenario opens first.
    this.pending = this.api
      .get<ScenarioSummary[]>('scenarios')
      .pipe(
        map((scenarios) => scenarios.filter((scenario) => !scenario.deletedAtUtc)),
        catchError(() => {
          this.error.set('Scenariile nu pot fi încărcate.');
          return of([] as ScenarioSummary[]);
        }),
      )
      .subscribe((scenarios) => {
        if (!scenarios.length) {
          this.runs.set([]);
          return;
        }
        this.pending = forkJoin(
          scenarios.map((scenario) =>
            this.api
              .get<PickerRun[]>(`scenarios/${scenario.id}/runs?limit=1`)
              .pipe(catchError(() => of([] as PickerRun[]))),
          ),
        ).subscribe((latest) => {
          const ranked = scenarios
            .map((scenario, index) => ({ scenario, at: timestamp(latest[index]?.[0]) }))
            .sort((a, b) => b.at - a.at)
            .map((entry) => entry.scenario);
          this.scenarios.set(ranked);
          const preferred = ranked.find((scenario) => scenario.id === this.preferredScenarioId());
          this.chooseScenario(preferred?.id ?? ranked[0]!.id);
        });
      });
  }

  chooseScenario(id: string) {
    this.scenarioId.set(id);
    this.runs.set(null);
    this.error.set('');
    this.pending?.unsubscribe();
    this.pending = this.api
      .get<PickerRun[]>(`scenarios/${id}/runs?limit=${RUNS_PER_SCENARIO}`)
      .subscribe({
        next: (runs) => {
          const sorted = [...runs].sort((a, b) => timestamp(b) - timestamp(a));
          this.runs.set(sorted);
          const newest = sorted.find((run) => this.usable(run));
          if (this.autoSelect() && !this.selected() && newest) this.choose(newest);
        },
        error: () => {
          this.runs.set([]);
          this.error.set('Rulările acestui scenariu nu pot fi încărcate.');
        },
      });
  }

  choose(run: PickerRun) {
    this.picked.emit({
      runId: run.id,
      scenarioId: this.scenarioId(),
      scenarioName: this.scenarioName(),
    });
  }

  usable(run: PickerRun) {
    return run.status === 'completed' && run.resultsValid !== false;
  }

  statusLabel(run: PickerRun) {
    const label = statusLabels[run.status] ?? run.status;
    return run.status === 'completed' && run.resultsValid === false
      ? `${label} · fără rezultate`
      : label;
  }

  when(run: PickerRun) {
    return fromNow(run.finishedAtUtc ?? run.startedAtUtc) || 'fără dată';
  }
}

function timestamp(run?: PickerRun) {
  const value = run?.finishedAtUtc ?? run?.startedAtUtc;
  return value ? Date.parse(value) : 0;
}

import {
  Component,
  computed,
  DestroyRef,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { finalize, map, Subscription, switchMap } from 'rxjs';
import { ApiClient } from '../../core/http/api-client';
import { ApiError } from '../../core/http/api-error.interceptor';
import { absoluteTimeLabel } from '../../shared/absolute-time';
import { fromNow, statusLabels } from '../../shared/ui/run-picker';
import { eventLabel, isOperational } from '../operations/events.models';
import { RunDetails, RunSummary, ScenarioDetails } from '../operations/operations.models';

export interface RevisionChange {
  kind: string;
  action: 'added' | 'changed' | 'removed';
  /** The flights or events concerned; the server's audit keeps no codes or texts. */
  ids: string[];
}
export interface ScenarioRevision {
  revision: number;
  recordedAtUtc: string;
  actor: string;
  changes: RevisionChange[];
  runs: { id: string; status: string }[];
}

const SECTIONS: Record<string, string> = {
  created: 'Scenariu creat',
  staffing: 'Personal modificat',
  passengers: 'Pasageri modificați',
  routes: 'Trasee modificate',
  resources: 'Resurse modificate',
  settings: 'Setări de rulare modificate',
  details: 'Nume sau descriere modificate',
  layout: 'Layout actualizat',
};
const ACTIONS = {
  added: ['adăugat', 'adăugate'],
  changed: ['modificat', 'modificate'],
  removed: ['eliminat', 'eliminate'],
} as const;

/** One plain line per change. Flights and events are named from the current scenario; removed ones no longer can be. */
export function changeLabel(change: RevisionChange, scenario: ScenarioDetails): string {
  const [one, many] = ACTIONS[change.action] ?? ACTIONS.changed;
  const n = change.ids.length;
  if (change.kind === 'flight') {
    if (n !== 1) return `${count(n)} zboruri ${many}`;
    const code = scenario.inputs.flights.find((row) => row.id === change.ids[0])?.code;
    return code ? `Zbor ${code} ${one}` : `Zbor ${one}`;
  }
  if (change.kind === 'event') {
    if (n !== 1) return `${count(n)} evenimente ${many}`;
    const type = scenario.inputs.events.find((row) => row.id === change.ids[0])?.type ?? '';
    const name =
      type === 'incident.passenger-surge'
        ? 'Aflux de pasageri'
        : isOperational(type)
          ? eventLabel(type)
          : '';
    return `Eveniment ${one}${name ? `: ${name}` : ''}`;
  }
  return SECTIONS[change.kind] ?? 'Configurație modificată';
}
/** Romanian puts "de" before 20 and above: "3 zboruri", "20 de zboruri", "105 zboruri". */
function count(n: number) {
  return n % 100 >= 20 || (n >= 100 && n % 100 === 0) ? `${n} de` : `${n}`;
}
function restoreFailure(error: unknown) {
  if (!(error instanceof ApiError)) return 'Revizia nu a putut fi restaurată. Reîncearcă.';
  if (error.status === 409)
    return 'Scenariul s-a modificat între timp, așa că revizia nu a fost restaurată. Încearcă din nou.';
  return `Revizia nu a putut fi restaurată: ${error.message}`;
}

/** Who changed the selected scenario, when and what, newest first, with the runs made on each revision. */
@Component({
  selector: 'app-revision-timeline',
  template: `
    <details (toggle)="open.set($any($event.target).open)">
      <summary>Istoric revizii</summary>
      @if (failed()) {
        <p class="state" role="alert">
          Istoricul nu a putut fi încărcat.
          <button type="button" (click)="load()">Reîncearcă</button>
        </p>
      } @else if (!entries()) {
        <p class="state" role="status">Se încarcă istoricul…</p>
      } @else if (!entries()!.length) {
        <p class="state">Nicio revizie înregistrată încă.</p>
      }
      @if (notice()) {
        <p class="state" role="status">{{ notice() }}</p>
      }
      @if (entries()?.length) {
        <ol [attr.aria-busy]="loading()">
          @for (entry of entries(); track $index) {
            <li>
              <p class="when">
                <strong>Revizia {{ entry.revision }}</strong>
                @if (entry.revision === scenario().scenario.revision) {
                  <span class="current">curentă</span>
                }
                <span>{{ fromNow(entry.recordedAtUtc) }}</span>
                <span>{{ absolute(entry.recordedAtUtc) }}</span>
                <span>{{ entry.actor === 'system' ? 'sistem' : entry.actor }}</span>
              </p>
              <ul>
                @for (change of entry.changes; track $index) {
                  <li>{{ label(change) }}</li>
                } @empty {
                  <li>Fără modificări de conținut</li>
                }
              </ul>
              @if (entry.runs.length) {
                <div
                  class="runs"
                  role="group"
                  [attr.aria-label]="'Rulări pe revizia ' + entry.revision"
                >
                  @for (run of entry.runs; track run.id) {
                    <button
                      type="button"
                      [attr.data-status]="run.status"
                      [attr.aria-pressed]="run.id === selectedRunId()"
                      [title]="statusLabel(run.status)"
                      (click)="openRun.emit(run.id)"
                    >
                      <span class="dot" aria-hidden="true"></span>{{ runName(run.id) }}
                    </button>
                  }
                </div>
              }
              @if (canRestore() && restorable(entry)) {
                <button
                  type="button"
                  class="restore"
                  [disabled]="restoring() !== null"
                  (click)="restore(entry)"
                >
                  {{
                    restoring() === entry.revision ? 'Se restaurează…' : 'Revino la această revizie'
                  }}
                </button>
              }
            </li>
          }
        </ol>
      }
    </details>
  `,
  styles: `
    :host {
      display: block;
      border: 1px solid var(--border);
      border-top: 0;
      background: var(--panel);
    }
    summary {
      padding: 12px 24px;
      cursor: pointer;
      font-size: var(--dp-typography-label);
      letter-spacing: var(--dp-typography-label-tracking);
      text-transform: uppercase;
    }
    .state {
      margin: 0;
      padding: 0 24px 16px;
      color: var(--text-muted);
      font-size: 14px;
    }
    ol {
      max-height: 420px;
      margin: 0;
      padding: 0 24px 16px;
      overflow-y: auto;
      list-style: none;
    }
    ol > li {
      padding: 10px 0 10px 16px;
      border-left: 2px solid var(--border);
    }
    .when {
      display: flex;
      flex-wrap: wrap;
      gap: 4px 12px;
      margin: 0;
      font-size: 14px;
    }
    .when span {
      color: var(--text-muted);
    }
    ul {
      margin: 4px 0 0;
      padding-left: 18px;
      font-size: 14px;
    }
    .runs {
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
      margin-top: 6px;
    }
    .runs button {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      min-height: 28px;
      padding: 2px 10px;
      font-size: 13px;
      font-variant-numeric: tabular-nums;
    }
    .runs button[aria-pressed='true'] {
      background: var(--text);
      color: var(--panel);
    }
    .current {
      color: var(--text);
      font-weight: 600;
    }
    .restore {
      min-height: 28px;
      margin-top: 8px;
      padding: 2px 10px;
      font-size: 13px;
    }
    .dot {
      width: 8px;
      height: 8px;
      border-radius: 50%;
      background: currentColor;
      opacity: 0.5;
    }
    [data-status='completed'] > .dot {
      background: #3ddc84;
      opacity: 1;
    }
    [data-status='failed'] > .dot,
    [data-status='pending'] > .dot,
    [data-status='planned'] > .dot,
    [data-status='running'] > .dot {
      background: var(--signal);
      opacity: 1;
    }
  `,
})
export class RevisionTimeline {
  private readonly api = inject(ApiClient);
  readonly scenario = input.required<ScenarioDetails>();
  /** The page's run list: numbers runs as "Rulări recente" does, and a new run or status refreshes the history. */
  readonly runs = input<RunSummary[]>([]);
  readonly selectedRunId = input<string | null>(null);
  readonly openRun = output<string>();
  /** Writing allowed and nothing else in progress on the page. */
  readonly canRestore = input(false);
  readonly restored = output<ScenarioDetails>();
  readonly restoring = signal<number | null>(null);
  readonly notice = signal('');
  readonly open = signal(false);
  readonly entries = signal<ScenarioRevision[] | null>(null);
  readonly loading = signal(false);
  readonly failed = signal(false);
  readonly statusLabel = (status: string) => statusLabels[status] ?? status;
  /** Ticks every 30 s so relative times change between change-detection passes, never during one (NG0100). */
  private readonly now = signal(Date.now());
  readonly fromNow = (iso: string) => fromNow(iso, this.now());
  private readonly changed = computed(() => {
    const { id, revision } = this.scenario().scenario;
    return `${id}/${revision}/${this.runs()
      .map((run) => run.id + run.status)
      .join()}`;
  });
  private request = Subscription.EMPTY;
  private restoration = Subscription.EMPTY;
  private shownId = '';

  constructor() {
    const clock = setInterval(() => this.now.set(Date.now()), 30_000);
    inject(DestroyRef).onDestroy(() => {
      clearInterval(clock);
      this.request.unsubscribe();
      this.restoration.unsubscribe();
    });
    // Fetched only while open, so the page itself makes no extra request.
    effect(() => {
      this.changed();
      if (this.open()) this.load();
    });
  }

  load() {
    const id = this.scenario().scenario.id;
    this.request.unsubscribe();
    if (id !== this.shownId) this.entries.set(null);
    this.failed.set(false);
    this.loading.set(true);
    this.request = this.api
      .get<ScenarioRevision[]>(`scenarios/${id}/revisions`)
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: (rows) => {
          this.shownId = id;
          this.entries.set(rows);
        },
        error: () => this.failed.set(true),
      });
  }
  /** Only a revision with a run can come back: each run captured its revision's whole configuration. */
  restorable(entry: ScenarioRevision) {
    return entry.revision !== this.scenario().scenario.revision && entry.runs.length > 0;
  }
  /** Saves the revision's captured configuration as a new revision; the server refuses if the scenario moved on. */
  restore(entry: ScenarioRevision) {
    const current = this.scenario();
    this.restoring.set(entry.revision);
    this.notice.set('');
    this.restoration.unsubscribe();
    this.restoration = this.api
      .get<RunDetails>(`runs/${entry.runs[0].id}`)
      .pipe(
        map((run) => {
          const captured = JSON.parse(run.inputSnapshotJson ?? 'null')?.scenario as
            ScenarioDetails | undefined;
          if (!captured?.inputs || captured.scenario.revision !== entry.revision)
            throw new Error('missing capture');
          return captured;
        }),
        switchMap((captured) => {
          const { id, code, name, description, sceneId, revision } = current.scenario;
          return this.api.put<ScenarioDetails>(`scenarios/${id}`, {
            code,
            name,
            description,
            sceneId,
            layoutVersionId: captured.scenario.layoutVersionId,
            inputs: captured.inputs,
            revision,
          });
        }),
        finalize(() => this.restoring.set(null)),
      )
      .subscribe({
        next: (saved) => {
          this.notice.set(
            `Revizia ${entry.revision} a fost restaurată ca revizia ${saved.scenario.revision}. Pornește simularea ca să o verifici.`,
          );
          this.restored.emit(saved);
        },
        error: (error: unknown) => this.notice.set(restoreFailure(error)),
      });
  }
  label(change: RevisionChange) {
    return changeLabel(change, this.scenario());
  }
  absolute(utc: string) {
    return absoluteTimeLabel(utc, this.scenario().inputs.timeZone);
  }
  runName(id: string) {
    const index = this.runs().findIndex((run) => run.id === id);
    return index < 0 ? 'Rulare' : `#${this.runs().length - index}`;
  }
}

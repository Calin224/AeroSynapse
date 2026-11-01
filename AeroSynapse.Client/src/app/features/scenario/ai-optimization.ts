import { NgTemplateOutlet } from '@angular/common';
import { Component, DestroyRef, effect, inject, input, output, signal } from '@angular/core';
import { finalize, Subscription } from 'rxjs';
import { WriteAccess } from '../../core/auth/write-access';
import { ApiClient } from '../../core/http/api-client';
import { ApiError } from '../../core/http/api-error.interceptor';

export interface OptimizationSide {
  meanWaitMinutes: number | null;
  maxP95WaitMinutes: number | null;
  maxQueue: number;
  queueClearedMinute: number | null;
}
export interface OptimizationOption {
  id: string;
  kind: 'move' | 'add' | 'save' | 'desk';
  action: string;
  operatorDelta: number;
  result: OptimizationSide;
  /** One sentence checked against the numbers on the server; the card's title is the action itself. */
  explanation: string;
  recommended: boolean;
  /** Operators added (+) or removed (−) per checkpoint id; sent back as is to apply the option. */
  staffDelta: Record<string, number>;
  /** Check-in desks opened, each with one of the scenario's check-in operators. */
  deskDelta?: number;
}
/** Staffing options for a finished run, each simulated on the same day and seed; the recommendation comes from the numbers, the model only words it. */
export interface RunOptimization {
  modelStatus: string;
  model: string;
  /** Where the longest queue formed and what is recommended; present whenever options were tested. */
  summary: string | null;
  baseline: OptimizationSide;
  options: OptimizationOption[];
  /** No tested option is worth it: nothing is recommended and the options are only shown on request. */
  optimal?: boolean;
  /** Present when the scenario has check-in desks; the waits then include them. */
  checkIn?: { name: string; open: number; desks: number } | null;
  /** Why the bottleneck gets no extra people: the scenario has nobody free for it. */
  notice?: string | null;
}

const silent: Record<string, string> = {
  unconfigured: 'Modelul AI nu este configurat',
  quota_exhausted: 'Contul Gemini nu mai are credit sau a atins limita de cereri',
  timeout: 'Modelul AI nu a răspuns la timp',
};

/** What AI proposes after a simulation: alternatives tested in the simulator, explained in plain words. */
@Component({
  selector: 'app-ai-optimization',
  imports: [WriteAccess, NgTemplateOutlet],
  template: `
    <section class="ai" aria-labelledby="ai-title" [attr.aria-busy]="loading()">
      <header>
        <h3 id="ai-title" tabindex="-1" #heading>
          <span class="spark" aria-hidden="true">✦</span> Optimizare propusă de AI
        </h3>
        @if (result(); as value) {
          <span
            class="source"
            [class.fallback]="value.modelStatus !== 'chosen'"
            [attr.title]="value.modelStatus === 'chosen' ? null : silentReason(value.modelStatus)"
          >
            {{ value.modelStatus === 'chosen' ? value.model : 'calcul automat' }}
          </span>
        }
      </header>

      @if (loading()) {
        <p class="lead" role="status">AI-ul testează variante în simulator, pe aceeași zi…</p>
        <div class="grid" aria-hidden="true">
          @for (slot of [1, 2, 3]; track slot) {
            <div class="card skeleton"></div>
          }
        </div>
      } @else if (failed()) {
        <p class="lead" role="alert">Nu am putut calcula variantele de optimizare.</p>
        <!-- The button disappears while loading; focus moves to the heading so keyboard users keep their place. -->
        <button type="button" (click)="heading.focus(); load(runId())">Reîncearcă</button>
      } @else if (result(); as value) {
        @if (value.optimal) {
          <p class="optimal" role="status">
            <strong>✓ Scenariul este deja optim.</strong> Am testat {{ value.options.length }}
            {{ value.options.length === 1 ? 'variantă' : 'variante' }} pe aceeași zi și niciuna nu
            scurtează vizibil așteptarea și nici nu economisește personal fără să o lungească.
          </p>
        } @else if (!value.options.length) {
          <p class="lead">Nu există variante de personal de testat pentru acest scenariu.</p>
        } @else if (value.summary) {
          <p class="lead summary">{{ value.summary }}</p>
        }
        @if (value.notice) {
          <p class="lead">{{ value.notice }}</p>
        }
        @if (stale() && value.options.length) {
          <p class="lead stale" role="status">
            Scenariul a fost modificat după această simulare, așa că variantele nu mai pot fi
            aplicate. Pornește o simulare nouă ca să vezi variantele pentru configurația actuală.
          </p>
        }
        @if (value.optimal) {
          <details class="tested">
            <summary>Vezi variantele testate</summary>
            <ng-container *ngTemplateOutlet="cards; context: { $implicit: value }" />
          </details>
        } @else {
          <ng-container *ngTemplateOutlet="cards; context: { $implicit: value }" />
        }
        @if (value.options.length) {
          <p class="note">
            Fiecare variantă a fost simulată pe aceeași zi. Cifrele și recomandarea vin din
            simulator.
            @if (value.modelStatus === 'chosen') {
              AI-ul doar le explică în cuvinte simple.
            }
          </p>
        }
      } @else {
        <p class="lead">Rularea nu are rezultate valide, deci nu există variante de testat.</p>
      }
      <ng-template #cards let-value>
        <div class="grid">
          @for (option of value.options; track option.id) {
            @let wait = option.result.meanWaitMinutes;
            @let before = value.baseline.meanWaitMinutes;
            <article class="card" [class.best]="option.recommended">
              @if (option.recommended) {
                <span class="badge">Recomandat</span>
              }
              <h4>{{ option.action }}</h4>
              <div class="metric">
                <span class="label">{{ value.checkIn ? 'Așteptare medie (check-in + control)' : 'Așteptare medie' }}</span>
                <span class="numbers">
                  <s>{{ minutes(before) }}</s> → <strong>{{ minutes(wait) }}</strong>
                  @if (change(before, wait); as pct) {
                    <span class="delta" [class.worse]="pct > 0"
                      >{{ pct > 0 ? '+' : '' }}{{ pct }}%</span
                    >
                  }
                </span>
                <span class="bar" aria-hidden="true">
                  <span [style.width.%]="share(before, wait)"></span>
                </span>
              </div>
              <ul class="facts">
                <li>{{ staff(option) }}</li>
                <li>Coadă maximă {{ value.baseline.maxQueue }} → {{ option.result.maxQueue }}</li>
              </ul>
              @if (option.explanation) {
                <p class="why">{{ option.explanation }}</p>
              }
              @if (!stale()) {
                <div class="apply" appWriteAccess>
                  <button type="button" [disabled]="applying()" (click)="apply(option)">
                    {{ applying() === option.id ? 'Se aplică…' : 'Aplică în scenariu' }}
                  </button>
                  @if (failure()?.id === option.id) {
                    <p class="issue" role="alert">{{ failure()!.text }}</p>
                  }
                </div>
              }
            </article>
          }
        </div>
      </ng-template>
    </section>
  `,
  styles: `
    :host {
      display: block;
    }
    .ai {
      padding: 16px 20px;
      border-top: 1px solid var(--border);
      background: linear-gradient(
        135deg,
        color-mix(in srgb, var(--accent) 7%, transparent),
        transparent 60%
      );
    }
    header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 12px;
    }
    h3 {
      margin: 0;
      font-size: 16px;
    }
    .spark {
      color: var(--accent);
    }
    .source {
      padding: 2px 8px;
      border: 1px solid var(--accent-line, var(--border));
      border-radius: 999px;
      font-size: 11px;
      white-space: nowrap;
      color: var(--accent);
    }
    .source.fallback {
      color: var(--text-muted);
      border-color: var(--border);
    }
    .optimal {
      margin: 8px 0 12px;
      padding: 10px 14px;
      border-left: 4px solid var(--success);
      background: color-mix(in srgb, var(--success) 10%, transparent);
      line-height: 1.5;
    }
    .tested summary {
      margin-bottom: 10px;
      font-size: 13px;
      color: var(--text-muted);
      cursor: pointer;
    }
    .lead {
      margin: 8px 0 12px;
      color: var(--text-muted);
      line-height: 1.5;
    }
    .lead.summary {
      color: var(--text);
    }
    .grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(210px, 1fr));
      gap: 10px;
    }
    .card {
      display: grid;
      align-content: start;
      gap: 8px;
      padding: 12px 14px;
      border: 1px solid var(--border);
      border-radius: var(--radius, 8px);
      background: var(--panel, var(--surface-raised));
      animation: rise 0.35s ease-out both;
    }
    .card:nth-child(2) {
      animation-delay: 0.06s;
    }
    .card:nth-child(3) {
      animation-delay: 0.12s;
    }
    .card:nth-child(4) {
      animation-delay: 0.18s;
    }
    .card.best {
      border-color: var(--accent);
      box-shadow:
        0 0 0 1px var(--accent),
        0 6px 24px -12px var(--accent-glow, var(--accent));
    }
    .badge {
      justify-self: start;
      padding: 1px 8px;
      border-radius: 999px;
      background: var(--accent);
      color: var(--on-accent, #fff);
      font-size: 11px;
      font-weight: 600;
    }
    h4 {
      margin: 0;
      font-size: 14px;
      line-height: 1.3;
    }
    .note {
      margin: 0;
      font-size: 12px;
      color: var(--text-faint);
    }
    .facts {
      display: flex;
      flex-wrap: wrap;
      gap: 4px 12px;
      margin: 0;
      padding: 0;
      list-style: none;
      font-size: 12px;
      color: var(--text-muted);
    }
    .metric {
      display: grid;
      gap: 4px;
    }
    .label {
      font-size: 11px;
      color: var(--text-faint);
    }
    .numbers {
      font-variant-numeric: tabular-nums;
    }
    .numbers s {
      color: var(--text-faint);
    }
    .numbers strong {
      font-size: 18px;
    }
    .delta {
      white-space: nowrap;
      margin-left: 6px;
      padding: 0 6px;
      border-radius: 4px;
      background: color-mix(in srgb, var(--success) 15%, transparent);
      color: var(--success);
      font-size: 12px;
      font-weight: 600;
    }
    .delta.worse {
      background: var(--danger-soft);
      color: var(--danger);
    }
    .bar {
      height: 4px;
      border-radius: 2px;
      background: var(--surface-sunken, var(--border));
      overflow: hidden;
    }
    .bar span {
      display: block;
      height: 100%;
      background: var(--accent);
      transition: width 0.6s ease-out;
    }
    .why {
      margin: 0;
      font-size: 13px;
      line-height: 1.45;
    }
    .note {
      margin-top: 10px;
    }
    .apply {
      display: grid;
      gap: 4px;
    }
    .apply button {
      justify-self: start;
    }
    .issue {
      margin: 0;
      font-size: 12px;
      color: var(--danger);
    }
    .skeleton {
      min-height: 150px;
      background: linear-gradient(
        90deg,
        var(--surface-sunken, var(--border)) 0%,
        var(--surface-hover, transparent) 50%,
        var(--surface-sunken, var(--border)) 100%
      );
      background-size: 200% 100%;
      animation: shimmer 1.2s linear infinite;
    }
    @keyframes rise {
      from {
        opacity: 0;
        transform: translateY(6px);
      }
    }
    @keyframes shimmer {
      to {
        background-position: -200% 0;
      }
    }
    @media (prefers-reduced-motion: reduce) {
      .card,
      .skeleton,
      .bar span {
        animation: none;
        transition: none;
      }
    }
  `,
})
export class AiOptimization {
  private readonly api = inject(ApiClient);
  readonly runId = input.required<string>();
  /** The scenario moved past the simulated revision: the server would refuse every apply, so none is offered. */
  readonly stale = input(false);
  readonly result = signal<RunOptimization | null>(null);
  readonly loading = signal(false);
  readonly failed = signal(false);
  /** The page reloads the scenario; this panel is done with the run once an option is in it. */
  readonly applied = output<void>();
  readonly applying = signal<string | null>(null);
  readonly failure = signal<{ id: string; text: string } | null>(null);

  /** Only the latest request may write the panel; the previous one is cancelled, which also stops its simulations. */
  private request = Subscription.EMPTY;

  constructor() {
    effect(() => this.load(this.runId()));
    inject(DestroyRef).onDestroy(() => this.request.unsubscribe());
  }

  load(runId: string) {
    this.request.unsubscribe();
    this.result.set(null);
    this.failed.set(false);
    this.loading.set(true);
    this.request = this.api
      .get<RunOptimization | null>(`runs/${runId}/ai-optimization`)
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: (value) => this.result.set(value),
        error: () => this.failed.set(true),
      });
  }

  /** Saved as a new scenario revision by the server, which refuses if the scenario changed since this run. */
  apply(option: OptimizationOption) {
    this.applying.set(option.id);
    this.failure.set(null);
    this.api
      .post(`runs/${this.runId()}/ai-optimization/apply`, {
        staffDelta: option.staffDelta,
        deskDelta: option.deskDelta ?? 0,
      })
      .pipe(finalize(() => this.applying.set(null)))
      .subscribe({
        next: () => this.applied.emit(),
        error: (error: unknown) => this.failure.set({ id: option.id, text: applyFailure(error) }),
      });
  }

  /** Under a minute in seconds, so short waits stay distinguishable. */
  minutes(value: number | null) {
    if (value === null) return '–';
    if (Math.round(value * 60) < 60) return `${Math.round(value * 60)} s`;
    return `${value.toLocaleString('ro-RO', { maximumFractionDigits: 1 })} min`;
  }
  change(before: number | null, after: number | null) {
    if (before === null || after === null || before <= 0) return null;
    return Math.round(((after - before) / before) * 100) || null;
  }
  /** Bar length: the option's wait as a share of the reference wait. */
  share(before: number | null, after: number | null) {
    if (before === null || after === null || before <= 0) return 0;
    return Math.min(100, (after / before) * 100);
  }
  staff(option: OptimizationOption) {
    const delta = option.operatorDelta;
    // A desk is staffed by one check-in operator, so desks say it better than operators.
    if (option.kind === 'desk') return `+${delta} ${delta === 1 ? 'ghișeu' : 'ghișee'} check-in`;
    if (delta === 0) return 'Fără personal suplimentar';
    const count = `${Math.abs(delta)} ${Math.abs(delta) === 1 ? 'operator' : 'operatori'}`;
    return delta > 0 ? `+${count}` : `Economisește ${count}`;
  }
  silentReason(status: string) {
    return silent[status] ?? 'Modelul AI nu a fost disponibil';
  }
}

function applyFailure(error: unknown) {
  if (!(error instanceof ApiError)) return 'Varianta nu a fost aplicată. Reîncearcă.';
  if (error.status === 409)
    return 'Scenariul s-a modificat după această simulare, așa că varianta nu a fost aplicată. Pornește o simulare nouă ca să primești variante actuale.';
  return error.fieldErrors['staffDelta']?.[0] ?? error.message;
}

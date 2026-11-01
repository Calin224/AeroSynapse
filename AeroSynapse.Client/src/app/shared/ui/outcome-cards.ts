import { DecimalPipe } from '@angular/common';
import { Component, computed, input } from '@angular/core';

/** One measured outcome, already in display units; lower values are better. */
export interface OutcomeRow {
  key: string;
  label: string;
  unit: string;
  before: number | null;
  after: number | null;
  /** Relative change in percent, `null` when it cannot be computed. */
  percent: number | null;
}
export type OutcomeTone = 'better' | 'worse' | 'neutral';

/** Changes under half a percent read as "no change". */
export function outcomeTone(percent: number | null): OutcomeTone {
  return percent === null || Math.abs(percent) < 0.5 ? 'neutral' : percent < 0 ? 'better' : 'worse';
}
export function signedPercent(percent: number | null) {
  return percent === null ? '—' : `${percent < 0 ? '−' : '+'}${Math.abs(percent).toFixed(2)}%`;
}
/** A wait a person can read: seconds under a minute, minutes otherwise. */
export function formatWait(minutes: number | null | undefined) {
  if (minutes == null || !Number.isFinite(minutes)) return '—';
  if (minutes < 1) return `${(minutes * 60).toFixed(2)} s`;
  return `${minutes.toFixed(2)} min`;
}
export function percentChange(before: number | null, after: number | null) {
  return before === null || after === null || before <= 0
    ? null
    : ((after - before) / before) * 100;
}

/** Before/after cards with proportional bars, for a small set of outcomes. */
@Component({
  selector: 'app-outcome-cards',
  imports: [DecimalPipe],
  template: `
    @for (row of cards(); track row.key) {
      <article class="kpi" [attr.data-tone]="row.tone">
        <header>
          <h3>{{ row.label }}</h3>
          <span class="delta">{{ row.signed }}</span>
        </header>
        <div class="bar-row">
          <span>{{ beforeLabel() }}</span>
          <span class="bar before"><i [style.width.%]="row.beforeWidth"></i></span>
          <strong>{{ row.before === null ? '—' : (row.before | number: row.digits) }}</strong>
        </div>
        <div class="bar-row">
          <span>{{ afterLabel() }}</span>
          <span class="bar after"><i [style.width.%]="row.afterWidth"></i></span>
          <strong>{{ row.after === null ? '—' : (row.after | number: row.digits) }}</strong>
        </div>
        <small>{{ row.unit }}{{ note() ? ' · ' + note() : '' }}</small>
      </article>
    }
  `,
  styles: `
    :host {
      --before: #c8643f;
      --after: #258a72;
      --after-soft: #258a7224;
      --after-text: #1c6b58;
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(min(100%, 15rem), 1fr));
      gap: 1rem;
    }
    .kpi {
      display: grid;
      gap: 0.75rem;
      min-width: 0;
      padding: 1.25rem;
      border: 1px solid var(--border);
      border-radius: var(--radius-lg);
      background: var(--panel);
      box-shadow: var(--shadow-panel);
    }
    header {
      display: flex;
      justify-content: space-between;
      align-items: flex-start;
      gap: 0.75rem;
    }
    h3 {
      margin: 0;
      font-size: 0.95rem;
      font-weight: 600;
    }
    .delta {
      flex: none;
      padding: 0.2rem 0.6rem;
      border-radius: var(--radius);
      background: var(--surface-raised);
      font-weight: 700;
      font-variant-numeric: tabular-nums;
    }
    [data-tone='better'] .delta {
      background: var(--after-soft);
      color: var(--after-text);
    }
    [data-tone='worse'] .delta {
      background: var(--danger-soft);
      color: var(--danger);
    }
    .bar-row {
      display: grid;
      grid-template-columns: 3.5rem 1fr 3.5rem;
      align-items: center;
      gap: 0.6rem;
      font-size: 0.8rem;
      color: var(--text-muted);
    }
    .bar-row strong {
      text-align: right;
      font-size: 1.15rem;
      color: var(--text);
      font-variant-numeric: tabular-nums;
    }
    .bar {
      height: 0.65rem;
      border-radius: var(--radius);
      background: var(--surface-raised);
      overflow: hidden;
    }
    .bar i {
      display: block;
      height: 100%;
      border-radius: inherit;
      transition: none;
    }
    .bar.before i {
      background: var(--before);
    }
    .bar.after i {
      background: var(--after);
    }
    small {
      color: var(--text-muted);
      font-size: 0.8rem;
    }
    @media (prefers-reduced-motion: reduce) {
      .bar i {
        transition: none;
      }
    }
  `,
})
export class OutcomeCards {
  readonly rows = input.required<readonly OutcomeRow[]>();
  readonly beforeLabel = input('Înainte');
  readonly afterLabel = input('După');
  readonly note = input('');
  readonly cards = computed(() =>
    this.rows().map((row) => {
      const top = Math.max(row.before ?? 0, row.after ?? 0) || 1;
      return {
        ...row,
        tone: outcomeTone(row.percent),
        signed: signedPercent(row.percent),
        // Small values keep a second decimal, so 0.02 does not read as 0.
        digits: '1.2-2',
        beforeWidth: ((row.before ?? 0) / top) * 100,
        afterWidth: ((row.after ?? 0) / top) * 100,
      };
    }),
  );
}

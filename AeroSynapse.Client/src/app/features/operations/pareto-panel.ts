import { Component, DestroyRef, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { finalize, Subscription } from 'rxjs';
import { ApiClient } from '../../core/http/api-client';
import type { BatchSummary } from '../batches/batches.models';
import type { StaffingEvaluationDetails } from './staffing-evaluation.models';
import type { ParetoComparison, ParetoPoint } from './pareto.models';

@Component({ selector: 'app-pareto-panel', imports: [FormsModule], templateUrl: './pareto-panel.html',
  styles: `:host {display:block; border:1px solid var(--border-color,#64748b); border-radius: var(--radius); padding:1rem; margin:1rem 0;}
    th,td {padding:.5rem;text-align:left;} .table-wrap {overflow:auto;} svg {width:100%;max-width:48rem;height:auto;background:var(--surface-card,#14202d);border-radius: var(--radius);}
    svg text {fill:currentColor;font-size:12px;} svg line {stroke:currentColor;opacity:.5;} .actions {display:flex;gap:.75rem;flex-wrap:wrap;margin:.75rem 0;}
    .preferred {font-weight:700;} code {overflow-wrap:anywhere;} label {display:grid;gap:.3rem;} .risk {color:var(--text-secondary);}` })
export class ParetoPanel {
  readonly evaluation = input.required<StaffingEvaluationDetails>();
  readonly history = input<BatchSummary[]>([]);
  readonly active = input(false);
  readonly comparison = signal<ParetoComparison | null>(null);
  readonly message = signal(''); readonly busy = signal(false);
  compareId = '';
  private readonly api = inject(ApiClient);
  private request?: Subscription;
  private context = '';
  private readonly numbers = new Intl.NumberFormat('ro-RO', { maximumFractionDigits: 4 });
  constructor() {
    inject(DestroyRef).onDestroy(() => this.request?.unsubscribe());
    effect(() => { const id = this.evaluation().batch.batch.id; const active = this.active();
      untracked(() => { if (id !== this.context) { this.request?.unsubscribe(); this.context = id; this.comparison.set(null); this.compareId = ''; this.message.set(''); }
        if (!active) this.request?.unsubscribe(); }); });
  }
  alternatives() { return this.history().filter(item => item.id !== this.evaluation().batch.batch.id && ['completed', 'completed_with_failures'].includes(item.status)); }
  compare() {
    if (!this.active() || this.busy() || !this.compareId) return;
    this.comparison.set(null); this.message.set(''); this.busy.set(true);
    this.request = this.api.get<ParetoComparison>(`staffing-evaluations/${this.evaluation().batch.batch.id}/pareto/compare/${this.compareId}`)
      .pipe(finalize(() => this.busy.set(false))).subscribe({ next: value => this.comparison.set(value), error: error => this.report(error) });
  }
  display(value: number | null) { return value === null ? 'indisponibil' : this.numbers.format(value); }
  label(runId: string) {
    const point = this.evaluation().pareto?.points.find(item => item.runId === runId);
    return point ? point.position === 0 ? 'Baseline' : 'Candidat ' + point.position : runId;
  }
  x(point: ParetoPoint) {
    const points = this.plotPoints(); const maximum = Math.max(1, ...points.map(item => item.costPoints));
    return 70 + point.costPoints / maximum * 570;
  }
  y(point: ParetoPoint) {
    const maximum = Math.max(1, ...this.plotPoints().map(item => item.timeMinutes ?? 0));
    return 280 - (point.timeMinutes ?? 0) / maximum * 230;
  }
  plotPoints() {
    return [...(this.evaluation().pareto?.points ?? []), ...(this.comparison()?.comparable ? this.comparison()!.right.points : [])].filter(item => item.feasible);
  }
  status(value: string) { return ({ unavailable: 'Evaluare incompletă: frontiera nu este disponibilă.',
    no_feasible_solution: 'Nicio soluție fezabilă în limitele declarate.', no_tradeoff_observed: 'Nu au fost observate două soluții cu un compromis real. Lărgește limitele sau căutarea.',
    tradeoffs_available: 'Alternative cu compromisuri verificate în simulator.' } as Record<string, string>)[value] ?? value; }
  reason(code: string) { const [key, id] = code.split(':'); return ({ operational_budget: 'Buget operațional depășit', staff_budget: 'Buget de personal depășit',
    mean_wait_limit: 'Limita de așteptare medie depășită', p95_limit: 'Limita P95 depășită', boarding_risk_limit: 'Limita de risc depășită',
    missing_successful_observations: 'Lipsesc observații finale valide', evaluation_incomplete_or_cancelled: 'Evaluare incompletă, anulată sau expirată',
    staffing_constraint: 'Restricție de personal încălcată', incomparable_population_or_engine: 'Populație sau motor incompatibil' } as Record<string, string>)[key]?.concat(id ? ': ' + id : '') ?? code; }
  private report(error: unknown) { this.message.set(error instanceof Error ? error.message : 'Comparația nu a putut fi încărcată.'); }
}

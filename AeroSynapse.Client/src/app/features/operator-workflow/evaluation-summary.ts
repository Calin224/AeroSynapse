import { Component, computed, input } from '@angular/core';
import { COST_BASIS, costText, netSaving } from '../../shared/ui/cost-estimate';
import { OutcomeCards, signedPercent } from '../../shared/ui/outcome-cards';
import { ShiftPlan } from './shift-plan';
import type { EvaluationSummary, ShiftPlanView } from './workflow.models';

/** The verdict, the staffing plan and every simulated candidate, compared with today. */
@Component({
  selector: 'app-evaluation-summary',
  imports: [OutcomeCards, ShiftPlan],
  templateUrl: './evaluation-summary.html',
  styleUrl: './evaluation-summary.css',
})
export class EvaluationSummaryView {
  readonly result = input.required<EvaluationSummary>();
  /** With a shift plan, the per-slot grid replaces the one-number-per-checkpoint plan. */
  readonly shiftPlan = input<ShiftPlanView | null>(null);
  /** Only the recommended plan is valued in money; a rejected plan is not a saving we claim. */
  readonly cost = computed(() => {
    const { savings, recommended } = this.result();
    return savings && recommended
      ? costText(netSaving(savings.passengerHoursSaved, savings.operatorHoursAdded))
      : null;
  });
  readonly costBasis = COST_BASIS;
  readonly signed = signedPercent;
}

import { Component, input } from '@angular/core';
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
  readonly signed = signedPercent;
}

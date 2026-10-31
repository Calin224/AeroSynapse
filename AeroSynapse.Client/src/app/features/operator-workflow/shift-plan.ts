import { Component, computed, input } from '@angular/core';
import type { ShiftPlanView } from './workflow.models';

/** A shift plan per 15-minute slot beside today's staffing, and every person's shift on one day axis. */
@Component({
  selector: 'app-shift-plan',
  templateUrl: './shift-plan.html',
  styleUrl: './shift-plan.css',
})
export class ShiftPlan {
  readonly plan = input.required<ShiftPlanView>();
  /** One header cell per hour, four slots wide; the last tick closes the day. */
  readonly hours = computed(() => this.plan().ticks.slice(0, -1));
  readonly checkpoints = computed(() =>
    this.plan().rows.map((row, tone) => ({ name: row.name, tone })),
  );
}

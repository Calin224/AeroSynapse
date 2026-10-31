import { DecimalPipe } from '@angular/common';
import { Component, computed, effect, input, signal, untracked } from '@angular/core';
import { QueueForecastResult } from './queue-forecast.models';
import { CheckpointOutlook, flowChart, queueChart } from './queue-forecast.view';

let instances = 0;

/** Observed history, forecast, threshold, mitigation and backtest for one checkpoint, with per-minute flow below. */
@Component({
  selector: 'app-queue-forecast-chart',
  imports: [DecimalPipe],
  templateUrl: './queue-forecast-chart.html',
  styleUrl: './queue-forecast-chart.css',
})
export class QueueForecastChart {
  readonly view = input.required<CheckpointOutlook>();
  readonly result = input.required<QueueForecastResult>();
  readonly name = input.required<string>();
  readonly hoverMinute = signal<number | null>(null);
  /** Gradient and clip ids must be unique per instance within the document. */
  readonly uid = `qf${++instances}`;
  readonly chart = computed(() => queueChart(this.view(), this.result()));
  readonly flow = computed(() => flowChart(this.view().series, this.chart().start, this.chart().to));
  readonly hover = computed(() => {
    const minute = this.hoverMinute(), view = this.view(), chart = this.chart();
    if (minute === null) return null;
    return {
      minute, x: chart.x(minute) / chart.box.width * 100,
      point: view.series.points.find(row => row.minute === minute) ?? null,
      observed: minute <= chart.from ? [...view.observed].reverse().find(row => row.minute <= minute) ?? null : null,
      actual: view.actual.find(row => row.minute === minute) ?? null,
      mitigated: view.mitigated?.find(row => row.minute === minute) ?? null,
    };
  });

  constructor() {
    effect(() => { this.view(); this.result(); untracked(() => this.hoverMinute.set(null)); });
  }

  pointer(event: PointerEvent) {
    const chart = this.chart(), target = event.currentTarget as SVGGraphicsElement | null;
    const bounds = (target?.ownerSVGElement ?? target)?.getBoundingClientRect();
    if (!bounds) return;
    const position = (event.clientX - bounds.left) / Math.max(bounds.width, 1) * chart.box.width;
    const minute = Math.round(chart.start + (position - chart.left) / Math.max(chart.right - chart.left, 1) * (chart.to - chart.start));
    this.hoverMinute.set(Math.min(chart.to, Math.max(chart.start, minute)));
  }
}

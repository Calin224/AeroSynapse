import { Component, computed, input, output, signal } from '@angular/core';
import { QueueHistory, runTimeline } from './queue-forecast.view';

/** Whole-run observed queue; a live run marks the current window, a finished run lets the operator drag the forecast origin. */
@Component({
  selector: 'app-queue-forecast-timeline',
  templateUrl: './queue-forecast-timeline.html',
  styleUrl: './queue-forecast-timeline.css',
})
export class QueueForecastTimeline {
  readonly history = input.required<QueueHistory>();
  readonly duration = input.required<number>();
  readonly interval = input.required<number>();
  readonly live = input(false);
  readonly latestMinute = input<number | null>(null);
  readonly windowStart = input(0);
  readonly horizon = input.required<number>();
  readonly origins = input.required<number[]>();
  readonly origin = input(0);
  readonly disabled = input(false);
  readonly state = input<'idle' | 'loading' | 'ready' | 'failed'>('idle');
  readonly originChange = output<number>();
  readonly scrub = signal<number | null>(null);
  readonly line = computed(() => runTimeline(this.history(), this.duration()));
  commit(value: number) { this.scrub.set(null); this.originChange.emit(value); }
}

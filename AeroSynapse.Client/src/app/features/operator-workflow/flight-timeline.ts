import { Component, computed, input, output } from '@angular/core';
import type { FlightTimeline, TimelineFlight } from './workflow.models';

/** The scenario's flights as a departures board on one time axis; picking a flight chooses where the surge happens. */
@Component({
  selector: 'app-flight-timeline',
  templateUrl: './flight-timeline.html',
  styleUrl: './flight-timeline.css',
})
export class FlightTimelineView {
  readonly line = input.required<FlightTimeline>();
  readonly selected = input<string | null>(null);
  readonly disabled = input(false);
  readonly pick = output<string>();
  /** Every other interval between ticks is shaded, so a row reads against the clock at a glance. */
  readonly bands = computed(() => {
    const ticks = this.line().ticks;
    return ticks
      .slice(0, -1)
      .map((tick, index) => ({ left: tick.left, width: ticks[index + 1].left - tick.left }))
      .filter((_, index) => index % 2 === 0);
  });

  /** The arrivals curve inside its own box: a peak where most passengers come, flat when they come evenly. */
  curve(flight: TimelineFlight) {
    const span = flight.arrivals!;
    if (flight.arrivalPeak === null || span.width <= 0) return 'M0,100 L0,45 L100,45 L100,100 Z';
    const peak = Math.min(100, Math.max(0, ((flight.arrivalPeak - span.left) / span.width) * 100));
    return `M0,100 C${peak / 2},100 ${peak / 2},0 ${peak},0 C${(peak + 100) / 2},0 ${(peak + 100) / 2},100 100,100 Z`;
  }

  label(flight: TimelineFlight) {
    return [
      flight.code,
      `plecare ${flight.departure}`,
      `poarta ${flight.gate}`,
      `${flight.passengers} pasageri`,
      flight.arrivalsLabel ? `sosiri ${flight.arrivalsLabel}` : '',
      flight.cancelled ? 'anulat' : '',
    ]
      .filter(Boolean)
      .join(', ');
  }
}

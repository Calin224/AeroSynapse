import { Component, input, output } from '@angular/core';
import type { FlightTimeline } from './workflow.models';

/** The scenario's flights on one time axis; picking a flight chooses where the surge happens. */
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
}

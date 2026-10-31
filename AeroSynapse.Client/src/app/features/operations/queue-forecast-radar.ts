import { DecimalPipe } from '@angular/common';
import { Component, computed, input, output } from '@angular/core';
import { QueueForecastResult } from './queue-forecast.models';
import { CheckpointOutlook, riskLabel, sparkline } from './queue-forecast.view';

/** Checkpoints ranked by forecast risk; selecting one opens its detailed chart. */
@Component({
  selector: 'app-queue-forecast-radar',
  imports: [DecimalPipe],
  templateUrl: './queue-forecast-radar.html',
  styleUrl: './queue-forecast-radar.css',
})
export class QueueForecastRadar {
  readonly outlooks = input.required<CheckpointOutlook[]>();
  readonly result = input.required<QueueForecastResult>();
  readonly names = input.required<Record<string, string>>();
  readonly selectedId = input<string | null>(null);
  readonly selected = output<string>();
  readonly sparks = computed(() => new Map(this.outlooks().map(row => [row.id, sparkline(row, this.result())])));
  readonly label = riskLabel;
  name(id: string) { return this.names()[id] ?? `Checkpoint ${id.slice(0, 8)}`; }
}

import { DecimalPipe } from '@angular/common';
import { Component, computed, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import type { HeatmapRun } from '../digital-twin/heatmap-model';
import type { ScenarioInputs } from '../operations/operations.models';
import { diagnose, WAIT_LIMIT_MINUTES } from './bottleneck-diagnosis';

@Component({
  selector: 'app-bottleneck-diagnosis',
  imports: [DecimalPipe, RouterLink],
  templateUrl: './bottleneck-diagnosis-panel.html',
  styleUrl: './bottleneck-diagnosis-panel.css',
})
export class BottleneckDiagnosisPanel {
  readonly heatmap = input.required<HeatmapRun | null>();
  readonly inputs = input.required<ScenarioInputs>();
  readonly scenarioId = input.required<string>();
  readonly runId = input.required<string>();
  readonly limit = WAIT_LIMIT_MINUTES;
  readonly diagnosis = computed(() => diagnose(this.heatmap(), this.inputs()));
  readonly spareLabels = computed(() => this.diagnosis().spare.map((row) => row.label));

  /** "CP-1", "CP-1 și CP-2", "CP-1, CP-2 și CP-3". */
  names(labels: string[]) {
    return labels.length > 1
      ? `${labels.slice(0, -1).join(', ')} și ${labels.at(-1)}`
      : (labels[0] ?? '');
  }
  donors(rows: { label: string; count: number }[]) {
    return this.names(rows.map((row) => `${row.label} (${row.count})`));
  }

  /** Wall-clock time of a scenario minute, in the scenario's own time zone. */
  time(minute: number) {
    const inputs = this.inputs();
    try {
      return new Intl.DateTimeFormat('ro-RO', {
        timeZone: inputs.timeZone,
        hour: '2-digit',
        minute: '2-digit',
        hourCycle: 'h23',
      }).format(new Date(Date.parse(inputs.startsAtUtc) + minute * 60000));
    } catch {
      return `min ${minute}`;
    }
  }
  unstaffed(waitMinutes: number) {
    return waitMinutes === Infinity;
  }
  operators(count: number) {
    return count === 1 ? '1 operator' : `${count} operatori`;
  }
}

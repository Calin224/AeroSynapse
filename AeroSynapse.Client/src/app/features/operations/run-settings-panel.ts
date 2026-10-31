import {
  Component,
  DestroyRef,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { finalize } from 'rxjs';
import { ApiClient } from '../../core/http/api-client';
import { ScenarioDetails } from './operations.models';

@Component({
  selector: 'app-run-settings-panel',
  imports: [FormsModule],
  templateUrl: './run-settings-panel.html',
  styles: `
    :host {
      display: block;
      margin-bottom: 16px;
    }
    .toolbar {
      align-items: end;
    }
    label {
      display: grid;
      gap: 6px;
      width: min(100%, 440px);
    }
    .interval-control {
      display: flex;
      align-items: center;
      gap: 12px;
    }
    input[type='range'] {
      flex: 1;
      min-width: 0;
    }
    output {
      white-space: nowrap;
      font-variant-numeric: tabular-nums;
    }
  `,
})
export class RunSettingsPanel {
  private readonly api = inject(ApiClient);
  private readonly destroy = inject(DestroyRef);
  private scenarioId?: string;
  readonly scenario = input.required<ScenarioDetails>();
  readonly disabled = input(false);
  readonly changed = output<ScenarioDetails>();
  readonly working = output<boolean>();
  readonly saving = signal(false);
  readonly message = signal('');
  interval = 1;

  constructor() {
    effect(() => {
      const scenario = this.scenario();
      untracked(() => {
        this.interval = scenario.inputs.snapshotIntervalMinutes ?? 1;
        if (this.scenarioId !== scenario.scenario.id) this.message.set('');
        this.scenarioId = scenario.scenario.id;
      });
    });
  }

  save() {
    if (this.disabled() || this.saving() || ![1, 2, 3, 4, 5].includes(this.interval)) return;
    const scenario = this.scenario().scenario;
    this.saving.set(true);
    this.working.emit(true);
    this.message.set('');
    this.api
      .put<ScenarioDetails>(`scenarios/${scenario.id}/kpis/settings`, {
        revision: scenario.revision,
        snapshotIntervalMinutes: this.interval,
      })
      .pipe(
        finalize(() => {
          this.saving.set(false);
          this.working.emit(false);
        }),
        takeUntilDestroyed(this.destroy),
      )
      .subscribe({
        next: (value) => {
          this.changed.emit(value);
          this.message.set('Intervalul a fost salvat pentru următoarea rulare.');
        },
        error: (error: unknown) => {
          this.message.set(
            error instanceof Error ? error.message : 'Intervalul nu a putut fi salvat.',
          );
        },
      });
  }
}

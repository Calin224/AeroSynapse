import { Component, computed, inject, signal } from '@angular/core';
import { ButtonModule } from 'primeng/button';
import { DataState } from '../../shared/ui/data-state';
import { DigitalTwin } from '../digital-twin/digital-twin';
import { ScenarioStore } from './scenario.store';
@Component({
  selector: 'app-scenario',
  imports: [ButtonModule, DataState, DigitalTwin],
  providers: [ScenarioStore],
  templateUrl: './scenario.html',
  styleUrl: './scenario.css',
})
export class Scenario {
  protected readonly resource = inject(ScenarioStore).resource;
  protected readonly selectedId = signal<string | null>(null);
  protected readonly selected = computed(() => {
    const state = this.resource.state();
    return state.status === 'ready'
      ? (state.data.find((item) => item.id === this.selectedId()) ?? state.data[0])
      : undefined;
  });
}

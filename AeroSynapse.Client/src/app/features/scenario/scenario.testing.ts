import { Component, forwardRef, input, output, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Subject } from 'rxjs';
import { vi } from 'vitest';
import { OperationsRun, OperationsStream } from '../../core/http/operations-stream';
import { ControlTowerStore } from '../control-tower/control-tower.store';
import { TWIN_RENDERER_FACTORY } from '../digital-twin/digital-twin';
import { EventsPanel } from '../operations/events-panel';
import { FlightSchedulePanel } from '../operations/flight-schedule-panel';
import { NavigationPanel } from '../operations/navigation-panel';
import { RunDetails, ScenarioDetails } from '../operations/operations.models';
import { PassengerGenerationPanel } from '../operations/passenger-generation-panel';
import { QueueForecastPanel } from '../operations/queue-forecast-panel';
import { ResourcePanel } from '../operations/resource-panel';
import { RunSettingsPanel } from '../operations/run-settings-panel';
import { StaffingPanel } from '../operations/staffing-panel';
import { Scenario } from './scenario';
import { SimulationExecution, SimulationPanel } from './simulation-panel';
import { AiOptimization } from './ai-optimization';

const editorImports = [
  FlightSchedulePanel,
  PassengerGenerationPanel,
  NavigationPanel,
  RunSettingsPanel,
  EventsPanel,
  StaffingPanel,
  ResourcePanel,
  QueueForecastPanel,
];

@Component({
  selector:
    'app-flight-schedule-panel, app-passenger-generation-panel, app-navigation-panel, app-run-settings-panel, app-events-panel, app-staffing-panel, app-resource-panel, app-queue-forecast-panel',
  template: '',
})
class PreparationPanelStub {
  readonly scenario = input<ScenarioDetails | null>(null);
  readonly run = input<RunDetails | null>(null);
  readonly progress = input<OperationsRun | null>(null);
  readonly active = input(false);
  readonly disabled = input(false);
  readonly changed = output<ScenarioDetails>();
  readonly working = output<boolean>();
  readonly goTo = output<'schedule' | 'passengers'>();
}

@Component({
  selector: 'app-simulation-panel',
  template: '',
  providers: [{ provide: SimulationPanel, useExisting: forwardRef(() => SimulationPanelStub) }],
})
export class SimulationPanelStub {
  readonly scenarioId = input<string | null>(null);
  readonly scenario = input<{ id: string; name: string; revision: number } | null>(null);
  readonly disabled = input(false);
  readonly accepted = output<SimulationExecution>();
  readonly finished = output<string>();
  readonly scenarioSelected = output<string>();
  readonly working = output<boolean>();
  readonly execution = signal<SimulationExecution | null>(null);
  readonly error = signal('');
  readonly command = signal<unknown | null>(null);
  readonly submitting = signal(false);
  readonly scenarios = { reload: vi.fn() };
  readonly start = vi.fn();
  readonly resume = vi.fn();
}

/** The AI panel fetches and simulates on its own; page tests cover what surrounds it. */
@Component({ selector: 'app-ai-optimization', template: '' })
class AiOptimizationStub {
  readonly runId = input('');
  readonly stale = input(false);
  readonly passengers = input(0);
  readonly durationMinutes = input(0);
  readonly applied = output<void>();
}

/** A renderer factory that fails, so the twin falls back to the 2D map. */
export const noWebGl = {
  provide: TWIN_RENDERER_FACTORY,
  useValue: () => {
    throw new Error('WebGL unavailable');
  },
};

/** Stubs the Scenario workspace's editor panels, launcher and live stream. Call after configureTestingModule. */
export function stubScenarioPanels(status = 'idle') {
  TestBed.overrideComponent(Scenario, {
    remove: { imports: [SimulationPanel, AiOptimization, ...editorImports] },
    add: { imports: [SimulationPanelStub, PreparationPanelStub, AiOptimizationStub] },
  });
  TestBed.overrideComponent(Scenario, {
    set: {
      providers: [
        ControlTowerStore,
        {
          provide: OperationsStream,
          useValue: {
            connect: vi.fn(),
            state: signal(null),
            updates: new Subject(),
            status: signal(status),
            error: signal(''),
          },
        },
      ],
    },
  });
}

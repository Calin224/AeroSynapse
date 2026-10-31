import { DecimalPipe, NgTemplateOutlet } from '@angular/common';
import {
  Component,
  computed,
  DestroyRef,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { finalize, Subscription, timeout } from 'rxjs';
import { ApiClient } from '../../core/http/api-client';
import { OPERATION_REQUEST_TIMEOUT_MS, operationError } from '../../core/http/operation-request';
import { ScenarioDetails } from './operations.models';
import { StaffingEvaluationPanel } from './staffing-evaluation-panel';
import type { EvaluationStage } from '../operator-workflow/workflow.models';
import type { StaffingEvaluationDetails } from './staffing-evaluation.models';

interface Constraint {
  checkpointId: string;
  minimumStaff: number;
  maximumStaff: number;
  physicalLimit: number;
  requiredOpen: boolean;
  closed: boolean;
  changeCost: number;
}
interface Checkpoint extends Constraint {
  terminalId: string;
  currentStaff: number;
  demandMilli: number;
  capacityMilli: number;
}
interface Pool {
  terminalId: string;
  availableStaff: number;
}
export interface StaffingProposal {
  revision: number;
  availableStaff: number;
  timeLimitSeconds: number;
  checkpoints: Constraint[];
}
export interface StaffingSettings {
  scenarioId: string;
  revision: number;
  passengerProfilesActive: boolean;
  availableStaff: number;
  staffPools: Pool[];
  checkpoints: { id: string; terminalId: string; name: string; currentStaff: number }[];
}
export interface StaffingResult {
  status: 'optimal' | 'feasible' | 'infeasible' | 'timeout' | 'invalid_model';
  solverStatus: string;
  timedOut: boolean;
  score: number | null;
  bestBound: number | null;
  usedStaff: number | null;
  unassignedStaff: number | null;
  assignments: {
    checkpointId: string;
    staffCount: number;
    estimatedCapacityMilli: number;
    unmetDemandMilli: number;
    changePenalty: number;
  }[];
  constraints: {
    scenarioId: string;
    scenarioRevision: number;
    availableStaff: number;
    timeLimitSeconds: number;
    staffPools: Pool[];
    checkpoints: Checkpoint[];
  };
  objective: string;
  capacityScale: number;
  wallTimeSeconds: number;
}
interface Row extends Omit<Constraint, 'physicalLimit'> {
  name: string;
  currentStaff: number;
  physicalLimit: number | null;
}

@Component({
  selector: 'app-staffing-panel',
  imports: [FormsModule, DecimalPipe, NgTemplateOutlet, StaffingEvaluationPanel],
  templateUrl: './staffing-panel.html',
  styles: `
    :host {
      display: block;
    }
    input[type='number'] {
      width: 6rem;
    }
    th,
    td {
      padding: 0.5rem;
      text-align: left;
    }
    .visually-hidden {
      position: absolute;
      width: 1px;
      height: 1px;
      overflow: hidden;
      clip-path: inset(50%);
      white-space: nowrap;
    }
    .situation p {
      margin: 0 0 0.6rem;
    }
    .chips {
      display: flex;
      flex-wrap: wrap;
      gap: 0.5rem;
      margin: 0 0 0.75rem;
      padding: 0;
      list-style: none;
    }
    .chips li {
      padding: 0.35rem 0.75rem;
      border-radius: var(--radius);
      background: var(--accent-soft);
      border: 1px solid var(--accent-line);
      font-size: 0.85rem;
      font-weight: 600;
    }
    .chips li.empty {
      background: var(--surface-raised);
      border-color: var(--border);
      color: var(--text-muted);
      font-weight: 500;
    }
    .advanced summary {
      cursor: pointer;
      min-height: 44px;
      padding: 0.65rem 0;
      font-weight: 600;
    }
    .table-wrap {
      overflow-x: auto;
    }
  `,
})
export class StaffingPanel {
  readonly scenario = input.required<ScenarioDetails>();
  readonly active = input(false);
  readonly disabled = input(false);
  readonly workflowStage = input<EvaluationStage | null>(null);
  readonly readOnly = input(false);
  readonly evaluated = output<StaffingEvaluationDetails | null>();
  readonly changed = output<ScenarioDetails>();
  readonly working = output<boolean>();
  readonly reviewing = signal(false);
  readonly settings = signal<StaffingSettings | null>(null);
  readonly loading = signal(false);
  readonly busy = signal(false);
  readonly result = signal<StaffingResult | null>(null);
  readonly message = signal('');
  rows: Row[] = [];
  availableStaff = 0;
  timeLimitSeconds = 5;
  private readonly api = inject(ApiClient);
  private pending?: Subscription;
  private readonly scenarioContext = computed(
    () => `${this.scenario().scenario.id}/${this.scenario().scenario.revision}`,
  );
  private context = '';
  private approvedContext = '';

  constructor() {
    inject(DestroyRef).onDestroy(() => this.pending?.unsubscribe());
    effect((onCleanup) => {
      const context = this.scenarioContext();
      const active = this.active();
      untracked(() => {
        if (context !== this.context) {
          this.pending?.unsubscribe();
          this.context = context;
          if (context !== this.approvedContext) {
            this.settings.set(null);
            this.rows = [];
          }
          this.approvedContext = '';
          this.result.set(null);
          this.message.set('');
        }
        if (active && !this.settings()) this.loadSettings();
      });
      onCleanup(() => this.pending?.unsubscribe());
    });
  }

  loadSettings() {
    if (!this.active() || this.loading() || this.busy()) return;
    const scenario = this.scenario().scenario;
    this.message.set('');
    this.loading.set(true);
    this.pending = this.api
      .get<StaffingSettings>(`scenarios/${scenario.id}/staffing/settings`)
      .pipe(
        timeout(OPERATION_REQUEST_TIMEOUT_MS),
        finalize(() => this.loading.set(false)),
      )
      .subscribe({
        next: (settings) => {
          if (settings.scenarioId !== scenario.id || settings.revision !== scenario.revision) {
            this.message.set(
              'Scenariul s-a modificat. Selectează-l din nou din listă pentru a încărca revizia curentă.',
            );
            return;
          }
          this.settings.set(settings);
          this.availableStaff = settings.availableStaff;
          this.rows = settings.checkpoints.map((item) => ({
            checkpointId: item.id,
            name: item.name,
            currentStaff: item.currentStaff,
            minimumStaff: item.currentStaff ? 1 : 0,
            maximumStaff: Math.min(10000, settings.availableStaff),
            // The guided workflow assumes no post limit below the staff budget; the advanced
            // section shows the assumption and lets the operator lower it.
            physicalLimit: this.workflowStage() ? settings.availableStaff : null,
            requiredOpen: item.currentStaff > 0,
            closed: false,
            changeCost: 10,
          }));
        },
        error: (error) => this.report(error),
      });
  }

  invalidate() {
    this.result.set(null);
    this.message.set('');
  }
  blocked() {
    return this.disabled() || this.loading() || this.busy() || this.reviewing();
  }
  approved(scenario: ScenarioDetails) {
    this.approvedContext = `${scenario.scenario.id}/${scenario.scenario.revision}`;
    const count = (id: string) =>
      scenario.inputs.staffAllocations.filter((item) => item.checkpointId === id).length;
    this.settings.update((settings) =>
      settings
        ? {
            ...settings,
            revision: scenario.scenario.revision,
            checkpoints: settings.checkpoints.map((item) => ({
              ...item,
              currentStaff: count(item.id),
            })),
          }
        : null,
    );
    this.rows = this.rows.map((row) => ({ ...row, currentStaff: count(row.checkpointId) }));
    this.changed.emit(scenario);
  }
  proposal(): StaffingProposal | null {
    const settings = this.settings();
    if (!settings?.passengerProfilesActive) return null;
    const integer = (value: number | null, max: number) =>
      value !== null && Number.isInteger(value) && value >= 0 && value <= max;
    if (
      !integer(this.availableStaff, settings.availableStaff) ||
      !Number.isFinite(this.timeLimitSeconds) ||
      this.timeLimitSeconds < 0 ||
      this.timeLimitSeconds > 30 ||
      this.rows.some(
        (row) =>
          !integer(row.minimumStaff, 10000) ||
          !integer(row.maximumStaff, 10000) ||
          !integer(row.physicalLimit, 10000) ||
          !integer(row.changeCost, 1000000),
      )
    ) {
      return null;
    }
    return {
      revision: settings.revision,
      availableStaff: this.availableStaff,
      timeLimitSeconds: this.timeLimitSeconds,
      checkpoints: this.rows.map(({ name, currentStaff, ...constraint }) => ({
        ...constraint,
        physicalLimit: constraint.physicalLimit!,
      })),
    };
  }
  propose() {
    const settings = this.settings();
    if (this.readOnly() || this.blocked() || !settings?.passengerProfilesActive) return;
    const proposal = this.proposal();
    if (!proposal) {
      this.message.set(
        'Completează limita fizică pentru fiecare control. Numere întregi 0–10000, cost 0–1000000, timeout 0–30 secunde și buget în limita personalului disponibil.',
      );
      return;
    }
    this.invalidate();
    this.busy.set(true);
    this.pending = this.api
      .post<StaffingResult>(`scenarios/${settings.scenarioId}/staffing/proposals`, proposal)
      .pipe(
        timeout(OPERATION_REQUEST_TIMEOUT_MS),
        finalize(() => this.busy.set(false)),
      )
      .subscribe({
        next: (result) => this.result.set(result),
        error: (error) => this.report(error),
      });
  }
  name(id: string) {
    return this.rows.find((row) => row.checkpointId === id)?.name ?? id;
  }
  status(value: StaffingResult) {
    return {
      optimal: 'Propunere optimă demonstrată pentru modelul estimat.',
      feasible: 'Propunere fezabilă. Limita de timp a fost atinsă fără dovada optimalității.',
      infeasible: 'Infeasible: nu există o configurație care să respecte toate restricțiile.',
      timeout:
        'Timeout: limita de timp a fost atinsă fără o soluție. Nu există configurație sau scor.',
      invalid_model: 'Model invalid: solverul nu poate produce o propunere. Verifică restricțiile.',
    }[value.status];
  }
  private report(error: unknown) {
    this.message.set(operationError(error));
  }
}

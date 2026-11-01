import { DecimalPipe } from '@angular/common';
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
  imports: [FormsModule, DecimalPipe, StaffingEvaluationPanel],
  templateUrl: './staffing-panel.html',
  styleUrl: './setup-panels.css',
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
            // Assume no post limit below the staff budget; "Setări avansate" shows the
            // assumption and lets the operator lower it.
            physicalLimit: settings.availableStaff,
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
        `Verifică Setări avansate: numere întregi 0–10000 pentru fiecare control, cost 0–1000000, timp de calcul 0–30 secunde și cel mult ${settings.availableStaff} operatori.`,
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
  current(id: string) {
    return this.result()?.constraints.checkpoints.find((item) => item.checkpointId === id)
      ?.currentStaff;
  }
  status(value: StaffingResult) {
    return {
      optimal: 'Am găsit cea mai bună împărțire pentru cererea estimată.',
      feasible:
        'Am găsit o împărțire bună, dar timpul de calcul s-a terminat înainte să confirmăm că este cea mai bună.',
      infeasible:
        'Nu există o împărțire care să respecte toate limitele. Verifică Setări avansate.',
      timeout:
        'Timpul de calcul s-a terminat fără o soluție. Mărește timpul maxim de calcul din Setări avansate.',
      invalid_model: 'Limitele introduse nu pot fi folosite. Verifică Setări avansate.',
    }[value.status];
  }
  private report(error: unknown) {
    this.message.set(operationError(error));
  }
}

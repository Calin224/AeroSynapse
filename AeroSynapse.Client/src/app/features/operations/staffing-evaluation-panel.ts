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
import { NgTemplateOutlet } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { SessionState } from '../../core/auth/session-state';
import {
  catchError,
  EMPTY,
  exhaustMap,
  finalize,
  Subscription,
  takeWhile,
  timer,
  timeout,
} from 'rxjs';
import { ApiClient } from '../../core/http/api-client';
import { ApiError } from '../../core/http/api-error.interceptor';
import { ResponseFeedback } from '../../core/http/response-feedback';
import { OPERATION_REQUEST_TIMEOUT_MS, operationError } from '../../core/http/operation-request';
import { evaluationIdentityProblem } from './staffing-evaluation.guards';
import type { EvaluationStage } from '../operator-workflow/workflow.models';
import type { BatchSummary } from '../batches/batches.models';
import type { ScenarioDetails } from './operations.models';
import type { StaffingProposal } from './staffing-panel';
import type {
  ShiftRules,
  StaffingAcceptancePolicy,
  StaffingEvaluationDetails,
} from './staffing-evaluation.models';
import { ParetoPanel } from './pareto-panel';
import { ParetoOptions, validParetoOptions } from './pareto.models';
import { RecommendationReviewPanel } from './recommendation-review-panel';
import { validRunId } from '../operational-report/report.models';

const ASSESSMENTS: Record<string, string> = {
  baseline: 'Baseline',
  improved: 'Îmbunătățire operațională',
  tradeoff: 'Compromis cu degradare acceptată',
  unchanged: 'Fără îmbunătățire simulată',
  rejected: 'Respins',
  unassessable: 'Date insuficiente',
  failed: 'Rulare eșuată',
  pending: 'În așteptare',
};
const REASONS: Record<string, string> = {
  mean_wait_degradation: 'Creșterea waiting-ului depășește toleranța.',
  p95_degradation: 'Creșterea P95 depășește toleranța.',
  boarding_risk_degradation: 'Riscul de boarding depășește toleranța.',
  unprocessed_degradation: 'Rămân prea mulți pasageri neprocesați.',
  reallocation_cost_limit: 'Costul realocării depășește limita.',
  insufficient_observed_metrics: 'Nu există suficiente observații pentru comparație.',
  accepted_degradation:
    'Există o degradare în toleranțele acceptate; aceasta nu este o îmbunătățire fără compromisuri.',
  no_simulated_improvement: 'Metricile simulate nu arată o îmbunătățire.',
  baseline_metrics_unavailable: 'Lipsesc metricile baseline.',
  kpi_unavailable:
    'Lipsesc sau sunt inconsistente snapshot-urile KPI finale și observațiile individuale ale pasagerilor.',
  checkpoint_metrics_missing: 'Lipsesc observații pentru unele controale.',
  insufficient_checkpoint_wait_metrics:
    'Lipsesc observațiile de waiting comparabile pentru acest control.',
};
/** Shift plans need a day long enough for real shifts (StaffShiftScenarios.MinimumDurationMinutes). */
export const SHIFT_MINIMUM_MINUTES = 120;
/** Shift rules as a person states them: hours and minutes. */
export interface ShiftForm {
  minShiftHours: number;
  maxShiftHours: number;
  breakAfterMinutes: number;
  transferMinutes: number;
  maxMovesPerHour: number;
  targetUtilizationPercent: number;
}
export const DEFAULT_SHIFTS: ShiftForm = {
  minShiftHours: 4,
  maxShiftHours: 6,
  breakAfterMinutes: 150,
  transferMinutes: 5,
  maxMovesPerHour: 2,
  // Random arrivals queue well before an operator is fully busy: about 9 minutes at 90% for one.
  targetUtilizationPercent: 75,
};
/** The planner's 15-minute-slot rules, or null when a value is outside its range. */
export function shiftRules(form: ShiftForm): ShiftRules | null {
  const slots = (hours: number) => hours * 4;
  const whole = (value: number, min: number, max: number) =>
    Number.isInteger(value) && value >= min && value <= max;
  const rules: ShiftRules = {
    minShiftSlots: slots(form.minShiftHours),
    maxShiftSlots: slots(form.maxShiftHours),
    breakAfterSlots: form.breakAfterMinutes / 15,
    transferMinutes: form.transferMinutes,
    maxMovesPerHour: form.maxMovesPerHour,
    // A queued passenger for one slot weighs 1000; a person on duty for a slot, one queued passenger.
    queueWeight: 1,
    staffSlotCost: 1000,
    moveCost: 3000,
    targetUtilizationPercent: form.targetUtilizationPercent,
  };
  return whole(rules.minShiftSlots, 1, 96) &&
    whole(rules.maxShiftSlots, rules.minShiftSlots, 96) &&
    whole(rules.breakAfterSlots, 1, 96) &&
    whole(rules.transferMinutes, 0, 14) &&
    whole(rules.maxMovesPerHour, 0, 100) &&
    whole(rules.targetUtilizationPercent, 1, 100)
    ? rules
    : null;
}
export function assessmentLabel(status: string) {
  return ASSESSMENTS[status] ?? status;
}
/** Reason codes may carry a checkpoint (`code:checkpointId`), named from the baseline configuration. */
export function reasonLabel(
  value: string,
  configuration: readonly { checkpointId: string; name: string }[],
) {
  const [code, checkpointId] = value.split(':');
  const text = REASONS[code];
  const name =
    configuration.find((item) => item.checkpointId === checkpointId)?.name ?? checkpointId;
  return text ? (checkpointId ? `${name}: ${text}` : text) : value;
}

@Component({
  selector: 'app-staffing-evaluation-panel',
  imports: [FormsModule, NgTemplateOutlet, ParetoPanel, RecommendationReviewPanel],
  templateUrl: './staffing-evaluation-panel.html',
  styles: `
    :host {
      display: block;
      margin-top: 1.5rem;
    }
    /* Guided workflow: one call to action, everything else folded. */
    .guided h3 {
      margin: 0 0 0.5rem;
      font-size: 1rem;
    }
    .guided details {
      margin: 0.5rem 0;
    }
    .guided summary {
      cursor: pointer;
      min-height: 44px;
      padding: 0.65rem 0;
      font-weight: 600;
    }
    .guided progress {
      width: 100%;
      height: 0.6rem;
      accent-color: var(--accent);
    }
    .start {
      display: inline-flex;
      align-items: center;
      gap: 0.6rem;
      min-height: 52px;
      padding-inline: 1.5rem;
      font-size: 1.05rem;
    }
    .evaluation-controls {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(min(100%, 12rem), 1fr));
      align-items: end;
      gap: 0.75rem;
    }
    .evaluation-controls > label {
      min-width: 0;
    }
    label {
      display: flex;
      flex-direction: column;
      gap: 0.3rem;
    }
    th,
    td {
      padding: 0.5rem;
      text-align: left;
    }
    .actions {
      display: flex;
      gap: 0.5rem;
      flex-wrap: wrap;
      margin: 0.75rem 0;
    }
    .plan-choice {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(min(100%, 16rem), 1fr));
      gap: 0.6rem;
      margin: 0.5rem 0;
      padding: 0;
      border: 0;
    }
    .plan-choice legend {
      margin-bottom: 0.4rem;
      font-weight: 600;
    }
    .choice {
      flex-direction: row;
      align-items: flex-start;
      gap: 0.6rem;
      margin: 0;
      padding: 0.8rem 0.9rem;
      border: 1px solid var(--border);
      border-radius: var(--radius);
      background: var(--panel);
      cursor: pointer;
    }
    .choice.selected {
      border-color: var(--accent);
      background: var(--accent-soft);
    }
    .choice input {
      margin-top: 0.2rem;
    }
    .choice span {
      display: grid;
      gap: 0.2rem;
    }
    .choice small {
      color: var(--text-muted);
    }
  `,
})
export class StaffingEvaluationPanel {
  private readonly session = inject(SessionState);
  readonly online = inject(ResponseFeedback).online;
  readonly scenario = input.required<ScenarioDetails>();
  readonly proposal = input<StaffingProposal | null>(null);
  readonly active = input(false);
  readonly disabled = input(false);
  readonly workflowStage = input<EvaluationStage | null>(null);
  readonly readOnly = input(false);
  readonly evaluated = output<StaffingEvaluationDetails | null>();
  readonly changed = output<ScenarioDetails>();
  readonly working = output<boolean>();
  readonly busy = signal(false);
  readonly message = signal('');
  readonly evaluation = signal<StaffingEvaluationDetails | null>(null);
  readonly history = signal<BatchSummary[]>([]);
  name = 'Evaluare personal DEMO';
  /** Decision flow only: one staffing for the whole day, or shifts per 15-minute slot. */
  plan: 'fixed' | 'shifts' = 'fixed';
  shiftForm: ShiftForm = { ...DEFAULT_SHIFTS };
  readonly shiftsAllowed = computed(
    () => this.scenario().inputs.durationMinutes >= SHIFT_MINIMUM_MINUTES,
  );
  candidateCount = 3;
  timeLimitSeconds = 120;
  multiObjectiveEnabled = false;
  multiObjective: ParetoOptions = {
    weights: { time: 40, cost: 40, risk: 20 },
    normalization: { timeMinutes: 10, costPoints: 1000, riskFraction: 0.1 },
    operationalBudget: 1000000,
    staffMinuteCost: 1,
    maxMeanWaitMinutes: 10080,
    maxP95WaitMinutes: 10080,
    maxBoardingRisk: 1,
  };
  // Moving people between controls always slows the ones they leave, so a slower control and up to
  // a minute more P95 are accepted as a tradeoff when passengers overall wait less.
  policy: StaffingAcceptancePolicy = {
    maxMeanWaitIncreaseMinutes: 0,
    maxP95IncreaseMinutes: 1,
    maxBoardingRiskIncrease: 0,
    maxUnprocessedIncrease: 0,
    maxReallocationCost: 100000000000000,
    checkpointTradeoffs: true,
  };
  /** The server's default and ceiling for the reallocation cost: no cost limit. */
  readonly unlimitedCost = 100000000000000;
  private readonly api = inject(ApiClient);
  private readonly numbers = new Intl.NumberFormat('ro-RO', { maximumFractionDigits: 4 });
  private request?: Subscription;
  private polling?: Subscription;
  private historyRequest?: Subscription;
  private readonly scenarioContext = computed(
    () => `${this.scenario().scenario.id}/${this.scenario().scenario.revision}`,
  );
  private context = '';
  private approvedContext = '';
  private command?: { identity: string; id: string; body: object };
  readonly pendingCommand = signal(false);
  readonly progress = computed(() => {
    const rows = this.evaluation()?.batch.replications ?? [];
    return {
      total: rows.length,
      finished: rows.filter((row) => !['pending', 'planned', 'running'].includes(row.runStatus))
        .length,
    };
  });

  constructor() {
    const stop = () => {
      this.request?.unsubscribe();
      this.polling?.unsubscribe();
      this.historyRequest?.unsubscribe();
    };
    inject(DestroyRef).onDestroy(stop);
    effect((onCleanup) => {
      const context = this.scenarioContext();
      const active = this.active();
      untracked(() => {
        if (this.context !== context) {
          stop();
          this.context = context;
          if (context !== this.approvedContext) {
            this.evaluation.set(null);
            this.evaluated.emit(null);
          }
          this.approvedContext = '';
          this.history.set([]);
          this.message.set('');
          this.command = undefined;
          this.pendingCommand.set(false);
          this.restoreCommand();
        }
        if (active) {
          this.loadHistory();
          this.watch();
        }
      });
      onCleanup(stop);
    });
  }

  running() {
    return ['pending', 'running'].includes(this.evaluation()?.batch.batch.status ?? '');
  }
  approved(scenario: ScenarioDetails) {
    this.approvedContext = `${scenario.scenario.id}/${scenario.scenario.revision}`;
    this.changed.emit(scenario);
  }
  start() {
    const proposal = this.proposal();
    if (
      !this.online() ||
      this.readOnly() ||
      !this.active() ||
      this.disabled() ||
      this.busy() ||
      this.running() ||
      !proposal ||
      (this.workflowStage() && this.workflowStage() !== 'optimize')
    )
      return;
    const wantsShifts = !!this.workflowStage() && this.plan === 'shifts';
    const shifts = wantsShifts && this.shiftsAllowed() ? shiftRules(this.shiftForm) : null;
    if (wantsShifts && !shifts) {
      this.message.set(
        this.shiftsAllowed()
          ? 'Reguli pentru ture: tura între 15 minute și 24 de ore (minima cel mult cât maxima), pauza la multipli de 15 minute, mutarea 0–14 minute și 0–100 de mutări pe oră.'
          : 'Planul pe ture are nevoie de un scenariu de cel puțin 2 ore.',
      );
      return;
    }
    if (
      !shifts &&
      this.multiObjectiveEnabled &&
      (!validParetoOptions(this.multiObjective) || this.candidateCount < 2)
    ) {
      this.message.set(
        'Multi-obiectiv: configurează 2–4 candidați, ponderi întregi 0–1000 cu sumă pozitivă, referințe pozitive și limite valide; buget/Cref ≤ 1000000.',
      );
      return;
    }
    const finite = (value: number, max: number) =>
      Number.isFinite(value) && value >= 0 && value <= max;
    if (
      !this.name.trim() ||
      this.name.length > 160 ||
      this.name.includes('\0') ||
      !Number.isInteger(this.candidateCount) ||
      this.candidateCount < 1 ||
      this.candidateCount > 4 ||
      !Number.isInteger(this.timeLimitSeconds) ||
      this.timeLimitSeconds < 1 ||
      this.timeLimitSeconds > 3600 ||
      !finite(this.policy.maxMeanWaitIncreaseMinutes, 10080) ||
      !finite(this.policy.maxP95IncreaseMinutes, 10080) ||
      !finite(this.policy.maxBoardingRiskIncrease, 1) ||
      !Number.isInteger(this.policy.maxUnprocessedIncrease) ||
      !finite(this.policy.maxUnprocessedIncrease, 100000) ||
      !Number.isInteger(this.policy.maxReallocationCost) ||
      !finite(this.policy.maxReallocationCost, this.unlimitedCost)
    ) {
      this.message.set(
        'Introdu un nume, 1–4 candidați, timeout întreg 1–3600 s și toleranțe nenegative în limitele formularului.',
      );
      return;
    }
    const value = {
      name: this.name.trim(),
      proposal,
      candidateCount: this.candidateCount,
      policy: { ...this.policy },
      timeLimitSeconds: this.timeLimitSeconds,
      // Shift plans are compared with the best fixed plan, without a multi-objective search.
      ...(shifts
        ? { shifts }
        : this.multiObjectiveEnabled
          ? { multiObjective: structuredClone(this.multiObjective) }
          : {}),
    };
    const identity = JSON.stringify(value);
    if (this.command && this.command.identity !== identity) {
      this.message.set(
        'O comandă are rezultatul neconfirmat. Reîncearcă aceeași comandă înainte de a modifica politica sau restricțiile.',
      );
      return;
    }
    if (!this.command) {
      const id = crypto.randomUUID();
      this.command = { identity, id, body: structuredClone({ commandId: id, ...value }) };
      this.persistCommand();
    }
    this.retry();
  }
  retry() {
    if (this.readOnly() || !this.command || !this.active() || this.disabled() || this.busy())
      return;
    const command = this.command;
    this.polling?.unsubscribe();
    this.evaluation.set(null);
    this.evaluated.emit(null);
    this.message.set('');
    this.setBusy(true);
    this.pendingCommand.set(true);
    this.request = this.api
      .post<StaffingEvaluationDetails>(
        `scenarios/${this.scenario().scenario.id}/staffing/evaluations`,
        command.body,
      )
      .pipe(
        timeout(OPERATION_REQUEST_TIMEOUT_MS),
        finalize(() => this.setBusy(false)),
      )
      .subscribe({
        next: (evaluation) => {
          if (
            evaluation.planning.request.constraints.scenarioRevision !==
            (command.body as { proposal: StaffingProposal }).proposal.revision
          ) {
            this.message.set('Confirmarea evaluării folosește altă revizie decât comanda.');
            return;
          }
          if (!this.receive(evaluation, command.id)) return;
          this.clearCommand();
          this.loadHistory();
          this.watch();
        },
        error: (error) => {
          if (error instanceof ApiError && [400, 404, 409, 422].includes(error.status))
            this.clearCommand();
          this.report(error);
        },
      });
  }
  loadHistory() {
    if (!this.active()) return;
    this.historyRequest?.unsubscribe();
    this.historyRequest = this.api
      .get<BatchSummary[]>(`scenarios/${this.scenario().scenario.id}/staffing/evaluations`)
      .pipe(timeout(OPERATION_REQUEST_TIMEOUT_MS))
      .subscribe({
        next: (values) => this.history.set(values),
        error: (error) => this.report(error),
      });
  }
  load(id: string) {
    if (!this.active() || this.busy()) return;
    if (this.command && id !== this.command.id) {
      this.message.set('Reverifică mai întâi comanda neconfirmată.');
      return;
    }
    this.polling?.unsubscribe();
    this.message.set('');
    this.setBusy(true);
    if (id !== this.evaluation()?.batch.batch.id) {
      this.evaluation.set(null);
      this.evaluated.emit(null);
    }
    this.request = this.api
      .get<StaffingEvaluationDetails>(`staffing-evaluations/${id}`)
      .pipe(
        timeout(OPERATION_REQUEST_TIMEOUT_MS),
        finalize(() => this.setBusy(false)),
      )
      .subscribe({
        next: (value) => {
          if (this.receive(value, id)) {
            this.clearCommand();
            this.watch();
          }
        },
        error: (error) => {
          this.report(error);
          this.watch();
        },
      });
  }
  cancel() {
    const id = this.evaluation()?.batch.batch.id;
    if (this.readOnly() || !id || !this.running() || this.busy()) return;
    this.setBusy(true);
    this.request = this.api
      .post(`simulation-batches/${id}/cancel`)
      .pipe(
        timeout(OPERATION_REQUEST_TIMEOUT_MS),
        finalize(() => this.setBusy(false)),
      )
      .subscribe({ next: () => this.watch(), error: (error) => this.report(error) });
  }
  private watch() {
    this.polling?.unsubscribe();
    const id = this.evaluation()?.batch.batch.id;
    if (!this.active() || !id || !this.running()) return;
    this.polling = timer(1000, 1500)
      .pipe(
        exhaustMap(() =>
          this.api.get<StaffingEvaluationDetails>(`staffing-evaluations/${id}`).pipe(
            timeout(OPERATION_REQUEST_TIMEOUT_MS),
            catchError((error) => {
              this.report(error);
              return EMPTY;
            }),
          ),
        ),
        takeWhile((value) => ['pending', 'running'].includes(value.batch.batch.status), true),
      )
      .subscribe((value) => {
        if (!this.receive(value, id)) {
          this.polling?.unsubscribe();
          return;
        }
        this.message.set('');
        if (!['pending', 'running'].includes(value.batch.batch.status)) this.loadHistory();
      });
  }
  display(value: number | null | undefined) {
    return value == null ? 'indisponibil' : this.numbers.format(value);
  }
  percent(value: number | null | undefined) {
    return value == null ? 'indisponibil' : this.display(value * 100) + '%';
  }
  assessment(status: string) {
    return assessmentLabel(status);
  }
  reason(value: string) {
    return reasonLabel(value, this.evaluation()?.variants[0]?.configuration ?? []);
  }
  private receive(value: StaffingEvaluationDetails, expectedId?: string) {
    const problem = evaluationIdentityProblem(value, this.scenario(), expectedId);
    if (problem) {
      this.message.set(problem);
      this.evaluation.set(null);
      this.evaluated.emit(null);
      return false;
    }
    this.evaluation.set(value);
    this.evaluated.emit(value);
    return true;
  }
  private setBusy(value: boolean) {
    this.busy.set(value);
    this.working.emit(value);
  }
  private storageKey() {
    return `aerosynapse.staffing-command.v2.${this.session.session()?.tenantId ?? 'signed-out'}.${this.scenario().scenario.id}`;
  }
  private persistCommand() {
    try {
      window.localStorage.setItem(this.storageKey(), JSON.stringify(this.command));
    } catch {
      /* Optional persistence. */
    }
  }
  private clearCommand() {
    this.command = undefined;
    this.pendingCommand.set(false);
    try {
      window.localStorage.removeItem(this.storageKey());
    } catch {
      /* Optional persistence. */
    }
  }
  private restoreCommand() {
    try {
      const value = JSON.parse(window.localStorage.getItem(this.storageKey()) ?? 'null');
      if (
        !value ||
        !validRunId(value.id) ||
        value.body?.commandId !== value.id ||
        typeof value.identity !== 'string' ||
        !Number.isSafeInteger(value.body?.proposal?.revision) ||
        value.body.proposal.revision <= 0
      )
        return;
      const { commandId, ...parameters } = value.body;
      if (JSON.stringify(parameters) !== value.identity) return;
      this.command = value;
      this.pendingCommand.set(true);
      this.message.set(
        'Comandă restaurată după reîncărcare. Reîncearcă aceeași optimizare pentru a confirma rezultatul.',
      );
    } catch {
      /* Optional persistence; corrupt storage must not block the page. */
    }
  }
  private report(error: unknown) {
    this.message.set(operationError(error));
  }
}

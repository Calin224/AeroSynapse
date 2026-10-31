import { WriteAccess } from '../../core/auth/write-access';
import { DatePipe, DecimalPipe } from '@angular/common';
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
import { FormsModule } from '@angular/forms';
import { SessionState } from '../../core/auth/session-state';
import { formatWait } from '../../shared/ui/outcome-cards';
import { finalize, Subscription, timeout } from 'rxjs';
import { ApiClient } from '../../core/http/api-client';
import { ResponseFeedback } from '../../core/http/response-feedback';
import { OPERATION_REQUEST_TIMEOUT_MS, operationError } from '../../core/http/operation-request';
import type { StaffingEvaluationDetails } from './staffing-evaluation.models';
import { approvalProblem } from './staffing-evaluation.guards';
import type { ScenarioDetails } from './operations.models';
import type {
  RecommendationDecisionResult,
  RecommendationOutcome,
  RecommendationReview,
} from './recommendation-review.models';
import type { ScenarioInputs } from './operations.models';
import { scenarioClock } from '../scenario-studio/incident-designer';

/** One line per checkpoint: today's staffing and the shift plan's range, with the hour it peaks. */
export function scheduleSummary(
  schedule: NonNullable<RecommendationReview['explanation']['schedule']>,
  inputs: ScenarioInputs,
  slotMinutes = 15,
) {
  const range = (values: number[]) => {
    const low = Math.min(...values),
      high = Math.max(...values);
    return low === high ? `${high} toată ziua` : `${low}–${high}`;
  };
  return schedule.map((row) => {
    const peak = Math.max(...row.after);
    return {
      checkpointId: row.checkpointId,
      name: row.name,
      before: range(row.before),
      after: range(row.after),
      peak: peak ? scenarioClock(inputs, row.after.indexOf(peak) * slotMinutes) : null,
      most: peak,
    };
  });
}

@Component({
  selector: 'app-recommendation-review-panel',
  imports: [WriteAccess, FormsModule, DatePipe, DecimalPipe],
  host: { '[class.guided-host]': 'requireConfirmation()' },
  templateUrl: './recommendation-review-panel.html',
  styles: `
    :host {
      display: block;
      border: 1px solid var(--border-color, #64748b);
      border-radius: var(--radius);
      padding: 1rem;
      margin: 1rem 0;
    }
    th,
    td {
      padding: 0.5rem;
      text-align: left;
    }
    label {
      display: grid;
      gap: 0.25rem;
      margin: 0.75rem 0;
    }
    .actions {
      display: flex;
      flex-wrap: wrap;
      gap: 0.75rem;
    }
    .table-wrap {
      overflow: auto;
    }
    /* Guided decision: no frame of its own, plain language first, details folded. */
    :host(.guided-host) {
      border: 0;
      padding: 0;
      margin: 0;
    }
    .guided {
      display: grid;
      gap: 1rem;
    }
    .guided-head {
      display: flex;
      align-items: center;
      gap: 0.75rem;
    }
    .guided h3,
    .guided h4 {
      margin: 0;
    }
    .guided h4 {
      font-size: 1rem;
    }
    .status {
      padding: 0.2rem 0.7rem;
      border-radius: var(--radius);
      background: var(--warning-soft);
      color: var(--warning);
      font-size: 0.8rem;
      font-weight: 700;
    }
    .status[data-status='approved'] {
      background: var(--accent-soft);
      color: var(--accent);
    }
    .prepare {
      display: inline-flex;
      align-items: center;
      gap: 0.5rem;
      justify-self: start;
      min-height: 48px;
      padding-inline: 1.3rem;
    }
    .hint,
    .guided small {
      color: var(--text-muted);
      font-size: 0.85rem;
    }
    .change,
    .note,
    .decision {
      display: grid;
      gap: 0.75rem;
      padding: 1.1rem 1.25rem;
      border-radius: var(--radius-lg);
      border: 1px solid var(--border);
    }
    .change {
      background: var(--accent-soft);
      border-color: var(--accent-line);
    }
    .change .hint {
      color: var(--text);
    }
    .headline {
      margin: 0;
      font-size: 1.1rem;
    }
    .headline i {
      margin: 0 0.25rem;
      color: var(--accent);
    }
    .people {
      display: grid;
      gap: 0.5rem;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .people li {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 0.5rem;
      padding: 0.6rem 0.8rem;
      border-radius: var(--radius);
      background: var(--panel);
    }
    .people .from,
    .people .to {
      padding: 0.15rem 0.6rem;
      border-radius: var(--radius);
      font-size: 0.85rem;
    }
    .people .from {
      background: var(--surface-raised);
    }
    .people .to {
      background: var(--accent);
      color: var(--on-accent);
      font-weight: 600;
    }
    .note ul {
      margin: 0;
      padding-left: 1.2rem;
      color: var(--text-muted);
    }
    .note h4 i {
      color: var(--warning);
    }
    .decision {
      border-color: var(--border-strong);
      background: var(--surface-raised);
    }
    .decision label {
      margin: 0;
    }
    .decision input:not([type='checkbox']),
    .decision textarea {
      width: 100%;
    }
    .check {
      display: flex !important;
      align-items: center;
      gap: 0.6rem;
      font-weight: 600;
    }
    .check input {
      width: 1.2rem;
      height: 1.2rem;
      flex: none;
    }
    .problem {
      margin: 0;
      color: var(--danger);
    }
    .outcome {
      margin: 0;
      padding: 0.9rem 1.1rem;
      border-radius: var(--radius);
      background: var(--accent-soft);
      font-weight: 600;
    }
    .outcome[data-status='rejected'] {
      background: var(--surface-raised);
    }
    .guided summary {
      cursor: pointer;
      min-height: 44px;
      padding: 0.65rem 0;
      font-weight: 600;
    }
    .visually-hidden {
      position: absolute;
      width: 1px;
      height: 1px;
      overflow: hidden;
      clip-path: inset(50%);
      white-space: nowrap;
    }
    .link-button {
      justify-self: start;
      border: 0;
      background: transparent;
      color: var(--accent);
      text-decoration: underline;
      text-underline-offset: 3px;
    }
    pre {
      white-space: pre-wrap;
      overflow-wrap: anywhere;
      max-height: 16rem;
      overflow: auto;
    }
  `,
})
export class RecommendationReviewPanel {
  readonly online = inject(ResponseFeedback).online;
  readonly evaluationId = input.required<string>();
  readonly scenario = input.required<ScenarioDetails>();
  readonly disabled = input(false);
  readonly active = input(false);
  readonly evaluation = input<StaffingEvaluationDetails | null>(null);
  readonly requireConfirmation = input(false);
  readonly readOnly = input(false);
  confirmed = false;
  readonly changed = output<ScenarioDetails>();
  readonly working = output<boolean>();
  readonly review = signal<RecommendationReview | null>(null);
  readonly busy = signal(false);
  readonly message = signal('');
  operator = '';
  private readonly session = inject(SessionState);
  reason = '';
  private readonly api = inject(ApiClient);
  private request?: Subscription;
  private command?: {
    identity: string;
    body: { commandId: string; actor: string; reason: string };
  };
  private context = '';

  constructor() {
    inject(DestroyRef).onDestroy(() => this.request?.unsubscribe());
    effect(() => {
      const context = `${this.scenario().scenario.id}/${this.evaluationId()}`;
      const active = this.active();
      untracked(() => {
        if (context !== this.context) {
          this.request?.unsubscribe();
          this.context = context;
          this.review.set(null);
          this.message.set('');
          this.command = undefined;
          this.reason = '';
          this.confirmed = false;
        }
        if (!active) this.request?.unsubscribe();
      });
    });
  }

  open() {
    if (!this.active() || this.busy() || this.disabled()) return;
    this.setBusy(true);
    this.message.set('');
    const path = `staffing-evaluations/${this.evaluationId()}/recommendation`;
    this.request = (
      this.readOnly()
        ? this.api.get<RecommendationOutcome>(path)
        : this.api.post<RecommendationOutcome>(path)
    )
      .pipe(
        timeout(OPERATION_REQUEST_TIMEOUT_MS),
        finalize(() => this.setBusy(false)),
      )
      .subscribe({
        next: (result) => {
          if (result.recommendation && !this.matches(result.recommendation)) {
            this.review.set(null);
            this.message.set(
              'Recomandarea primită nu corespunde scenariului și evaluării selectate.',
            );
            return;
          }
          this.review.set(result.recommendation);
          this.command = undefined;
          this.message.set(result.message);
          // The server records the signed-in author anyway; start the declared name from it.
          if (this.requireConfirmation() && !this.operator)
            this.operator = this.session.session()?.username ?? '';
        },
        error: (error) => this.report(error),
      });
  }

  /** A shift plan changes when people work, not only where: its schedule replaces the moves list. */
  schedule(review: RecommendationReview) {
    const rows = review.explanation.schedule;
    return rows?.length ? scheduleSummary(rows, this.scenario().inputs) : null;
  }
  /** The overall mean wait estimate, readable as seconds or minutes. */
  meanWait(review: RecommendationReview) {
    const estimate = review.explanation.estimates[0];
    return estimate && estimate.unit === 'min'
      ? { before: formatWait(estimate.before), after: formatWait(estimate.after) }
      : null;
  }
  stale() {
    const review = this.review();
    return !!review && review.scenarioRevision !== this.scenario().scenario.revision;
  }
  approvalProblem() {
    if (this.readOnly())
      return 'Istoric · numai citire. Deciziile nu pot fi schimbate din acest mod.';
    const review = this.review(),
      evaluation = this.evaluation();
    if (!review || !this.matches(review))
      return 'Lipsește o recomandare pentru contextul selectat.';
    if (review.status !== 'pending') return 'Decizia este deja finalizată.';
    if (this.stale()) return 'Scenariul s-a modificat. Este necesară o evaluare nouă.';
    if (!this.scenario().scenario.isSynthetic || this.scenario().scenario.deletedAtUtc)
      return 'Aprobarea necesită un scenariu DEMO activ.';
    return evaluation ? approvalProblem(evaluation, this.scenario(), review) : '';
  }
  decide(action: 'approve' | 'reject') {
    const review = this.review();
    if (
      !this.online() ||
      this.readOnly() ||
      !this.active() ||
      !review ||
      review.status !== 'pending' ||
      this.busy() ||
      this.disabled() ||
      !this.matches(review) ||
      (action === 'approve' &&
        (this.approvalProblem() || (this.requireConfirmation() && !this.confirmed)))
    )
      return;
    if (
      !this.operator.trim() ||
      this.operator.length > 160 ||
      this.operator.includes('\0') ||
      this.reason.length > 2000 ||
      this.reason.includes('\0')
    ) {
      this.message.set(
        'Completează numele operatorului (1–160 caractere) și un motiv de cel mult 2000 caractere.',
      );
      return;
    }
    const actor = this.operator.trim();
    const reason = this.reason.trim();
    const identity = JSON.stringify({ id: review.id, action, actor, reason });
    if (this.command && this.command.identity !== identity) {
      this.message.set(
        'O decizie anterioară are rezultatul neconfirmat. Reîncarcă recomandarea înainte de a schimba decizia sau motivul.',
      );
      return;
    }
    if (!this.command)
      this.command = { identity, body: { commandId: crypto.randomUUID(), actor, reason } };
    this.setBusy(true);
    this.message.set('');
    this.request = this.api
      .post<RecommendationDecisionResult>(
        `recommendation-reviews/${review.id}/${action}`,
        this.command.body,
      )
      .pipe(
        timeout(OPERATION_REQUEST_TIMEOUT_MS),
        finalize(() => this.setBusy(false)),
      )
      .subscribe({
        next: (result) => {
          if (
            !this.matches(result.recommendation) ||
            result.recommendation.id !== review.id ||
            result.recommendation.baselineRunId !== review.baselineRunId ||
            result.recommendation.candidateRunId !== review.candidateRunId ||
            result.recommendation.status !== (action === 'approve' ? 'approved' : 'rejected') ||
            (result.scenario && result.scenario.scenario.id !== review.scenarioId)
          ) {
            this.message.set(
              'Confirmarea deciziei nu corespunde recomandării. Reîncarcă auditul înainte de a continua.',
            );
            return;
          }
          this.review.set(result.recommendation);
          this.command = undefined;
          this.message.set(
            action === 'approve'
              ? 'Aprobat: alocările scenariului DEMO au fost actualizate și auditate.'
              : 'Respins: scenariul a fost păstrat. Decizia este auditată.',
          );
          if (result.scenario) this.changed.emit(result.scenario);
        },
        error: (error) => this.report(error),
      });
  }

  status(value: string) {
    return (
      (
        {
          pending: 'În așteptarea deciziei',
          approved: 'Aprobat',
          rejected: 'Respins',
          created: 'Creat',
        } as Record<string, string>
      )[value] ?? value
    );
  }
  checkpointName(id: string) {
    return (
      this.review()?.explanation.checkpoints.find((item) => item.checkpointId === id)?.name ?? id
    );
  }
  private setBusy(value: boolean) {
    this.busy.set(value);
    this.working.emit(value);
  }
  private matches(review: RecommendationReview) {
    return (
      review.scenarioId === this.scenario().scenario.id &&
      review.evaluationId === this.evaluationId()
    );
  }
  private report(error: unknown) {
    this.message.set(operationError(error));
  }
}

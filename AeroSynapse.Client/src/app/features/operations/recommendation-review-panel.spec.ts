import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { apiErrorInterceptor } from '../../core/http/api-error.interceptor';
import { ResponseFeedback } from '../../core/http/response-feedback';
import type { ScenarioDetails, ScenarioInputs } from './operations.models';
import { RecommendationReviewPanel, scheduleSummary } from './recommendation-review-panel';
import type { RecommendationReview } from './recommendation-review.models';
import {
  ids,
  evaluationId,
  workflowEvaluation,
  workflowFixture,
  workflowReview,
} from '../operator-workflow/workflow.testing';
import { OPERATION_REQUEST_TIMEOUT_MS } from '../../core/http/operation-request';

const scenario: ScenarioDetails = {
  scenario: {
    id: 'scenario-1',
    code: 'DEMO',
    name: 'DEMO',
    description: '',
    sceneId: 'scene',
    layoutVersionId: 'layout',
    revision: 3,
    createdAtUtc: '',
    updatedAtUtc: '',
    isSynthetic: true,
  },
  inputs: {
    startsAtUtc: '2026-10-16T10:00:00Z',
    durationMinutes: 60,
    seed: 42,
    timeZone: 'UTC',
    flights: [],
    populations: [],
    flows: [],
    staffAllocations: [],
    events: [],
  },
};
function recommendation(): RecommendationReview {
  return {
    id: 'review-1',
    evaluationId: 'evaluation-1',
    scenarioId: 'scenario-1',
    scenarioRevision: 3,
    baselineRunId: 'baseline-run',
    candidateRunId: 'candidate-run',
    status: 'pending',
    createdAtUtc: '2026-10-16T10:00:00Z',
    explanation: {
      resources: [
        {
          staffMemberId: 'staff-1',
          name: 'Operator sintetic A',
          beforeCheckpointId: 'cp-1',
          beforeCheckpoint: 'Control A',
          afterCheckpointId: 'cp-2',
          afterCheckpoint: 'Control B',
        },
      ],
      checkpoints: [
        { checkpointId: 'cp-1', name: 'Control A', before: 2, after: 1 },
        { checkpointId: 'cp-2', name: 'Control B', before: 1, after: 2 },
      ],
      estimates: [{ metric: 'Așteptare medie', unit: 'min', before: 5, after: 3, delta: -2 }],
      assumptions: ['Aceeași populație și seed 42.'],
      benefit: 'Așteptare mai mică în simulator.',
      risks: ['Estimări dintr-o singură pereche de rulări.'],
      ranking: 'risk, waiting',
      reallocatedPeople: 1,
      reallocationCost: 20,
      acceptancePolicy: {
        maxMeanWaitIncreaseMinutes: 0,
        maxP95IncreaseMinutes: 1,
        maxBoardingRiskIncrease: 0.01,
        maxUnprocessedIncrease: 0,
        maxReallocationCost: 100,
      },
      constraints: {
        availableStaff: 3,
        staffPools: [{ terminalId: 'terminal', availableStaff: 3 }],
        checkpoints: [
          {
            checkpointId: 'cp-1',
            minimumStaff: 1,
            maximumStaff: 2,
            physicalLimit: 2,
            requiredOpen: true,
            closed: false,
            changeCost: 10,
          },
        ],
      },
    },
    audit: [
      {
        id: 'audit-1',
        commandId: 'creation-1',
        action: 'created',
        actor: 'system',
        reason: '',
        recordedAtUtc: '2026-10-16T10:00:00Z',
        scenarioRevisionBefore: 3,
        scenarioRevisionAfter: 3,
        beforeJson: '{}',
        afterJson: '{}',
      },
    ],
  };
}

describe('Shift schedule summary', () => {
  it('reads each checkpoint as today against the shift range and the hour it peaks', () => {
    const inputs = {
      startsAtUtc: '2026-10-16T05:00:00Z',
      timeZone: 'UTC',
    } as unknown as ScenarioInputs;
    expect(
      scheduleSummary(
        [{ checkpointId: 'a', name: 'CP-1', before: [2, 2, 2, 2], after: [1, 3, 3, 0] }],
        inputs,
      ),
    ).toEqual([
      {
        checkpointId: 'a',
        name: 'CP-1',
        before: '2 toată ziua',
        after: '0–3',
        peak: '05:15',
        most: 3,
      },
    ]);
  });
});

describe('Explainable recommendation and explicit human approval', () => {
  let http: HttpTestingController;
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(withInterceptors([apiErrorInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => {
    http.verify();
    vi.useRealTimers();
  });
  function component() {
    const fixture = TestBed.createComponent(RecommendationReviewPanel);
    fixture.componentRef.setInput('scenario', structuredClone(scenario));
    fixture.componentRef.setInput('evaluationId', 'evaluation-1');
    fixture.componentRef.setInput('active', true);
    fixture.detectChanges();
    return fixture;
  }
  function open(fixture: ReturnType<typeof component>, review = recommendation()) {
    fixture.componentInstance.open();
    http
      .expectOne('/api/staffing-evaluations/evaluation-1/recommendation')
      .flush({ status: 'available', message: '', recommendation: review });
    fixture.detectChanges();
  }
  function guided() {
    const fixture = TestBed.createComponent(RecommendationReviewPanel);
    fixture.componentRef.setInput('scenario', workflowFixture().scenario);
    fixture.componentRef.setInput('evaluationId', evaluationId);
    fixture.componentRef.setInput('evaluation', workflowEvaluation());
    fixture.componentRef.setInput('active', true);
    fixture.componentRef.setInput('requireConfirmation', true);
    fixture.detectChanges();
    fixture.componentInstance.open();
    http
      .expectOne(`/api/staffing-evaluations/${evaluationId}/recommendation`)
      .flush({ status: 'available', message: '', recommendation: workflowReview() });
    fixture.detectChanges();
    return fixture;
  }
  it('disables approval and rejection offline and does not dispatch them when the network returns', () => {
    const fixture = guided(),
      view = fixture.componentInstance;
    view.operator = 'Operator';
    view.confirmed = true;
    const network = TestBed.inject(ResponseFeedback);
    network.online.set(false);
    fixture.detectChanges();
    const approve = fixture.nativeElement.querySelector('button.btn-primary') as HTMLButtonElement;
    expect(approve.disabled).toBe(true);
    view.decide('approve');
    view.decide('reject');
    http.expectNone((request) => request.method === 'POST');
    network.online.set(true);
    fixture.detectChanges();
    expect(approve.disabled).toBe(false);
    http.expectNone((request) => request.method === 'POST');
    fixture.destroy();
  });
  it('requires explicit workflow confirmation and blocks invalid evaluation results even through direct method calls', () => {
    const fixture = guided(),
      view = fixture.componentInstance;
    view.operator = 'Operator';
    view.decide('approve');
    http.expectNone(`/api/recommendation-reviews/${ids.mark}/approve`);
    view.confirmed = true;
    const invalid = workflowEvaluation();
    invalid.batch.replications[1].resultsValid = false;
    fixture.componentRef.setInput('evaluation', invalid);
    fixture.detectChanges();
    expect(view.approvalProblem()).toContain('valide');
    view.decide('approve');
    http.expectNone(`/api/recommendation-reviews/${ids.mark}/approve`);
    fixture.destroy();
  });
  it('rejects a recommendation from another scenario before exposing decision actions', () => {
    const fixture = component();
    fixture.componentInstance.open();
    http.expectOne('/api/staffing-evaluations/evaluation-1/recommendation').flush({
      status: 'available',
      message: '',
      recommendation: { ...recommendation(), scenarioId: 'foreign-scenario' },
    });
    fixture.detectChanges();
    expect(fixture.componentInstance.review()).toBeNull();
    expect(fixture.nativeElement.textContent).toContain('nu corespunde');
    fixture.destroy();
  });
  it('keeps the exact decision after timeout and refuses a mismatched confirmation', async () => {
    const fixture = guided(),
      view = fixture.componentInstance;
    view.operator = 'Operator';
    view.confirmed = true;
    vi.useFakeTimers();
    view.decide('approve');
    const first = http.expectOne(`/api/recommendation-reviews/${ids.mark}/approve`),
      body = first.request.body;
    await vi.advanceTimersByTimeAsync(OPERATION_REQUEST_TIMEOUT_MS);
    expect(first.cancelled).toBe(true);
    expect(view.message()).toContain('Timeout');
    expect(view.busy()).toBe(false);
    view.decide('approve');
    const retry = http.expectOne(`/api/recommendation-reviews/${ids.mark}/approve`);
    expect(retry.request.body).toEqual(body);
    retry.flush({
      recommendation: { ...workflowReview(), candidateRunId: ids.run, status: 'approved' },
      scenario: null,
    });
    expect(view.review()?.status).toBe('pending');
    expect(view.message()).toContain('nu corespunde');
    fixture.destroy();
  });
  it('displays resource moves, estimates, premises, constraints, risks and audit without approving on read', () => {
    const fixture = component();
    open(fixture);
    const text = fixture.nativeElement.textContent;
    for (const value of ['candidate-run', 'Control A → Control B', 'limită fizică 2', 'Approve'])
      expect(text).toContain(value);
    http.expectNone((request) => request.url.includes('recommendation-reviews'));
    fixture.destroy();
  });
  it('requires an operator and sends approval only on an explicit click, then renders the durable decision', () => {
    const fixture = component();
    open(fixture);
    const view = fixture.componentInstance;
    view.decide('approve');
    http.expectNone((request) => request.url.includes('/approve'));
    view.operator = 'Operator DEMO';
    view.reason = 'Verificat';
    let updated: ScenarioDetails | undefined;
    view.changed.subscribe((value) => (updated = value));
    fixture.detectChanges();
    (Array.from(fixture.nativeElement.querySelectorAll('button')) as HTMLButtonElement[])
      .find((button) => button.textContent!.includes('Approve'))!
      .click();
    const request = http.expectOne('/api/recommendation-reviews/review-1/approve');
    expect(request.request.body.actor).toBe('Operator DEMO');
    expect(request.request.body.reason).toBe('Verificat');
    expect(request.request.body.commandId).toBeTruthy();
    view.decide('approve');
    http.expectNone('/api/recommendation-reviews/review-1/approve');
    const review = recommendation();
    review.status = 'approved';
    review.audit.push({
      ...review.audit[0],
      id: 'audit-2',
      commandId: request.request.body.commandId,
      action: 'approved',
      actor: 'Operator DEMO',
      scenarioRevisionAfter: 4,
    });
    request.flush({
      recommendation: review,
      scenario: { ...scenario, scenario: { ...scenario.scenario, revision: 4 } },
    });
    fixture.detectChanges();
    expect(updated?.scenario.revision).toBe(4);
    expect(fixture.nativeElement.textContent).toContain('Aprobat');
    expect(fixture.nativeElement.textContent).toContain('Operator DEMO');
    expect(
      (Array.from(fixture.nativeElement.querySelectorAll('button')) as HTMLButtonElement[]).some(
        (button) => button.textContent!.includes('Approve'),
      ),
    ).toBe(false);
    fixture.destroy();
  });
  it('keeps the command ID for an uncertain retry, and reconciles before permitting a different decision', () => {
    const fixture = component();
    open(fixture);
    const view = fixture.componentInstance;
    view.operator = 'Operator';
    view.decide('approve');
    const first = http.expectOne('/api/recommendation-reviews/review-1/approve');
    const body = first.request.body;
    first.error(new ProgressEvent('error'));
    view.decide('reject');
    http.expectNone('/api/recommendation-reviews/review-1/reject');
    expect(view.message()).toContain('neconfirmat');
    view.decide('approve');
    const retry = http.expectOne('/api/recommendation-reviews/review-1/approve');
    expect(retry.request.body).toEqual(body);
    retry.error(new ProgressEvent('error'));
    open(fixture);
    view.decide('reject');
    const reject = http.expectOne('/api/recommendation-reviews/review-1/reject');
    expect(reject.request.body.commandId).not.toBe(body.commandId);
    reject.flush({ recommendation: { ...recommendation(), status: 'rejected' }, scenario: null });
    fixture.detectChanges();
    expect(view.review()?.status).toBe('rejected');
    expect(fixture.nativeElement.textContent).toContain('scenariul a fost păstrat');
    fixture.destroy();
  });
  it('blocks stale approval, retains rejection, and surfaces server conflicts without inventing a decision', () => {
    const fixture = component();
    open(fixture);
    const view = fixture.componentInstance;
    view.operator = 'Operator';
    fixture.componentRef.setInput('scenario', {
      ...scenario,
      scenario: { ...scenario.scenario, revision: 4 },
    });
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('evaluare nouă');
    view.decide('approve');
    http.expectNone('/api/recommendation-reviews/review-1/approve');
    view.decide('reject');
    http
      .expectOne('/api/recommendation-reviews/review-1/reject')
      .flush({ title: 'Decizie deja finalizată' }, { status: 409, statusText: 'Conflict' });
    expect(view.review()?.status).toBe('pending');
    expect(view.message()).toContain('Datele s-au schimbat');
    fixture.destroy();
  });
  it('shows the no-superior outcome and never renders decision actions for a missing recommendation', () => {
    const fixture = component();
    fixture.componentInstance.open();
    http.expectOne('/api/staffing-evaluations/evaluation-1/recommendation').flush({
      status: 'no_superior_candidate',
      message: 'Nu există candidat superior validat.',
      recommendation: null,
    });
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Nu există candidat superior');
    expect(fixture.nativeElement.textContent).not.toContain('Approve');
    expect(fixture.nativeElement.textContent).not.toContain('Reject');
    fixture.destroy();
  });
  it('cancels stale requests when changing evaluation or leaving the tab', () => {
    const fixture = component();
    fixture.componentInstance.open();
    const pending = http.expectOne('/api/staffing-evaluations/evaluation-1/recommendation');
    fixture.componentRef.setInput('evaluationId', 'evaluation-2');
    fixture.detectChanges();
    expect(pending.cancelled).toBe(true);
    fixture.componentInstance.open();
    const next = http.expectOne('/api/staffing-evaluations/evaluation-2/recommendation');
    fixture.componentRef.setInput('active', false);
    fixture.detectChanges();
    expect(next.cancelled).toBe(true);
    expect(fixture.componentInstance.busy()).toBe(false);
    fixture.destroy();
  });
});

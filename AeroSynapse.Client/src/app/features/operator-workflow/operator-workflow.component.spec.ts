import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { Subject } from 'rxjs';
import { apiErrorInterceptor } from '../../core/http/api-error.interceptor';
import { OperationsStream } from '../../core/http/operations-stream';
import { OPERATION_REQUEST_TIMEOUT_MS } from '../../core/http/operation-request';
import { ControlTowerStore } from '../control-tower/control-tower.store';
import { StaffingPanel } from '../operations/staffing-panel';
import { StaffingEvaluationPanel } from '../operations/staffing-evaluation-panel';
import { RecommendationReviewPanel } from '../operations/recommendation-review-panel';
import { OperatorWorkflowComponent } from './operator-workflow.component';
import {
  baselineRunId,
  evaluationId,
  ids,
  workflowFixture,
  workflowEvaluation,
  workflowReview,
} from './workflow.testing';

describe('Operator incident-to-decision workflow', () => {
  let http: HttpTestingController;
  beforeEach(() => {
    const storage = new Map<string, string>();
    vi.stubGlobal('localStorage', {
      getItem: (key: string) => storage.get(key) ?? null,
      setItem: (key: string, value: string) => storage.set(key, value),
      removeItem: (key: string) => storage.delete(key),
    });
    TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: 'operator-workflow', component: OperatorWorkflowComponent }]),
        provideHttpClient(withInterceptors([apiErrorInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    TestBed.overrideComponent(OperatorWorkflowComponent, {
      set: {
        providers: [
          ControlTowerStore,
          {
            provide: OperationsStream,
            useValue: {
              state: signal(null),
              updates: new Subject(),
              connect: vi.fn(),
              status: signal('polling'),
              error: signal(''),
            },
          },
        ],
      },
    });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => {
    TestBed.resetTestingModule();
    http.verify({ ignoreCancelled: true });
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });
  async function setup() {
    const harness = await RouterTestingHarness.create();
    const view = await harness.navigateByUrl(
      `/operator-workflow?scenarioId=${ids.scenario}&runId=${ids.run}`,
      OperatorWorkflowComponent,
    );
    await context(harness);
    return { harness, view };
  }
  async function context(harness: RouterTestingHarness, scenario = workflowFixture().scenario) {
    http
      .match('/api/scenarios')
      .filter((request) => !request.cancelled)
      .forEach((request) => request.flush([scenario.scenario]));
    http.expectOne(`/api/scenarios/${ids.scenario}`).flush(scenario);
    http
      .expectOne(`/api/scenes/${ids.scene}/layout-versions/${ids.layout}`)
      .flush(workflowFixture().layout);
    http.expectOne(`/api/scenarios/${ids.scenario}/runs?limit=100`).flush([workflowFixture().run]);
    await vi.waitFor(() =>
      http.expectOne(`/api/scenarios/${ids.scenario}/runs/${ids.run}`).flush(workflowFixture().run),
    );
    harness.detectChanges();
  }
  it('reopens historical results and audit after a revision change without enabling any decision', async () => {
    const { harness, view } = await setup();
    const scenario = workflowFixture().scenario;
    scenario.scenario.revision = 2;
    view.tower.selected.set(scenario);
    view.openHistory();
    harness.detectChanges();
    http.expectOne(`/api/scenarios/${ids.scenario}/staffing/settings`).flush({
      scenarioId: ids.scenario,
      revision: 2,
      passengerProfilesActive: true,
      availableStaff: 1,
      staffPools: [],
      checkpoints: [],
    });
    harness.detectChanges();
    http.expectOne(`/api/scenarios/${ids.scenario}/staffing/evaluations`).flush([]);
    const evaluator = harness.fixture.debugElement.query(By.directive(StaffingEvaluationPanel))
      .componentInstance as StaffingEvaluationPanel;
    expect(evaluator.readOnly()).toBe(true);
    evaluator.load(evaluationId);
    http.expectOne(`/api/staffing-evaluations/${evaluationId}`).flush(workflowEvaluation());
    harness.detectChanges();
    expect(view.runReady()).toBe(false);
    expect(view.canCompare()).toBe(true);
    view.confirmComparison();
    harness.detectChanges();
    const reviewer = harness.fixture.debugElement.query(By.directive(RecommendationReviewPanel))
      .componentInstance as RecommendationReviewPanel;
    reviewer.open();
    const read = http.expectOne(`/api/staffing-evaluations/${evaluationId}/recommendation`);
    expect(read.request.method).toBe('GET');
    read.flush({
      status: 'available',
      message: '',
      recommendation: { ...workflowReview(), status: 'approved' },
    });
    reviewer.operator = 'Operator';
    reviewer.confirmed = true;
    reviewer.decide('approve');
    reviewer.decide('reject');
    evaluator.start();
    evaluator.cancel();
    http.expectNone((request) => request.method === 'POST');
    expect(reviewer.review()?.status).toBe('approved');
    expect(view.historyOnly()).toBe(true);
  });
  it('gates every dependent step and invalidates optimization when there is an unsaved incident', async () => {
    const { harness, view } = await setup();
    expect(view.allowed('simulate')).toBe(false);
    expect(view.allowed('optimize')).toBe(false);
    expect(view.allowed('compare')).toBe(false);
    expect(view.allowed('review')).toBe(false);
    view.useEvent(ids.event);
    harness.detectChanges();
    expect(view.runReady()).toBe(true);
    expect(view.allowed('optimize')).toBe(true);
    view.go('event');
    view.addEvent();
    harness.detectChanges();
    expect(view.runReady()).toBe(false);
    expect(view.allowed('optimize')).toBe(false);
    view.go('review');
    expect(view.stage()).toBe('event');
    expect(harness.routeNativeElement?.querySelector('app-incident-designer')).toBeTruthy();
  });
  it('moves keyboard focus to the new stage content and keeps the current step explicit', async () => {
    const { harness, view } = await setup();
    view.useEvent(ids.event);
    harness.detectChanges();
    await harness.fixture.whenStable();
    expect(document.activeElement?.id).toBe('simulate-title');
    expect(
      harness.routeNativeElement?.querySelector('[aria-current="step"]')?.textContent,
    ).toContain('Simulare impact');
    view.go('event');
    harness.detectChanges();
    await harness.fixture.whenStable();
    expect(document.activeElement?.id).toBe('event-title');
  });
  it('reconciles an ambiguous incident save by event ID and then requires a run of the new revision', async () => {
    const { harness, view } = await setup();
    view.addEvent();
    harness.detectChanges();
    expect(view.draftProblems()).toEqual([]);
    view.saveEvent();
    const request = http.expectOne(`/api/scenarios/${ids.scenario}/events`);
    const event = request.request.body.event;
    expect(request.request.body.revision).toBe(1);
    request.error(new ProgressEvent('error'));
    expect(view.eventUncertain()).toBe(true);
    view.saveEvent();
    http.expectNone(`/api/scenarios/${ids.scenario}/events`);
    view.reconcileEvent();
    const scenario = workflowFixture().scenario;
    scenario.scenario.revision = 2;
    // The API may reorder JSON properties; that must not change the event identity.
    const payload = JSON.parse(event.payloadJson);
    scenario.inputs.events.push({
      ...event,
      payloadJson: JSON.stringify({
        share: payload.share,
        isSynthetic: true,
        durationMinutes: payload.durationMinutes,
        contractVersion: '1.0',
      }),
    });
    http.expectOne(`/api/scenarios/${ids.scenario}`).flush(scenario);
    await vi.waitFor(() => expect(TestBed.inject(Router).url).toContain(event.id));
    await context(harness, scenario);
    expect(view.savedEventId()).toBe(event.id);
    expect(view.draft()).toBeNull();
    expect(view.eventUncertain()).toBe(false);
    expect(view.stage()).toBe('simulate');
    expect(view.tower.selected()?.scenario.revision).toBe(2);
    expect(view.runReady()).toBe(false);
    expect(view.allowed('optimize')).toBe(false);
  });
  it('keeps the event ID after timeout and retries it only after confirming it is absent at the same revision', async () => {
    const { harness, view } = await setup();
    view.addEvent();
    harness.detectChanges();
    expect(view.draftProblems()).toEqual([]);
    vi.useFakeTimers();
    view.saveEvent();
    const first = http.expectOne(`/api/scenarios/${ids.scenario}/events`),
      body = first.request.body;
    await vi.advanceTimersByTimeAsync(OPERATION_REQUEST_TIMEOUT_MS);
    expect(first.cancelled).toBe(true);
    expect(view.message()).toContain('Timeout');
    expect(view.eventBusy()).toBe(false);
    view.reconcileEvent();
    http.expectOne(`/api/scenarios/${ids.scenario}`).flush(workflowFixture().scenario);
    const retry = http.expectOne(`/api/scenarios/${ids.scenario}/events`);
    expect(retry.request.body).toEqual(body);
    expect(view.eventBusy()).toBe(true);
    retry.flush({}, { status: 503, statusText: 'Unavailable' });
  });
  it('blocks saving an incident beyond boarding closure and shows API validation without internal field paths', async () => {
    const { harness, view } = await setup();
    view.addEvent();
    const draft = view.draft()!;
    view.draft.set({ ...draft, occurredAtUtc: '2026-10-16T10:04:00Z' });
    harness.detectChanges();
    expect(view.draftProblems().join()).toContain('fereastra de sosire');
    view.saveEvent();
    http.expectNone(`/api/scenarios/${ids.scenario}/events`);
    view.draft.set(draft);
    view.saveEvent();
    http.expectOne(`/api/scenarios/${ids.scenario}/events`).flush(
      {
        errors: {
          'inputs.events[0].payloadJson.durationMinutes': [
            'Intervalul trebuie inclus în fereastra de sosire [0, 3] minute.',
          ],
        },
      },
      { status: 400, statusText: 'Bad Request' },
    );
    expect(view.message()).toBe('Intervalul trebuie inclus în fereastra de sosire [0, 3] minute.');
    expect(view.draft()?.id).toBe(draft.id);
    expect(view.eventUncertain()).toBe(false);
  });
  it('carries the diagnostic run, evaluation pair and explicit approval through one UI', async () => {
    const { harness, view } = await setup();
    view.useEvent(ids.event);
    view.go('optimize');
    harness.detectChanges();
    http.expectOne(`/api/scenarios/${ids.scenario}/staffing/settings`).flush({
      scenarioId: ids.scenario,
      revision: 1,
      passengerProfilesActive: true,
      availableStaff: 1,
      staffPools: [],
      checkpoints: [{ id: ids.cp, terminalId: ids.scene, name: 'Control API', currentStaff: 1 }],
    });
    harness.detectChanges();
    http.expectOne(`/api/scenarios/${ids.scenario}/staffing/evaluations`).flush([]);
    await harness.fixture.whenStable();
    const limit = harness.routeNativeElement!.querySelector<HTMLInputElement>(
      '[aria-label="Limită fizică Control API"]',
    )!;
    limit.value = '1';
    limit.dispatchEvent(new Event('input', { bubbles: true }));
    await harness.fixture.whenStable();
    harness.detectChanges();
    const evaluator = harness.fixture.debugElement.query(By.directive(StaffingEvaluationPanel))
      .componentInstance as StaffingEvaluationPanel;
    expect(evaluator.proposal()).not.toBeNull();
    expect(evaluator.active()).toBe(true);
    expect(evaluator.disabled()).toBe(false);
    evaluator.start();
    const create = http.expectOne(`/api/scenarios/${ids.scenario}/staffing/evaluations`);
    const id = create.request.body.commandId;
    expect(create.request.body.proposal.revision).toBe(1);
    create.flush(workflowEvaluation(id));
    http.expectOne(`/api/scenarios/${ids.scenario}/staffing/evaluations`).flush([]);
    harness.detectChanges();
    expect(view.tower.run()?.id).toBe(ids.run);
    expect(view.evaluation()?.variants[0].runId).toBe(baselineRunId);
    expect(view.canCompare()).toBe(true);
    expect(view.allowed('review')).toBe(false);
    view.go('compare');
    harness.detectChanges();
    expect(harness.routeNativeElement?.textContent).toContain(
      'Metrici din snapshot-urile SimPy persistate',
    );
    view.confirmComparison();
    harness.detectChanges();
    expect(view.stage()).toBe('review');
    const reviewer = harness.fixture.debugElement.query(By.directive(RecommendationReviewPanel))
      .componentInstance as RecommendationReviewPanel;
    reviewer.open();
    http
      .expectOne(`/api/staffing-evaluations/${id}/recommendation`)
      .flush({ status: 'available', message: '', recommendation: workflowReview(id) });
    reviewer.operator = 'Operator test';
    reviewer.decide('approve');
    http.expectNone(`/api/recommendation-reviews/${ids.mark}/approve`);
    reviewer.confirmed = true;
    reviewer.decide('approve');
    const approve = http.expectOne(`/api/recommendation-reviews/${ids.mark}/approve`);
    const scenario = workflowFixture().scenario;
    scenario.scenario.revision = 2;
    approve.flush({ recommendation: { ...workflowReview(id), status: 'approved' }, scenario });
    harness.detectChanges();
    http.expectOne(`/api/scenarios/${ids.scenario}/staffing/evaluations`).flush([]);
    harness.detectChanges();
    expect(view.approved()).toBe(true);
    expect(view.tower.selected()?.scenario.revision).toBe(2);
    expect(harness.routeNativeElement?.textContent).toContain('Aprobat');
    expect(
      harness.fixture.debugElement.query(By.directive(RecommendationReviewPanel)).componentInstance,
    ).toBe(reviewer);
  });
});

import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { apiErrorInterceptor } from '../../core/http/api-error.interceptor';
import { ResponseFeedback } from '../../core/http/response-feedback';
import type { ScenarioDetails } from './operations.models';
import type { StaffingProposal } from './staffing-panel';
import type {
  StaffingEvaluationDetails,
  StaffingEvaluatedVariant,
} from './staffing-evaluation.models';
import { DEFAULT_SHIFTS, shiftRules, StaffingEvaluationPanel } from './staffing-evaluation-panel';
import { OPERATION_REQUEST_TIMEOUT_MS } from '../../core/http/operation-request';

describe('Shift rules', () => {
  it('turns hours and minutes into the planner slots, and rejects values it cannot plan', () => {
    expect(shiftRules(DEFAULT_SHIFTS)).toMatchObject({
      minShiftSlots: 16,
      maxShiftSlots: 24,
      breakAfterSlots: 10,
      transferMinutes: 5,
      maxMovesPerHour: 2,
      targetUtilizationPercent: 75,
    });
    expect(shiftRules({ ...DEFAULT_SHIFTS, targetUtilizationPercent: 0 })).toBeNull();
    expect(shiftRules({ ...DEFAULT_SHIFTS, minShiftHours: 7 })).toBeNull();
    expect(shiftRules({ ...DEFAULT_SHIFTS, breakAfterMinutes: 20 })).toBeNull();
    expect(shiftRules({ ...DEFAULT_SHIFTS, transferMinutes: 15 })).toBeNull();
  });
});

const scenario: ScenarioDetails = {
  scenario: {
    id: 'scenario-1',
    code: 'demo',
    name: 'Demo',
    description: '',
    sceneId: 'scene-1',
    layoutVersionId: 'layout-1',
    revision: 3,
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
const proposal: StaffingProposal = {
  revision: 3,
  availableStaff: 4,
  timeLimitSeconds: 5,
  checkpoints: [
    {
      checkpointId: 'cp-1',
      minimumStaff: 1,
      maximumStaff: 4,
      physicalLimit: 4,
      requiredOpen: true,
      closed: false,
      changeCost: 10,
    },
  ],
};
const historyPath = '/api/scenarios/scenario-1/staffing/evaluations';
function evaluation(state = 'completed', id = 'evaluation-1'): StaffingEvaluationDetails {
  const metrics = {
    populationCount: 100,
    processedCount: 100,
    unprocessedCount: 0,
    waitSampleCount: 100,
    meanWaitMinutes: 2,
    p95WaitMinutes: 4,
    boardingRisk: 0.1,
    boardingAtRiskCount: 10,
    boardingRiskPopulationCount: 100,
    checkpoints: [
      {
        checkpointId: 'cp-1',
        populationCount: 100,
        processedCount: 100,
        waitSampleCount: 100,
        meanWaitMinutes: 2,
        p95WaitMinutes: 4,
        boardingAtRiskCount: 10,
        boardingRiskPopulationCount: 100,
      },
    ],
  };
  const baseline: StaffingEvaluatedVariant = {
    variantId: 'v-0',
    scenarioId: 'scenario-1',
    runId: 'run-0',
    position: 0,
    runStatus: state === 'completed' ? 'completed' : 'pending',
    configuration: [{ checkpointId: 'cp-1', name: 'Control A', staffCount: 2 }],
    reallocatedPeople: 0,
    reallocationCost: 0,
    metrics: state === 'completed' ? metrics : null,
    assessment: { status: state === 'completed' ? 'baseline' : 'pending', reasons: [] },
  };
  return {
    batch: {
      contractVersion: '1.0',
      batch: {
        id,
        name: 'DEMO',
        status: state,
        revision: 1,
        createdAtUtc: '2026-10-16T10:00:00Z',
        deadlineAtUtc: '2026-10-16T10:02:00Z',
        startedAtUtc: null,
        finishedAtUtc: null,
        cancellationRequested: false,
        timeLimitSeconds: 120,
        replayOfBatchId: null,
        failureReason: null,
        engineBuild: null,
      },
      seeds: [42],
      isSynthetic: true,
      metricsTruncated: false,
      variants: [],
      replications: [],
      metrics: [],
      comparisons: [],
    },
    planning: {
      status: 'exhausted',
      candidates: [{}],
      request: {
        candidateCount: 3,
        constraints: { scenarioId: 'scenario-1', scenarioRevision: 3, timeLimitSeconds: 5 },
      },
    },
    policy: {
      maxMeanWaitIncreaseMinutes: 0,
      maxP95IncreaseMinutes: 0,
      maxBoardingRiskIncrease: 0,
      maxUnprocessedIncrease: 0,
      maxReallocationCost: 100,
    },
    ranking: 'risk, unprocessed, waiting, P95, cost',
    recommendedRunId: state === 'completed' ? 'run-1' : null,
    variants: [
      baseline,
      {
        ...baseline,
        variantId: 'v-1',
        scenarioId: 'candidate-1',
        runId: 'run-1',
        position: 1,
        reallocatedPeople: 1,
        reallocationCost: 10,
        configuration: [{ checkpointId: 'cp-1', name: 'Control A', staffCount: 3 }],
        metrics:
          state === 'completed' ? { ...metrics, meanWaitMinutes: 1, p95WaitMinutes: 2 } : null,
        assessment: { status: state === 'completed' ? 'improved' : 'pending', reasons: [] },
      },
    ],
  };
}

describe('Simulated staffing evaluation', () => {
  let http: HttpTestingController;
  beforeEach(() => {
    vi.useFakeTimers();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([apiErrorInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => {
    http.verify();
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });
  function component() {
    const fixture = TestBed.createComponent(StaffingEvaluationPanel);
    fixture.componentRef.setInput('scenario', structuredClone(scenario));
    fixture.componentRef.setInput('proposal', structuredClone(proposal));
    fixture.componentRef.setInput('active', true);
    fixture.detectChanges();
    http.expectOne(historyPath).flush([]);
    return fixture;
  }
  it('blocks a new optimization offline and never submits it on reconnect', () => {
    const fixture = component(),
      network = TestBed.inject(ResponseFeedback);
    network.online.set(false);
    fixture.detectChanges();
    fixture.componentInstance.start();
    http.expectNone((request) => request.method === 'POST');
    network.online.set(true);
    fixture.detectChanges();
    http.expectNone((request) => request.method === 'POST');
    fixture.destroy();
  });
  it('restores a timed-out command after refresh and keeps its original payload and idempotency key', async () => {
    const values = new Map<string, string>();
    vi.stubGlobal('localStorage', {
      getItem: (key: string) => values.get(key) ?? null,
      setItem: (key: string, value: string) => values.set(key, value),
      removeItem: (key: string) => values.delete(key),
    });
    const firstView = component();
    firstView.componentInstance.start();
    const first = http.expectOne(historyPath),
      body = first.request.body;
    await vi.advanceTimersByTimeAsync(OPERATION_REQUEST_TIMEOUT_MS);
    expect(first.cancelled).toBe(true);
    expect(firstView.componentInstance.message()).toContain('Timeout');
    firstView.destroy();
    const restored = component();
    expect(restored.componentInstance.pendingCommand()).toBe(true);
    restored.componentInstance.load('another-evaluation');
    http.expectNone('/api/staffing-evaluations/another-evaluation');
    restored.componentInstance.retry();
    const retry = http.expectOne(historyPath);
    expect(retry.request.body).toEqual(body);
    retry.flush(evaluation('completed', retry.request.body.commandId));
    http.expectOne(historyPath).flush([]);
    expect(values.size).toBe(0);
    restored.destroy();
  });
  it('rejects a loaded evaluation from another source without retaining its metrics', () => {
    const fixture = component(),
      value = evaluation();
    value.planning.request.constraints.scenarioId = 'foreign';
    fixture.componentInstance.load('evaluation-1');
    http.expectOne('/api/staffing-evaluations/evaluation-1').flush(value);
    expect(fixture.componentInstance.evaluation()).toBeNull();
    expect(fixture.componentInstance.message()).toContain('nu aparține');
    fixture.destroy();
  });
  it('captures constraints, policy and command identity, then polls genuine metrics through completion', async () => {
    const fixture = component();
    const view = fixture.componentInstance;
    view.start();
    const request = http.expectOne(historyPath);
    expect(request.request.body.proposal).toEqual(proposal);
    expect(request.request.body.candidateCount).toBe(3);
    expect(request.request.body.policy.maxUnprocessedIncrease).toBe(0);
    expect(request.request.body.policy.maxP95IncreaseMinutes).toBe(1);
    expect(request.request.body.policy.checkpointTradeoffs).toBe(true);
    expect(request.request.body.commandId).toBeTruthy();
    request.flush(evaluation('pending', request.request.body.commandId));
    http.expectOne(historyPath).flush([]);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('indisponibil');
    expect(view.evaluation()?.recommendedRunId).toBeNull();
    await vi.advanceTimersByTimeAsync(1000);
    const id = request.request.body.commandId;
    http.expectOne(`/api/staffing-evaluations/${id}`).flush(evaluation('completed', id));
    http.expectOne(historyPath).flush([]);
    fixture.detectChanges();
    expect(view.running()).toBe(false);
    expect(fixture.nativeElement.textContent).toContain('Îmbunătățire operațională');
    expect(fixture.nativeElement.textContent).toContain('Persoane realocate');
    expect(fixture.nativeElement.textContent).toContain('run-1');
    expect(fixture.nativeElement.textContent).toContain('Control A: 3');
    expect(fixture.nativeElement.textContent).toContain('10%');
    await vi.advanceTimersByTimeAsync(5000);
    http.expectNone(`/api/staffing-evaluations/${id}`);
    fixture.destroy();
  });
  it('retains an unconfirmed command and its original policy even when the form changes', () => {
    const fixture = component();
    const view = fixture.componentInstance;
    view.start();
    const first = http.expectOne(historyPath);
    const id = first.request.body.commandId;
    first.error(new ProgressEvent('error'));
    expect(view.evaluation()).toBeNull();
    expect(view.busy()).toBe(false);
    view.start();
    const retry = http.expectOne(historyPath);
    expect(retry.request.body.commandId).toBe(id);
    retry.error(new ProgressEvent('error'));
    view.policy.maxP95IncreaseMinutes = 5;
    view.start();
    http.expectNone(historyPath);
    expect(view.message()).toContain('neconfirmat');
    view.retry();
    const unchanged = http.expectOne(historyPath);
    expect(unchanged.request.body.commandId).toBe(id);
    expect(unchanged.request.body.policy.maxP95IncreaseMinutes).toBe(1);
    unchanged.flush(evaluation('completed', unchanged.request.body.commandId));
    http.expectOne(historyPath).flush([]);
    fixture.destroy();
  });
  it('shows accepted degradation as a tradeoff and preserves the captured policy during form edits', () => {
    const fixture = component();
    const result = evaluation();
    result.recommendedRunId = null;
    result.variants[1].assessment = { status: 'tradeoff', reasons: ['accepted_degradation'] };
    fixture.componentInstance.load('evaluation-1');
    http.expectOne('/api/staffing-evaluations/evaluation-1').flush(result);
    fixture.componentInstance.policy.maxP95IncreaseMinutes = 99;
    fixture.detectChanges();
    const text = fixture.nativeElement.textContent;
    expect(text).toContain('Compromis cu degradare acceptată');
    expect(text).not.toContain('Îmbunătățire operațională');
    expect(text).toContain('P95 0 min');
    expect(text).toContain('Nu există candidat superior validat');
    fixture.destroy();
  });
  it('renders failed candidate metrics as unavailable and exposes the rejection reason', () => {
    const fixture = component();
    const result = evaluation();
    result.recommendedRunId = null;
    result.variants[1].metrics = null;
    result.variants[1].assessment = { status: 'failed', reasons: ['simulation_timeout'] };
    fixture.componentInstance.load('evaluation-1');
    http.expectOne('/api/staffing-evaluations/evaluation-1').flush(result);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Rulare eșuată');
    expect(fixture.nativeElement.textContent).toContain('indisponibil');
    expect(fixture.nativeElement.textContent).toContain('simulation_timeout');
    fixture.destroy();
  });
  it('cancels an obsolete polling response and resets the report on scenario revision changes', async () => {
    const fixture = component();
    fixture.componentInstance.load('evaluation-1');
    http.expectOne('/api/staffing-evaluations/evaluation-1').flush(evaluation('pending'));
    await vi.advanceTimersByTimeAsync(1000);
    const abandoned = http.expectOne('/api/staffing-evaluations/evaluation-1');
    fixture.componentRef.setInput('scenario', {
      ...scenario,
      scenario: { ...scenario.scenario, revision: 4 },
    });
    fixture.detectChanges();
    expect(abandoned.cancelled).toBe(true);
    expect(fixture.componentInstance.evaluation()).toBeNull();
    http.expectOne(historyPath).flush([]);
    fixture.destroy();
  });
  it('pauses polling while inactive and recovers from a transient polling error on return', async () => {
    const fixture = component();
    fixture.componentInstance.load('evaluation-1');
    http.expectOne('/api/staffing-evaluations/evaluation-1').flush(evaluation('pending'));
    fixture.componentRef.setInput('active', false);
    fixture.detectChanges();
    await vi.advanceTimersByTimeAsync(3000);
    http.expectNone('/api/staffing-evaluations/evaluation-1');
    fixture.componentRef.setInput('active', true);
    fixture.detectChanges();
    http.expectOne(historyPath).flush([]);
    await vi.advanceTimersByTimeAsync(1000);
    http.expectOne('/api/staffing-evaluations/evaluation-1').error(new ProgressEvent('error'));
    expect(fixture.componentInstance.message()).not.toBe('');
    await vi.advanceTimersByTimeAsync(1500);
    http.expectOne('/api/staffing-evaluations/evaluation-1').flush(evaluation());
    http.expectOne(historyPath).flush([]);
    expect(fixture.componentInstance.message()).toBe('');
    fixture.destroy();
  });
  it('clears the previous report when opening another evaluation fails', async () => {
    const fixture = component();
    const view = fixture.componentInstance;
    const selections: (StaffingEvaluationDetails | null)[] = [];
    view.evaluated.subscribe((value) => selections.push(value));
    view.load('evaluation-1');
    http.expectOne('/api/staffing-evaluations/evaluation-1').flush(evaluation());
    view.load('evaluation-2');
    fixture.detectChanges();
    expect(view.evaluation()).toBeNull();
    expect(fixture.nativeElement.textContent).not.toContain('Recomandare dintre');
    http.expectOne('/api/staffing-evaluations/evaluation-2').error(new ProgressEvent('error'));
    fixture.detectChanges();
    expect(view.evaluation()).toBeNull();
    expect(selections.at(-1)).toBeNull();
    expect(view.message()).not.toBe('');
    await vi.advanceTimersByTimeAsync(3000);
    http.expectNone('/api/staffing-evaluations/evaluation-1');
    fixture.destroy();
  });
  it('resumes polling after a manual refresh fails for the current evaluation', async () => {
    const fixture = component();
    const view = fixture.componentInstance;
    view.load('evaluation-1');
    http.expectOne('/api/staffing-evaluations/evaluation-1').flush(evaluation('pending'));
    view.load('evaluation-1');
    http.expectOne('/api/staffing-evaluations/evaluation-1').error(new ProgressEvent('error'));
    expect(view.evaluation()?.batch.batch.id).toBe('evaluation-1');
    await vi.advanceTimersByTimeAsync(1000);
    http.expectOne('/api/staffing-evaluations/evaluation-1').flush(evaluation());
    http.expectOne(historyPath).flush([]);
    expect(view.running()).toBe(false);
    fixture.destroy();
  });
  it('keeps an in-flight command when the same scenario revision is refreshed', () => {
    const fixture = component();
    const view = fixture.componentInstance;
    view.start();
    const request = http.expectOne(historyPath);
    fixture.componentRef.setInput('scenario', structuredClone(scenario));
    fixture.detectChanges();
    expect(request.cancelled).toBe(false);
    expect(view.busy()).toBe(true);
    http.expectNone(historyPath);
    request.flush(evaluation('pending', request.request.body.commandId));
    http.expectOne(historyPath).flush([]);
    expect(view.running()).toBe(true);
    fixture.destroy();
  });
  it('uses the durable batch cancellation endpoint without inventing completed results', async () => {
    const fixture = component();
    fixture.componentInstance.load('evaluation-1');
    http.expectOne('/api/staffing-evaluations/evaluation-1').flush(evaluation('pending'));
    fixture.componentInstance.cancel();
    http.expectOne('/api/simulation-batches/evaluation-1/cancel').flush({});
    await vi.advanceTimersByTimeAsync(1000);
    const stopped = evaluation('cancelled');
    http.expectOne('/api/staffing-evaluations/evaluation-1').flush(stopped);
    http.expectOne(historyPath).flush([]);
    expect(fixture.componentInstance.running()).toBe(false);
    expect(fixture.componentInstance.evaluation()?.recommendedRunId).toBeNull();
    fixture.destroy();
  });
  it('blocks invalid policies and incomplete staffing constraints before posting', () => {
    const fixture = component();
    const view = fixture.componentInstance;
    view.policy.maxBoardingRiskIncrease = -1;
    view.start();
    expect(view.message()).toContain('toleranțe');
    http.expectNone((request) => request.method === 'POST');
    fixture.componentRef.setInput('proposal', null);
    fixture.detectChanges();
    view.start();
    http.expectNone((request) => request.method === 'POST');
    fixture.destroy();
  });
  it('captures changed multi-objective weights in a new solver command and never mutates a saved evaluation', () => {
    const fixture = component();
    const view = fixture.componentInstance;
    view.multiObjectiveEnabled = true;
    view.multiObjective.weights = { time: 100, cost: 0, risk: 0 };
    view.start();
    const first = http.expectOne(historyPath);
    const id = first.request.body.commandId;
    expect(first.request.body.multiObjective.weights).toEqual({ time: 100, cost: 0, risk: 0 });
    first.flush(evaluation('completed', first.request.body.commandId));
    http.expectOne(historyPath).flush([]);
    view.multiObjective.weights = { time: 0, cost: 100, risk: 0 };
    view.start();
    const changed = http.expectOne(historyPath);
    expect(changed.request.body.commandId).not.toBe(id);
    expect(changed.request.body.multiObjective.weights.cost).toBe(100);
    expect(first.request.body.multiObjective.weights.time).toBe(100);
    changed.flush(evaluation('completed', changed.request.body.commandId));
    http.expectOne(historyPath).flush([]);
    fixture.destroy();
  });
  it('rejects zero weights and invalid normalization before creating multi-objective scenarios', () => {
    const fixture = component();
    const view = fixture.componentInstance;
    view.multiObjectiveEnabled = true;
    view.multiObjective.weights = { time: 0, cost: 0, risk: 0 };
    view.start();
    http.expectNone((request) => request.method === 'POST');
    expect(view.message()).toContain('sumă pozitivă');
    view.multiObjective.weights.time = 1;
    view.multiObjective.normalization.timeMinutes = 0;
    view.start();
    http.expectNone((request) => request.method === 'POST');
    fixture.destroy();
  });
  it('retains the evaluated recommendation and audit view after its own approval updates the scenario revision', () => {
    const fixture = component();
    const view = fixture.componentInstance;
    view.load('evaluation-1');
    http.expectOne('/api/staffing-evaluations/evaluation-1').flush(evaluation());
    const approved = { ...scenario, scenario: { ...scenario.scenario, revision: 4 } };
    view.approved(approved);
    fixture.componentRef.setInput('scenario', approved);
    fixture.detectChanges();
    http.expectOne(historyPath).flush([]);
    expect(view.evaluation()?.batch.batch.id).toBe('evaluation-1');
    expect(fixture.nativeElement.querySelector('app-recommendation-review-panel')).toBeTruthy();
    fixture.destroy();
  });
  it('rejects a creation receipt for another command outside the guided workflow and keeps the original retry', () => {
    const fixture = component(),
      view = fixture.componentInstance;
    view.start();
    const first = http.expectOne(historyPath);
    const body = first.request.body;
    first.flush(evaluation());
    expect(view.evaluation()).toBeNull();
    expect(view.pendingCommand()).toBe(true);
    expect(view.message()).toContain('comenzii');
    view.retry();
    const retry = http.expectOne(historyPath);
    expect(retry.request.body).toEqual(body);
    retry.flush(evaluation('completed', body.commandId));
    http.expectOne(historyPath).flush([]);
    expect(view.pendingCommand()).toBe(false);
    fixture.destroy();
  });
  it('notifies the host while loading another report and releases the lock when the request is cancelled', () => {
    const fixture = component(),
      view = fixture.componentInstance;
    const changes: boolean[] = [];
    view.working.subscribe((value) => changes.push(value));
    view.load('evaluation-1');
    const pending = http.expectOne('/api/staffing-evaluations/evaluation-1');
    expect(changes).toEqual([true]);
    fixture.componentRef.setInput('active', false);
    fixture.detectChanges();
    expect(pending.cancelled).toBe(true);
    expect(changes).toEqual([true, false]);
    fixture.destroy();
  });
});

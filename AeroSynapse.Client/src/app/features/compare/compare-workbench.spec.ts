import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { BatchDetails } from '../batches/batches.models';
import { RunComparison } from '../operations/comparison.models';
import { ScenarioDetails } from '../operations/operations.models';
import { StudioLayout } from '../scenario-studio/studio.models';
import { apiErrorInterceptor } from '../../core/http/api-error.interceptor';
import { OPERATION_REQUEST_TIMEOUT_MS } from '../../core/http/operation-request';
import { CompareWorkbench } from './compare-workbench';

const scenario: ScenarioDetails = {
  scenario: {
    id: 'source',
    code: 'DEMO',
    name: 'Original demo',
    description: '',
    sceneId: 'scene',
    layoutVersionId: 'v1',
    revision: 3,
    isSynthetic: true,
  },
  inputs: {
    startsAtUtc: '2026-10-16T10:00:00Z',
    durationMinutes: 90,
    timeZone: 'UTC',
    seed: 42,
    flights: [],
    populations: [],
    flows: [],
    events: [],
    staffAllocations: [
      { staffMemberId: 's1', checkpointId: 'cp1' },
      { staffMemberId: 's2', checkpointId: 'cp1' },
      { staffMemberId: 's3', checkpointId: 'cp2' },
    ],
  },
};
const layout: StudioLayout = {
  version: { id: 'v1', version: 1, sceneId: 'scene', createdAtUtc: '2026-10-16T10:00:00Z' },
  layout: {
    scene: { id: 'scene', code: 'DEMO', name: 'Aurora', isSynthetic: true },
    gates: [],
    routes: [],
    checkpoints: ['cp1', 'cp2'].map((id) => ({
      id,
      code: id,
      name: id,
      capacityPerMinute: 7,
      terminalId: 'terminal',
    })),
    staff: ['s1', 's2', 's3', 's4'].map((id) => ({
      id,
      code: id,
      displayName: id,
      terminalId: 'terminal',
      checkpointId: null,
    })),
  },
};
const provenance = {
  hashVersion: 'sha256-tree-v1',
  inputHash: 'a',
  resultHash: 'b',
  engineVersion: 'simpy-kpi-1.0',
  engineBuild: 'build',
  engineMilliseconds: 1,
  executionMilliseconds: 2,
  correlationId: 'trace',
};
function pair(status = 'pending', id = 'pair'): BatchDetails {
  return {
    contractVersion: '1.0',
    isSynthetic: true,
    seeds: [42],
    metricsTruncated: false,
    batch: {
      id,
      name: 'Experiment demo',
      status,
      revision: 1,
      createdAtUtc: '2026-10-16T10:00:00Z',
      deadlineAtUtc: '2026-10-16T10:01:00Z',
      startedAtUtc: null,
      finishedAtUtc: null,
      cancellationRequested: false,
      timeLimitSeconds: 60,
      replayOfBatchId: null,
      failureReason: null,
      engineBuild: null,
    },
    variants: [0, 1].map((position) => ({
      id: 'v' + position,
      position,
      scenarioId: position ? 'copy' : 'source',
      scenarioRevision: 3,
      name: position ? 'Copie what-if' : 'Original demo',
      layoutVersionId: 'v1',
      engineVersion: 'simpy-kpi-1.0',
    })),
    replications: [0, 1].map((index) => ({
      id: 'r' + index,
      variantId: 'v' + index,
      seedIndex: 0,
      seed: 42,
      runId: index ? 'candidate-run' : 'baseline-run',
      status,
      runStatus: status === 'pending' ? 'planned' : status,
      resultsValid: status === 'completed',
      failureReason: null,
      provenance,
      remoteCancellationPending: false,
    })),
    metrics: [],
    comparisons: [],
  };
}
function report(): RunComparison {
  return {
    baseline: {
      id: 'baseline-run',
      status: 'completed',
      resultsValid: true,
      scenarioRevision: 3,
      provenance,
      failureReason: null,
    },
    candidate: {
      id: 'candidate-run',
      status: 'completed',
      resultsValid: true,
      scenarioRevision: 1,
      provenance,
      failureReason: null,
    },
    inputsMatch: false,
    versionsMatch: true,
    resultsMatch: false,
    metricsTruncated: false,
    comparability: {
      comparable: true,
      populationMatches: true,
      baselinePopulationHash: 'p',
      candidatePopulationHash: 'p',
      issues: [],
    },
    metrics: [
      {
        zoneId: 'cp1',
        name: 'meanWaitMinutes',
        unit: 'minutes',
        baseline: 10,
        candidate: 5,
        delta: -5,
        percentChange: -50,
      },
    ],
  };
}
describe('Baseline/what-if workbench', () => {
  let http: HttpTestingController;
  beforeEach(() => {
    vi.useFakeTimers();
    const values = new Map<string, string>();
    vi.stubGlobal('localStorage', {
      getItem: (key: string) => values.get(key) ?? null,
      setItem: (key: string, value: string) => values.set(key, value),
      removeItem: (key: string) => values.delete(key),
    });
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([apiErrorInterceptor])),
        provideHttpClientTesting(),
        provideRouter([]),
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
    const fixture = TestBed.createComponent(CompareWorkbench);
    fixture.detectChanges();
    http.expectOne('/api/scenarios').flush([scenario.scenario]);
    return fixture;
  }
  function select(view: CompareWorkbench, value = scenario, plan = layout) {
    view.scenarioId = 'source';
    view.select();
    http.expectOne('/api/scenarios/source').flush(structuredClone(value));
    http.expectOne('/api/scenes/scene/layout-versions/v1').flush(plan);
    http.expectNone('/api/simulation-batches?scenarioId=source');
  }
  it("shows the scenario's team at each post and restaffs the check-in desks", () => {
    const fixture = component(),
      view = fixture.componentInstance;
    const desks = {
      id: 'desks',
      code: 'CHECK-IN',
      name: 'Ghișee',
      deskCount: 12,
      serviceSeconds: 120,
      walkingSeconds: 90,
      terminalId: 'terminal',
    };
    select(
      view,
      {
        ...scenario,
        inputs: {
          ...scenario.inputs,
          checkIn: { areaId: 'desks', staffCount: 2, holdBaggageShare: 0.4 },
          operators: [
            { id: 's1', name: 'Ana', role: 'security' },
            { id: 'c1', name: 'Bogdan', role: 'checkIn' },
            { id: 'c2', name: 'Carmen', role: 'checkIn' },
          ],
        },
      },
      { ...layout, layout: { ...layout.layout, checkInAreas: [desks] } },
    );
    fixture.detectChanges();
    // Named operators keep their names; unnamed slots fall back to the layout's.
    expect(view.team()).toEqual(['Ana', 's2']);
    expect(fixture.nativeElement.textContent).toContain('Check-in · Ghișee');
    view.checkpointId = 'desks';
    view.checkpointChanged();
    expect(view.team()).toEqual(['Bogdan', 'Carmen']);
    expect([view.currentCount(), view.availableCount(), view.staffCount]).toEqual([2, 12, 3]);
    view.staffCount = 0;
    view.create();
    expect(view.message()).toContain('1–12');
    view.staffCount = 4;
    view.create();
    const request = http.expectOne('/api/scenarios/source/what-if');
    expect(request.request.body).toMatchObject({ checkpointId: 'desks', staffCount: 4 });
    request.flush({}, { status: 503, statusText: 'Unavailable' });
  });
  it('suggests one additional available operator without taking people from other controls', () => {
    const fixture = component(),
      view = fixture.componentInstance;
    select(view);
    expect(view.currentCount()).toBe(2);
    expect(view.availableCount()).toBe(3);
    expect(view.staffCount).toBe(3);
    view.checkpointId = 'cp2';
    view.checkpointChanged();
    expect(view.currentCount()).toBe(1);
    expect(view.availableCount()).toBe(2);
    expect(view.staffCount).toBe(2);
    expect(view.source()!.scenario.inputs.staffAllocations).toEqual(
      scenario.inputs.staffAllocations,
    );
  });
  it("caps a control at the scenario's team instead of the layout's spare slots", () => {
    const fixture = component(),
      view = fixture.componentInstance;
    const withTeam = (ids: string[]) => ({
      ...scenario,
      inputs: {
        ...scenario.inputs,
        operators: ids.map((id) => ({ id, name: id, role: 'security' as const })),
      },
    });
    select(view, withTeam(['s1', 's2', 's3']));
    fixture.detectChanges();
    expect([view.currentCount(), view.availableCount()]).toEqual([2, 2]);
    expect(fixture.nativeElement.textContent).toContain('Toată echipa e alocată');
    select(view, withTeam(['s1', 's2', 's3', 's4']));
    expect(view.availableCount()).toBe(3);
  });
  it('preserves the original and atomically starts and compares independent labeled runs', async () => {
    const fixture = component(),
      view = fixture.componentInstance;
    select(view);
    view.staffCount = 1;
    view.create();
    const request = http.expectOne('/api/scenarios/source/what-if');
    expect(request.request.body).toMatchObject({
      revision: 3,
      checkpointId: 'cp1',
      staffCount: 1,
      timeLimitSeconds: 60,
    });
    expect(request.request.body.commandId).toMatch(/^[0-9a-f-]{36}$/);
    const id = request.request.body.commandId;
    request.flush(pair('pending', id));
    http.expectOne('/api/scenarios').flush([scenario.scenario]);
    await vi.advanceTimersByTimeAsync(0);
    http.expectOne(`/api/simulation-batches/${id}`).flush(pair('pending', id));
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('baseline-run');
    expect(fixture.nativeElement.textContent).toContain('candidate-run');
    await vi.advanceTimersByTimeAsync(1000);
    http.expectOne(`/api/simulation-batches/${id}`).flush(pair('completed', id));
    http.expectOne('/api/runs/baseline-run/compare/candidate-run').flush(report());
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.summary-table').textContent).toContain('-5');
    expect(fixture.nativeElement.textContent).not.toContain(
      'Raport detaliat și verificarea comparabilității',
    );
    expect(
      fixture.nativeElement.querySelector('section[aria-label="Rezultate baseline și what-if"]'),
    ).toBeNull();
    expect(fixture.nativeElement.querySelector('.history')).toBeNull();
    expect(view.source()!.scenario.inputs).toEqual(scenario.inputs);
    http.expectNone((request) => request.method === 'PUT');
    await vi.advanceTimersByTimeAsync(5000);
    http.expectNone(`/api/simulation-batches/${id}`);
    view.create();
    const repeated = http.expectOne('/api/scenarios/source/what-if');
    expect(repeated.request.body.commandId).not.toBe(request.request.body.commandId);
    repeated.flush({}, { status: 503, statusText: 'Unavailable' });
  });
  it('does not take staff allocated elsewhere and rejects an invalid count before sending a command', () => {
    const view = component().componentInstance;
    select(view);
    expect(view.currentCount()).toBe(2);
    expect(view.availableCount()).toBe(3);
    view.staffCount = 4;
    view.create();
    expect(view.message()).toContain('0–3');
    http.expectNone((request) => request.method === 'POST');
  });
  it('preserves the original command after a network error and blocks changed parameters until reconciliation', () => {
    const fixture = component(),
      view = fixture.componentInstance;
    select(view);
    view.create();
    const first = http.expectOne('/api/scenarios/source/what-if'),
      id = first.request.body.commandId;
    first.flush({}, { status: 503, statusText: 'Unavailable' });
    view.create();
    const retry = http.expectOne('/api/scenarios/source/what-if');
    expect(retry.request.body.commandId).toBe(id);
    retry.flush({}, { status: 503, statusText: 'Unavailable' });
    view.staffCount = 1;
    view.create();
    http.expectNone('/api/scenarios/source/what-if');
    expect(view.message()).toContain('neconfirmat');
    view.retry();
    const unchanged = http.expectOne('/api/scenarios/source/what-if');
    expect(unchanged.request.body).toEqual(first.request.body);
    unchanged.flush({}, { status: 409, statusText: 'Conflict' });
    expect(view.pendingCommand()).toBe(false);
    view.create();
    const changed = http.expectOne('/api/scenarios/source/what-if');
    expect(changed.request.body.commandId).not.toBe(id);
    expect(changed.request.body.staffCount).toBe(1);
    changed.flush({}, { status: 409, statusText: 'Conflict' });
    fixture.destroy();
  });
  it('loads a retained pair, rejects a report with different run IDs and cancels stale requests', async () => {
    const fixture = component(),
      view = fixture.componentInstance;
    select(view);
    view.open('pair');
    await vi.advanceTimersByTimeAsync(0);
    http.expectOne('/api/simulation-batches/pair').flush(pair('completed'));
    const invalid = report();
    invalid.candidate.id = 'unrelated-run';
    http.expectOne('/api/runs/baseline-run/compare/candidate-run').flush(invalid);
    expect(view.report()).toBeNull();
    expect(view.message()).toContain('nu corespunde');
    view.compare();
    const pending = http.expectOne('/api/runs/baseline-run/compare/candidate-run');
    view.scenarioId = '';
    view.select();
    expect(pending.cancelled).toBe(true);
    expect(view.report()).toBeNull();
    expect(view.detail()).toBeNull();
  });
  it('shows polling failures, recovers and handles a failed candidate as an unavailable comparison', async () => {
    const fixture = component(),
      view = fixture.componentInstance;
    select(view);
    view.open('pair');
    await vi.advanceTimersByTimeAsync(0);
    http
      .expectOne('/api/simulation-batches/pair')
      .flush({}, { status: 503, statusText: 'Unavailable' });
    expect(view.pollingError()).toContain('ultima stare confirmată');
    const failed = pair('completed_with_failures');
    failed.replications[0].runStatus = 'completed';
    failed.replications[0].resultsValid = true;
    failed.replications[1].runStatus = 'failed';
    failed.replications[1].failureReason = 'engine_timeout';
    await vi.advanceTimersByTimeAsync(1000);
    http.expectOne('/api/simulation-batches/pair').flush(failed);
    const comparison = report();
    comparison.candidate.status = 'failed';
    comparison.candidate.resultsValid = false;
    comparison.candidate.failureReason = 'engine_timeout';
    comparison.comparability!.comparable = false;
    comparison.metrics[0] = {
      ...comparison.metrics[0],
      candidate: null,
      delta: null,
      percentChange: null,
    };
    http.expectOne('/api/runs/baseline-run/compare/candidate-run').flush(comparison);
    fixture.detectChanges();
    expect(view.pollingError()).toBe('');
    expect(fixture.nativeElement.textContent).toContain('engine_timeout');
    expect(fixture.nativeElement.textContent).toContain(
      'Date insuficiente pentru calcularea diferențelor',
    );
  });
  it('does not present a multi-seed batch as a single pair', async () => {
    const fixture = component(),
      view = fixture.componentInstance;
    select(view);
    view.open('pair');
    const multiple = pair('completed');
    multiple.seeds = [42, 43];
    await vi.advanceTimersByTimeAsync(0);
    http.expectOne('/api/simulation-batches/pair').flush(multiple);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('a[href="/batches"]')).toBeNull();
    http.expectNone((request) => request.url.includes('/compare/'));
    expect(view.roles()).toEqual([]);
  });
  it('restores a timed-out command after reload and retries its original inputs despite a newer source revision', async () => {
    const fixture = component(),
      view = fixture.componentInstance;
    select(view);
    view.staffCount = 1;
    view.create();
    const request = http.expectOne('/api/scenarios/source/what-if'),
      body = request.request.body;
    await vi.advanceTimersByTimeAsync(OPERATION_REQUEST_TIMEOUT_MS);
    expect(request.cancelled).toBe(true);
    expect(view.message()).toContain('Timeout');
    fixture.destroy();
    const restored = component(),
      next = restored.componentInstance;
    next.scenarioId = 'source';
    next.select();
    http
      .expectOne('/api/scenarios/source')
      .flush({ ...scenario, scenario: { ...scenario.scenario, revision: 4 } });
    http.expectOne('/api/scenes/scene/layout-versions/v1').flush(layout);
    http.expectNone('/api/simulation-batches?scenarioId=source');
    expect(next.pendingCommand()).toBe(true);
    next.retry();
    const retry = http.expectOne('/api/scenarios/source/what-if');
    expect(retry.request.body).toEqual(body);
    retry.flush(pair('pending', body.commandId));
    http.expectOne('/api/scenarios').flush([scenario.scenario]);
    expect(next.pendingCommand()).toBe(false);
    restored.destroy();
  });
  it('rejects a different batch creation receipt and retains the pending command', () => {
    const fixture = component(),
      view = fixture.componentInstance;
    select(view);
    view.create();
    http.expectOne('/api/scenarios/source/what-if').flush(pair());
    expect(view.pendingCommand()).toBe(true);
    expect(view.detail()).toBeNull();
    expect(view.message()).toContain('nu corespunde');
    fixture.destroy();
  });
  it('rejects a foreign polling manifest without rendering or comparing its runs', async () => {
    const fixture = component(),
      view = fixture.componentInstance;
    select(view);
    view.open('pair');
    await vi.advanceTimersByTimeAsync(0);
    http.expectOne('/api/simulation-batches/pair').flush(pair('completed', 'foreign'));
    expect(view.detail()).toBeNull();
    expect(view.pollingError()).toContain('nu aparține');
    http.expectNone('/api/runs/baseline-run/compare/candidate-run');
    fixture.destroy();
  });
  it.each(['missing-replica', 'duplicate-run', 'wrong-seed', 'wrong-role'])(
    'does not treat an inconsistent %s manifest as a pair',
    async (failure) => {
      const fixture = component(),
        view = fixture.componentInstance;
      select(view);
      view.open('pair');
      const value = pair('completed');
      if (failure === 'missing-replica') value.replications[0].variantId = 'unrelated';
      if (failure === 'duplicate-run') value.replications[1].runId = value.replications[0].runId;
      if (failure === 'wrong-seed') value.replications[1].seed = 99;
      if (failure === 'wrong-role') value.variants[1].position = 2;
      await vi.advanceTimersByTimeAsync(0);
      http.expectOne('/api/simulation-batches/pair').flush(value);
      fixture.detectChanges();
      expect(view.roles()).toEqual([]);
      http.expectNone('/api/runs/baseline-run/compare/candidate-run');
      fixture.destroy();
    },
  );
});

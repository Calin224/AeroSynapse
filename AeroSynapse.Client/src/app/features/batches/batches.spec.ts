import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { Batches } from './batches';
import { BatchDetails, Statistics } from './batches.models';
import { ScenarioSummary } from '../operations/operations.models';

const scenarios: ScenarioSummary[] = ['baseline', 'candidate'].map((id) => ({
  id,
  code: id,
  name: id,
  revision: 3,
  description: '',
  sceneId: 'scene',
  layoutVersionId: 'layout',
}));
const stats: Statistics = {
  count: 2,
  mean: 3,
  standardDeviation: 1.414213562,
  minimum: 2,
  maximum: 4,
  median: 3,
  p05: 2,
  p95: 4,
};
const report: BatchDetails = {
  contractVersion: '1.0',
  isSynthetic: true,
  metricsTruncated: false,
  batch: {
    id: 'batch-1',
    name: 'Demo Monte Carlo',
    status: 'completed_with_failures',
    revision: 3,
    createdAtUtc: '2026-10-15T10:00:00Z',
    deadlineAtUtc: '2026-10-15T10:01:00Z',
    startedAtUtc: '2026-10-15T10:00:01Z',
    finishedAtUtc: '2026-10-15T10:00:05Z',
    cancellationRequested: false,
    timeLimitSeconds: 60,
    replayOfBatchId: null,
    failureReason: null,
    engineBuild: 'c'.repeat(64),
  },
  seeds: [42, 7, 99],
  variants: [
    {
      id: 'v0',
      position: 0,
      scenarioId: 'baseline',
      scenarioRevision: 3,
      name: 'Baseline demo',
      layoutVersionId: 'layout',
      engineVersion: 'simpy-kpi-1.0',
    },
    {
      id: 'v1',
      position: 1,
      scenarioId: 'candidate',
      scenarioRevision: 3,
      name: 'Candidate demo',
      layoutVersionId: 'layout',
      engineVersion: 'simpy-kpi-1.0',
    },
  ],
  replications: [
    {
      id: 'rep-1',
      variantId: 'v1',
      seedIndex: 1,
      seed: 7,
      runId: 'run-failed',
      status: 'failed',
      runStatus: 'failed',
      resultsValid: false,
      failureReason: 'simulation_failed: seed 7 failed',
      remoteCancellationPending: false,
      provenance: {
        hashVersion: 'sha256-tree-v1',
        inputHash: 'a'.repeat(64),
        resultHash: null,
        engineVersion: 'simpy-kpi-1.0',
        engineBuild: 'c'.repeat(64),
        engineMilliseconds: 1,
        executionMilliseconds: 2,
        correlationId: 'trace',
      },
    },
  ],
  metrics: [
    {
      variantId: 'v1',
      zoneId: 'cp-1',
      name: 'meanWaitMinutes',
      unit: 'minutes',
      values: stats,
      missingOrUnsuccessfulCount: 1,
    },
  ],
  comparisons: [
    {
      candidateVariantId: 'v1',
      zoneId: 'cp-1',
      name: 'meanWaitMinutes',
      unit: 'minutes',
      differences: stats,
      includedSeeds: [42, 99],
      excludedSeeds: [{ seed: 7, reason: 'candidate_failed' }],
    },
  ],
};

/** Two variants over the same two days, every run valid and paired. */
function paired(): BatchDetails {
  const replication = (variantId: string, seed: number, seedIndex: number) => ({
    ...report.replications[0],
    id: `${variantId}-${seed}`,
    variantId,
    seed,
    seedIndex,
    runId: `run-${variantId}-${seed}`,
    status: 'completed',
    runStatus: 'completed',
    resultsValid: true,
    failureReason: null,
  });
  const values = (median: number): Statistics => ({
    ...stats,
    mean: median,
    median,
    minimum: median - 2,
    maximum: median + 2,
    p05: median - 2,
    p95: median + 2,
  });
  const metric = (variantId: string, name: string, unit: string, median: number) => ({
    variantId,
    zoneId: 'cp-1',
    name,
    unit,
    values: values(median),
    missingOrUnsuccessfulCount: 0,
  });
  return {
    ...report,
    batch: { ...report.batch, status: 'completed' },
    seeds: [42, 43],
    replications: [
      replication('v0', 42, 0),
      replication('v0', 43, 1),
      replication('v1', 42, 0),
      replication('v1', 43, 1),
    ],
    metrics: [
      metric('v0', 'meanWaitMinutes', 'minutes', 30),
      metric('v1', 'meanWaitMinutes', 'minutes', 15),
      metric('v0', 'maximumQueue', 'passengers', 100),
      metric('v1', 'maximumQueue', 'passengers', 120),
    ],
    comparisons: [
      {
        candidateVariantId: 'v1',
        zoneId: 'cp-1',
        name: 'meanWaitMinutes',
        unit: 'minutes',
        differences: values(-15),
        includedSeeds: [42, 43],
        excludedSeeds: [],
      },
    ],
  };
}
/** Final wait and queue recorded by each run. */
const runResults: Record<string, [number, number]> = {
  'run-v0-42': [29, 98],
  'run-v0-43': [31, 102],
  'run-v1-42': [14, 121],
  'run-v1-43': [16, 119],
};
function runPage(runId: string) {
  const [wait, queue] = runResults[runId];
  const row = (name: string, unit: string, value: number) => ({
    id: `${runId}-${name}`,
    zoneId: 'cp-1',
    name,
    unit,
    value,
    recordedAtUtc: '2026-06-01T09:30:00Z',
  });
  return {
    runId,
    isValid: true,
    items: [row('meanWaitMinutes', 'minutes', wait), row('maximumQueue', 'passengers', queue)],
  };
}

describe('Monte Carlo batch page', () => {
  let http: HttpTestingController;
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());
  function component() {
    const fixture = TestBed.createComponent(Batches);
    fixture.detectChanges();
    http.expectOne('/api/scenarios').flush(scenarios);
    http.expectOne('/api/simulation-batches').flush([]);
    fixture.detectChanges();
    return fixture;
  }
  it('preselects the baseline and ignores candidate links without showing variant controls', async () => {
    const route = TestBed.inject(ActivatedRoute);
    vi.spyOn(route.snapshot, 'queryParamMap', 'get').mockReturnValue(
      convertToParamMap({
        scenarioId: 'baseline',
        candidateId: 'candidate',
      }),
    );
    const fixture = component(),
      view = fixture.componentInstance;
    expect(view.baselineId).toBe('baseline');
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector('select[name="baseline"]').value).toBe('baseline');
    const candidates = fixture.nativeElement.querySelectorAll('input[type="checkbox"]');
    expect(candidates).toHaveLength(0);
    expect(fixture.nativeElement.querySelector('.candidate-options')).toBeNull();
  });
  it('ignores an unknown baseline from the incoming link', () => {
    const route = TestBed.inject(ActivatedRoute);
    vi.spyOn(route.snapshot, 'queryParamMap', 'get').mockReturnValue(
      convertToParamMap({
        scenarioId: 'unknown',
        candidateId: 'candidate',
      }),
    );
    const view = component().componentInstance;
    expect(view.baselineId).toBe('');
  });
  it('turns a repetition count into distinct seeds for the baseline only', () => {
    const fixture = component(),
      view = fixture.componentInstance;
    view.chooseBaseline('baseline');
    view.setRepetitions(5);
    fixture.changeDetectorRef.markForCheck();
    fixture.detectChanges();
    expect(view.seedText).toBe('42, 43, 44, 45, 46');
    expect(fixture.nativeElement.textContent).toContain('5 simulări în total');
    view.create();
    const request = http.expectOne('/api/simulation-batches');
    expect(request.request.body).toMatchObject({
      seeds: [42, 43, 44, 45, 46],
      baseline: { scenarioId: 'baseline', revision: 3 },
      candidates: [],
    });
    request.flush({}, { status: 503, statusText: 'Unavailable' });
    view.setRepetitions(0);
    expect(view.seedText).toBe('42, 43, 44, 45, 46');
    view.setRepetitions(26);
    expect(view.seedText).toBe('42, 43, 44, 45, 46');
  });
  it('does not replace a hand-written seed list until a preset is chosen', () => {
    const view = component().componentInstance;
    view.chooseBaseline('baseline');
    view.seedText = '7, 999';
    expect(view.repeatCount()).toBe(2);
    expect(view.seedText).toBe('7, 999');
    view.setRepetitions(3);
    expect(view.seedText).toBe('42, 43, 44');
  });
  it('submits the exact ordered seeds once for the selected scenario revision and preserves the command ID after a lost response', () => {
    const fixture = component(),
      view = fixture.componentInstance;
    view.baselineId = 'baseline';
    view.seedText = '42, 7, 99,';
    view.create();
    const first = http.expectOne('/api/simulation-batches');
    const id = first.request.body.commandId;
    expect(first.request.body).toMatchObject({
      seeds: [42, 7, 99],
      baseline: { scenarioId: 'baseline', revision: 3 },
      candidates: [],
      timeLimitSeconds: 60,
    });
    first.flush({}, { status: 503, statusText: 'Lost response' });
    view.create();
    const repeated = http.expectOne('/api/simulation-batches');
    expect(repeated.request.body.commandId).toBe(id);
    repeated.flush({}, { status: 503, statusText: 'Unavailable' });
  });
  it('rejects duplicate and empty seed lists', () => {
    const fixture = component(),
      view = fixture.componentInstance;
    view.baselineId = 'baseline';
    view.seedText = '42,42';
    view.create();
    http.expectNone((request) => request.method === 'POST');
    expect(view.message()).toContain('distincte');
    view.chooseBaseline('candidate');
    view.seedText = ',,';
    view.create();
    http.expectNone((request) => request.method === 'POST');
  });
  function runRequests() {
    return http.match((request) => request.url.startsWith('/api/runs/'));
  }
  function flushRuns(requests = runRequests()) {
    for (const request of requests) request.flush(runPage(request.request.url.split('/')[3]));
  }
  it('shows spread, paired sample counts and failed seeds explicitly', () => {
    const fixture = component();
    fixture.componentInstance.detail.set(report);
    fixture.detectChanges();
    const text = fixture.nativeElement.textContent;
    expect(text).toContain('O simulare nu a reușit. Ele nu intră în calcul');
    expect(text).toContain('Nicio repetare finalizată.');
    expect(text).toContain('Deviație standard');
    expect(text).toContain('P05');
    expect(text).toContain('2 / 1');
    expect(text).toContain('candidate_failed');
    expect(text).toContain('simulation_failed');
    expect(text).toContain('42, 7, 99');
  });
  it('requests cancellation without dropping the current seed matrix', () => {
    const fixture = component(),
      view = fixture.componentInstance;
    view.detail.set({
      ...report,
      batch: { ...report.batch, status: 'running', finishedAtUtc: null },
    });
    view.cancel();
    const request = http.expectOne('/api/simulation-batches/batch-1/cancel');
    expect(request.request.method).toBe('POST');
    request.flush({}, { status: 503, statusText: 'Unavailable' });
    expect(view.detail()!.seeds).toEqual([42, 7, 99]);
    expect(view.busy()).toBe(false);
  });
  it('replays from the persisted batch rather than rebuilding edited scenario inputs', () => {
    const fixture = component(),
      view = fixture.componentInstance;
    view.detail.set(report);
    view.replay();
    const request = http.expectOne('/api/simulation-batches/batch-1/replay');
    expect(request.request.body).toMatchObject({
      name: 'Demo Monte Carlo · reluare',
      timeLimitSeconds: 60,
    });
    expect(request.request.body.seeds).toBeUndefined();
    expect(request.request.body.baseline).toBeUndefined();
    request.flush({}, { status: 503, statusText: 'Unavailable' });
  });
  it('loads every simulated day once and leads with a plain verdict and a day-by-day duel', () => {
    const fixture = component(),
      view = fixture.componentInstance,
      element: HTMLElement = fixture.nativeElement;
    view.detail.set(paired());
    fixture.detectChanges();
    const requests = runRequests();
    expect(requests.map((request) => request.request.url)).toEqual(
      ['run-v0-42', 'run-v0-43', 'run-v1-42', 'run-v1-43'].map(
        (id) => `/api/runs/${id}/metrics?limit=100`,
      ),
    );
    flushRuns(requests);
    fixture.detectChanges();
    const verdict = element.querySelector('.verdict')!;
    expect(verdict.getAttribute('data-tone')).toBe('better');
    expect(verdict.querySelector('h3')!.textContent).toBe(
      '„Candidate demo” reduce așteptarea medie de la 30 min la 15 min.',
    );
    expect(verdict.textContent).toContain('Mai bună în 2 din 2 zile');
    expect(verdict.querySelector('.figure strong')!.textContent!.trim()).toBe('−50%');
    expect(element.querySelectorAll('app-batch-spread .dot')).toHaveLength(4);
    expect(element.querySelector('.duel')!.textContent).toContain('mai bună în 2 din 2 zile');
    expect(element.querySelectorAll('.duel [data-outcome="better"]')).toHaveLength(2);
    const details = element.querySelector<HTMLDetailsElement>('details.tech')!;
    expect(details.open).toBe(false);
    expect(details.textContent).toContain('run-v0-42');
    view.detail.set(paired());
    fixture.detectChanges();
    expect(runRequests()).toHaveLength(0);
  });
  it('switches the indicator and admits when the variant makes things worse', () => {
    const fixture = component(),
      element: HTMLElement = fixture.nativeElement;
    fixture.componentInstance.detail.set(paired());
    fixture.detectChanges();
    flushRuns();
    fixture.detectChanges();
    const chips = [...element.querySelectorAll<HTMLButtonElement>('.chips button')];
    expect(chips.map((chip) => chip.textContent!.trim())).toEqual([
      'Așteptare medie',
      'Coada maximă',
    ]);
    expect(chips[0].getAttribute('aria-pressed')).toBe('true');
    chips[1].click();
    fixture.detectChanges();
    const verdict = element.querySelector('.verdict')!;
    expect(verdict.getAttribute('data-tone')).toBe('worse');
    expect(verdict.querySelector('h3')!.textContent).toBe(
      '„Candidate demo” crește coada maximă de la 100 pasageri la 120 pasageri.',
    );
    expect(verdict.textContent).toContain('Comparația zi cu zi apare după');
    expect(verdict.querySelector('.figure strong')!.textContent!.trim()).toBe('+20%');
    expect(element.querySelector('.duel')!.textContent).toContain(
      'încă nu există zile comparabile',
    );
  });
  it('explains a single scenario in plain words, hides unreadable days and retries failed loads', () => {
    const fixture = component(),
      view = fixture.componentInstance,
      element: HTMLElement = fixture.nativeElement;
    const single = paired();
    const alone: BatchDetails = {
      ...single,
      variants: single.variants.slice(0, 1),
      replications: single.replications.filter((row) => row.variantId === 'v0'),
      metrics: single.metrics.filter((row) => row.variantId === 'v0'),
      comparisons: [],
    };
    view.detail.set(alone);
    fixture.detectChanges();
    const [full, broken] = runRequests();
    full.flush({
      runId: 'run-v0-42',
      isValid: true,
      items: Array.from({ length: 100 }, (_, index) => ({
        ...runPage('run-v0-42').items[0],
        id: `row-${index}`,
      })),
    });
    broken.flush({}, { status: 503, statusText: 'Unavailable' });
    fixture.detectChanges();
    const verdict = element.querySelector('.verdict')!;
    expect(verdict.querySelector('h3')!.textContent).toBe(
      'Rezultat stabil: zilele simulate seamănă mult între ele.',
    );
    expect(verdict.textContent).toContain(
      'Interval realist: între 28 min și 32 min în 9 zile din 10',
    );
    expect(verdict.textContent).toContain('Sunt doar 2 zile simulate');
    expect(verdict.querySelector('.figure strong')!.textContent!.trim()).toBe('30min');
    expect(element.querySelector('.duels')).toBeNull();
    expect(element.textContent).toContain('O zi reușită nu are valori individuale disponibile');
    view.detail.set({ ...alone });
    fixture.detectChanges();
    expect(runRequests().map((request) => request.request.url)).toEqual([
      '/api/runs/run-v0-43/metrics?limit=100',
    ]);
  });
  it('brings the results into view when a saved test is opened', async () => {
    const original = Element.prototype.scrollIntoView;
    const scroll = vi.fn();
    Element.prototype.scrollIntoView = scroll; // Not implemented by jsdom.
    try {
      const fixture = TestBed.createComponent(Batches);
      fixture.detectChanges();
      http.expectOne('/api/scenarios').flush(scenarios);
      http.expectOne('/api/simulation-batches').flush([report.batch]);
      fixture.detectChanges();
      fixture.nativeElement.querySelector('.history').click();
      await new Promise((resolve) => setTimeout(resolve));
      http.expectOne('/api/simulation-batches/batch-1').flush(report);
      fixture.detectChanges();
      expect(scroll).toHaveBeenCalledWith({ behavior: 'auto', block: 'start' });
    } finally {
      Element.prototype.scrollIntoView = original;
    }
  });
});

import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { OperationalReport } from './operational-report';
import { ids, replayData, replayEnvelope } from '../replay/replay.testing';
import { candidateData, comparisonFixture } from './report.testing';

describe('Operational report selection and persisted data loading', () => {
  let http: HttpTestingController;
  beforeEach(async () => {
    vi.stubGlobal(
      'crypto',
      (await vi.importActual<{ webcrypto: Crypto }>('node:crypto')).webcrypto,
    );
    TestBed.configureTestingModule({
      imports: [OperationalReport],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => {
    // The run pickers list scenarios on render; these specs set run ids directly.
    http
      .match('/api/scenarios')
      .filter((request) => !request.cancelled)
      .forEach((request) => request.flush([]));
    http.verify();
    vi.unstubAllGlobals();
  });
  async function component() {
    const fixture = TestBed.createComponent(OperationalReport);
    await fixture.whenStable();
    // Both run pickers list scenarios on render; an empty library keeps them idle.
    http
      .match((request) => request.method === 'GET' && request.url === '/api/scenarios')
      .forEach((request) => request.flush([]));
    return fixture;
  }
  async function until(predicate: () => boolean) {
    await vi.waitFor(() => expect(predicate()).toBe(true), { timeout: 3000, interval: 10 });
  }
  const completed = (id = ids.run) => ({
    id,
    status: 'completed',
    resultsValid: true,
    failureReason: null,
    provenance: replayData(id).run.provenance,
  });
  async function flushPair(view: OperationalReport) {
    http.expectOne(`/api/runs/${ids.run}`).flush(completed());
    http.expectOne(`/api/runs/${ids.otherRun}`).flush(completed(ids.otherRun));
    http.expectOne(`/api/runs/${ids.run}/replay`).flush(await replayEnvelope());
    http.expectOne(`/api/runs/${ids.otherRun}/replay`).flush(await replayEnvelope(candidateData()));
    http.expectOne(`/api/runs/${ids.run}/compare/${ids.otherRun}`).flush(comparisonFixture());
    await until(() => !view.loading());
  }
  it('synchronizes summary, chart and table around the same pair, then changes zones locally', async () => {
    const fixture = await component(),
      view = fixture.componentInstance;
    view.baselineId = ids.run;
    view.candidateId = ids.otherRun;
    view.load();
    await flushPair(view);
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('[data-summary-metric="queuedPassengers"]')!.textContent).toContain(
      '-50%',
    );
    expect(host.querySelectorAll('[data-chart-run-id]')).toHaveLength(2);
    expect(host.querySelector('[data-baseline-run-id]')!.getAttribute('data-baseline-run-id')).toBe(
      ids.run,
    );
    view.page.set(9);
    view.zoneId.set(ids.otherCp);
    view.metricKey.set('meanWaitMinutes');
    view.filterChanged();
    await fixture.whenStable();
    expect(view.page()).toBe(0);
    expect(view.summary().find((row) => row.metric.key === 'meanWaitMinutes')?.percent).toBe(-50);
    expect(view.rows().at(-1)?.percent).toBe(-50);
    expect(view.chart()?.series[0].runId).toBe(ids.run);
    http.expectNone((request) => true);
  });
  it('shows failed diagnostics without requesting a replay or fabricating valid KPI for that run', async () => {
    const fixture = await component(),
      view = fixture.componentInstance;
    view.baselineId = ids.run;
    view.load();
    http.expectOne(`/api/runs/${ids.run}`).flush({
      id: ids.run,
      status: 'failed',
      resultsValid: false,
      failureReason: 'Engine timeout',
      error: { code: 'timeout', message: 'Engine timeout' },
    });
    await fixture.whenStable();
    expect(fixture.nativeElement.textContent).toContain('Engine timeout');
    expect(view.summary().every((row) => row.before === null)).toBe(true);
    expect(fixture.nativeElement.querySelector('svg')).toBeNull();
    http.expectNone(`/api/runs/${ids.run}/replay`);
  });
  it('clears all derived data immediately on selection change and cancels older requests', async () => {
    const fixture = await component(),
      view = fixture.componentInstance;
    view.baselineId = ids.run;
    view.load();
    const request = http.expectOne(`/api/runs/${ids.run}`);
    view.baselineId = ids.otherRun;
    view.selectionChanged();
    expect(request.cancelled).toBe(true);
    expect(view.pair()).toBeNull();
    expect(view.rows()).toEqual([]);
    expect(view.summary()).toEqual([]);
    view.load();
    http
      .expectOne(`/api/runs/${ids.otherRun}`)
      .flush({ ...completed(ids.otherRun), status: 'running', resultsValid: false });
    await fixture.whenStable();
    expect(view.pair()?.baseline.id).toBe(ids.otherRun);
    expect(view.pair()?.baseline.replay).toBeNull();
  });
  it('keeps raw values visible when the comparison service fails and suppresses all differences', async () => {
    const fixture = await component(),
      view = fixture.componentInstance;
    view.baselineId = ids.run;
    view.candidateId = ids.otherRun;
    view.load();
    http.expectOne(`/api/runs/${ids.run}`).flush(completed());
    http.expectOne(`/api/runs/${ids.otherRun}`).flush(completed(ids.otherRun));
    http.expectOne(`/api/runs/${ids.run}/replay`).flush(await replayEnvelope());
    http.expectOne(`/api/runs/${ids.otherRun}/replay`).flush(await replayEnvelope(candidateData()));
    http
      .expectOne(`/api/runs/${ids.run}/compare/${ids.otherRun}`)
      .error(new ProgressEvent('error'));
    await until(() => !view.loading());
    expect(view.summary()[0]).toMatchObject({ before: 4, after: 2, delta: null, percent: null });
    expect(view.comparable()).toBe(false);
  });
  it('rejects a replay for a different run and a comparison response for a different pair', async () => {
    const fixture = await component(),
      view = fixture.componentInstance;
    view.baselineId = ids.run;
    view.candidateId = ids.otherRun;
    view.load();
    http.expectOne(`/api/runs/${ids.run}`).flush(completed());
    http.expectOne(`/api/runs/${ids.otherRun}`).flush(completed(ids.otherRun));
    http.expectOne(`/api/runs/${ids.run}/replay`).flush(await replayEnvelope(candidateData()));
    http.expectOne(`/api/runs/${ids.otherRun}/replay`).flush(await replayEnvelope(candidateData()));
    const comparison = comparisonFixture();
    comparison.baseline.id = ids.mark;
    http.expectOne(`/api/runs/${ids.run}/compare/${ids.otherRun}`).flush(comparison);
    await until(() => !view.loading());
    expect(view.pair()?.baseline.replay).toBeNull();
    expect(view.pair()?.comparison).toBeNull();
    expect(view.summary()[0].before).toBeNull();
  });
});

import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { OperationalReport } from './operational-report';
import { ids, replayData, replayEnvelope } from '../replay/replay.testing';
import { candidateData, comparisonFixture } from './report.testing';

const otherScenario = '30000000-0000-0000-0000-000000000002';
const thirdRun = '10000000-0000-0000-0000-000000000003';
const scenario = (id: string, name: string) => ({
  id,
  name,
  code: name.toLowerCase(),
  description: '',
  sceneId: ids.scene,
  layoutVersionId: ids.layout,
  revision: 1,
});
const listed = (id: string, status: string, hoursAgo: number) => ({
  id,
  status,
  revision: 1,
  seed: 42,
  scenarioRevision: 1,
  retryOfRunId: null,
  resultsValid: status === 'completed',
  finishedAtUtc: new Date(Date.now() - hoursAgo * 3600000).toISOString(),
});
const details = (id: string) => ({
  id,
  status: 'completed',
  resultsValid: true,
  failureReason: null,
  error: null,
  provenance: replayData(id).run.provenance,
});

describe('Operational report page', () => {
  let http: HttpTestingController;
  async function setup(query: Record<string, string> = {}) {
    vi.stubGlobal(
      'crypto',
      (await vi.importActual<{ webcrypto: Crypto }>('node:crypto')).webcrypto,
    );
    TestBed.configureTestingModule({
      imports: [OperationalReport],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { queryParamMap: of(convertToParamMap(query)) } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(OperationalReport);
    await fixture.whenStable();
    return { fixture, view: fixture.componentInstance, host: fixture.nativeElement as HTMLElement };
  }
  afterEach(() => {
    http.verify();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });
  async function until(predicate: () => boolean) {
    await vi.waitFor(() => expect(predicate()).toBe(true), { timeout: 3000, interval: 10 });
  }
  /** The run picker lists scenarios, ranks them by their newest run, then lists the first one's runs. */
  function flushPicker(runs: ReturnType<typeof listed>[]) {
    http.expectOne('/api/scenarios').flush([scenario(ids.scenario, 'Demo')]);
    http.expectOne(`/api/scenarios/${ids.scenario}/runs?limit=1`).flush(runs.slice(0, 1));
    http.expectOne(`/api/scenarios/${ids.scenario}/runs?limit=8`).flush(runs);
  }
  async function flushRun(view: OperationalReport, id: string, data = replayData(id)) {
    http.expectOne(`/api/runs/${id}`).flush(details(id));
    http.expectOne(`/api/runs/${id}/replay`).flush(await replayEnvelope(data));
    await until(() => !view.loading());
  }

  it('opens the newest finished run of the scenario and summarises it without any setup', async () => {
    const { fixture, view, host } = await setup();
    flushPicker([
      listed(thirdRun, 'failed', 1),
      listed(ids.run, 'completed', 2),
      listed(ids.otherRun, 'completed', 5),
    ]);
    await flushRun(view, ids.run);
    await fixture.whenStable();
    expect(host.querySelector('[data-run-id]')?.getAttribute('data-run-id')).toBe(ids.run);
    expect(host.textContent).toContain(
      'Din 20 de pasageri, 12 au trecut de control (60%); nu avem date despre întârzieri la îmbarcare.',
    );
    expect(host.querySelectorAll('[data-card]')).toHaveLength(4);
    expect(host.querySelector('[data-card="meanWait"] .value')?.textContent).toContain('1,3 min');
    expect(host.textContent).toContain('Ce înseamnă:');
    expect(host.textContent).toContain('Cea mai lungă coadă: 20 de pasageri, la 13:00.');
    expect(host.querySelectorAll('details:not([open])')).toHaveLength(1);
    expect(host.querySelectorAll('[data-checkpoint]').length).toBeGreaterThan(0);
    // Only other finished runs can be chosen for the optional comparison.
    const options = [...host.querySelectorAll<HTMLOptionElement>('.compare option')].map(
      (o) => o.value,
    );
    expect(options).toEqual(['', ids.otherRun]);
    expect(host.querySelector('rect.other')).toBeNull();
    host.querySelector<HTMLButtonElement>('[data-export-pdf]')!.click();
    const printFrame = document.querySelector<HTMLIFrameElement>(
      'iframe[title="Raport operațional pentru export PDF"]',
    )!;
    expect(printFrame.srcdoc).toContain(ids.run);
    expect(printFrame.srcdoc).toContain('1,25 min');
    printFrame.remove();
    view.scenarioChanged();
    await fixture.whenStable();
    expect(host.querySelector('[data-export-pdf]')).toBeNull();
  });

  it('adds a comparison run on request, in the cards and as paired columns', async () => {
    const { fixture, view, host } = await setup();
    flushPicker([listed(ids.run, 'completed', 2), listed(ids.otherRun, 'completed', 5)]);
    await flushRun(view, ids.run);
    await fixture.whenStable();
    const select = host.querySelector<HTMLSelectElement>('.compare select')!;
    select.value = ids.otherRun;
    select.dispatchEvent(new Event('change'));
    await fixture.whenStable();
    expect(host.querySelector<HTMLButtonElement>('[data-export-pdf]')?.disabled).toBe(true);
    http.expectOne(`/api/runs/${ids.otherRun}`).flush(details(ids.otherRun));
    http.expectOne(`/api/runs/${ids.otherRun}/replay`).flush(await replayEnvelope(candidateData()));
    http.expectOne(`/api/runs/${ids.run}/compare/${ids.otherRun}`).flush(comparisonFixture());
    await until(() => !!view.otherReplay());
    await fixture.whenStable();
    expect(host.querySelector('[data-card="meanWait"]')?.textContent).toContain(
      'Rularea de comparat: 38 s (acum cu 38 s mai mult)',
    );
    expect(Number(host.querySelector('rect.other')?.getAttribute('height'))).toBeGreaterThan(0);
    expect(host.textContent).toContain('Rularea de comparat');
  });

  it('opens linked runs, but refuses a comparison the server does not confirm', async () => {
    const { fixture, view, host } = await setup({
      baselineRunId: ids.run,
      candidateRunId: ids.otherRun,
    });
    http.expectOne('/api/scenarios').flush([]);
    await flushRun(view, ids.run);
    http.expectOne(`/api/runs/${ids.otherRun}`).flush(details(ids.otherRun));
    http.expectOne(`/api/runs/${ids.otherRun}/replay`).flush(await replayEnvelope(candidateData()));
    const different = comparisonFixture();
    different.comparability!.comparable = false;
    different.comparability!.issues = [
      { code: 'seed_mismatch', message: 'Seed-uri diferite.', severity: 'blocking' },
    ];
    http.expectOne(`/api/runs/${ids.run}/compare/${ids.otherRun}`).flush(different);
    await until(() => !view.comparing());
    await fixture.whenStable();
    expect(host.querySelector('[role="alert"]')?.textContent).toContain(
      'Rulările au fost simulate în condiții diferite, așa că nu le comparăm.',
    );
    expect(view.otherReplay()).toBeNull();
    expect(host.querySelector('.versus')).toBeNull();
    expect(host.querySelector('rect.other')).toBeNull();
    // The linked run stays selectable even though the picker did not list it.
    expect(view.choices().map((choice) => choice.id)).toEqual([ids.otherRun]);
  });

  it('shows why a failed run has no report, without requesting a replay', async () => {
    const { fixture, view, host } = await setup({ baselineRunId: ids.run });
    http.expectOne('/api/scenarios').flush([]);
    http.expectOne(`/api/runs/${ids.run}`).flush({
      ...details(ids.run),
      status: 'failed',
      resultsValid: false,
      failureReason: 'Engine timeout',
    });
    await until(() => !view.loading());
    await fixture.whenStable();
    expect(host.querySelector('[role="alert"]')?.textContent).toContain(
      'Rularea a eșuat: Engine timeout',
    );
    expect(host.querySelector('[data-card]')).toBeNull();
    expect(host.querySelector('svg')).toBeNull();
    http.expectNone(`/api/runs/${ids.run}/replay`);
    expect(host.querySelector('[data-export-pdf]')).toBeNull();
  });

  it('shows a recoverable error if the browser cannot prepare the print document', async () => {
    const { fixture, view, host } = await setup({ baselineRunId: ids.run });
    http.expectOne('/api/scenarios').flush([]);
    await flushRun(view, ids.run);
    vi.spyOn(document.body, 'append').mockImplementationOnce(() => {
      throw new Error('Print unavailable');
    });
    await view.exportPdf();
    await fixture.whenStable();
    expect(host.querySelector('[role="alert"]')?.textContent).toContain(
      'Exportul PDF nu a putut fi pregătit. Încearcă din nou.',
    );
    expect(host.querySelector<HTMLButtonElement>('[data-export-pdf]')?.disabled).toBe(false);
  });

  it('rejects a replay that belongs to another run', async () => {
    const { view } = await setup({ baselineRunId: ids.run });
    http.expectOne('/api/scenarios').flush([]);
    http.expectOne(`/api/runs/${ids.run}`).flush(details(ids.run));
    http.expectOne(`/api/runs/${ids.run}/replay`).flush(await replayEnvelope(candidateData()));
    await until(() => !view.loading());
    expect(view.replay()).toBeNull();
    expect(view.run()?.problem).toContain('nu corespunde');
  });

  it('opens the newest run of a scenario the user picks by hand', async () => {
    const { fixture, view, host } = await setup();
    http
      .expectOne('/api/scenarios')
      .flush([scenario(ids.scenario, 'Demo'), scenario(otherScenario, 'Altul')]);
    http
      .expectOne(`/api/scenarios/${ids.scenario}/runs?limit=1`)
      .flush([listed(ids.run, 'completed', 1)]);
    http
      .expectOne(`/api/scenarios/${otherScenario}/runs?limit=1`)
      .flush([listed(thirdRun, 'completed', 9)]);
    http
      .expectOne(`/api/scenarios/${ids.scenario}/runs?limit=8`)
      .flush([listed(ids.run, 'completed', 1)]);
    await flushRun(view, ids.run);
    await fixture.whenStable();
    const select = host.querySelector<HTMLSelectElement>('app-run-picker select')!;
    select.value = otherScenario;
    select.dispatchEvent(new Event('change', { bubbles: true }));
    expect(view.run()).toBeNull();
    await fixture.whenStable();
    http
      .expectOne(`/api/scenarios/${otherScenario}/runs?limit=8`)
      .flush([listed(ids.otherRun, 'failed', 1), listed(thirdRun, 'completed', 9)]);
    http.expectOne(`/api/runs/${thirdRun}`);
    expect(view.runId()).toBe(thirdRun);
  });
});

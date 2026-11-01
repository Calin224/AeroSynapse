import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { apiErrorInterceptor } from '../../core/http/api-error.interceptor';
import { RunSummary, ScenarioDetails } from '../operations/operations.models';
import { changeLabel, RevisionTimeline, ScenarioRevision } from './revision-timeline';

describe('Scenario revision history', () => {
  let http: HttpTestingController;
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([apiErrorInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());

  const scenario = {
    scenario: { id: 's-1', revision: 3 },
    inputs: {
      timeZone: 'Europe/Bucharest',
      flights: [{ id: 'f-1', code: 'RO301' }],
      events: [
        { id: 'e-1', type: 'checkpoint_unavailable' },
        { id: 'e-2', type: 'incident.passenger-surge' },
        { id: 'e-3', type: 'note' },
      ],
    },
  } as unknown as ScenarioDetails;
  const run = (id: string, scenarioRevision: number, status = 'completed') =>
    ({ id, status, scenarioRevision }) as RunSummary;
  const history: ScenarioRevision[] = [
    {
      revision: 3,
      recordedAtUtc: '2026-10-31T21:43:26Z',
      actor: 'operator.demo',
      changes: [
        { kind: 'flight', action: 'changed', ids: ['f-1'] },
        { kind: 'event', action: 'added', ids: ['e-1'] },
        { kind: 'staffing', action: 'changed', ids: [] },
      ],
      runs: [{ id: 'r-2', status: 'completed' }],
    },
    { revision: 2, recordedAtUtc: '2026-10-31T21:30:00Z', actor: 'system', changes: [], runs: [] },
    {
      revision: 1,
      recordedAtUtc: '2026-10-31T21:00:00Z',
      actor: 'operator.demo',
      changes: [{ kind: 'created', action: 'added', ids: [] }],
      runs: [{ id: 'r-1', status: 'failed' }],
    },
  ];
  function render(runs = [run('r-2', 3), run('r-1', 1, 'failed')]) {
    const fixture = TestBed.createComponent(RevisionTimeline);
    fixture.componentRef.setInput('scenario', scenario);
    fixture.componentRef.setInput('runs', runs);
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    const open = () => {
      const details = element.querySelector('details')!;
      details.open = true;
      details.dispatchEvent(new Event('toggle'));
      fixture.detectChanges();
    };
    return { fixture, element, open };
  }

  it('loads only when opened and tells who changed what, when, and which runs used it', () => {
    const { fixture, element, open } = render();
    http.expectNone('/api/scenarios/s-1/revisions');
    open();
    expect(element.textContent).toContain('Se încarcă istoricul…');
    http.expectOne('/api/scenarios/s-1/revisions').flush(history);
    fixture.detectChanges();
    const rows = [...element.querySelectorAll('ol > li')].map((row) => row.textContent ?? '');
    expect(rows.length).toBe(3);
    expect(rows[0]).toContain('Revizia 3');
    expect(rows[0]).toContain('23:43 · 2026-10-31'); // Scenario zone, not the browser's.
    expect(rows[0]).toContain('operator.demo');
    expect(rows[0]).toContain('Zbor RO301 modificat');
    expect(rows[0]).toContain('Eveniment adăugat: Control indisponibil');
    expect(rows[0]).toContain('Personal modificat');
    expect(rows[1]).toContain('sistem');
    expect(rows[1]).toContain('Fără modificări de conținut');
    expect(rows[2]).toContain('Scenariu creat');
    const runs = [...element.querySelectorAll<HTMLButtonElement>('.runs button')];
    expect(runs.map((button) => button.textContent?.trim())).toEqual(['#2', '#1']);
    expect(runs[1].title).toBe('Eșuată');
    const opened: string[] = [];
    fixture.componentInstance.openRun.subscribe((id) => opened.push(id));
    runs[1].click();
    expect(opened).toEqual(['r-1']);
  });

  it('refreshes when a new run appears and keeps the list visible meanwhile', () => {
    const { fixture, element, open } = render();
    open();
    http.expectOne('/api/scenarios/s-1/revisions').flush(history);
    fixture.componentRef.setInput('runs', [run('r-3', 3, 'running'), run('r-2', 3), run('r-1', 1)]);
    fixture.detectChanges();
    expect(element.querySelectorAll('ol > li').length).toBe(3);
    http.expectOne('/api/scenarios/s-1/revisions').flush(history);
  });

  it('shows the empty and failed states, with a retry', () => {
    const { fixture, element, open } = render();
    open();
    http
      .expectOne('/api/scenarios/s-1/revisions')
      .flush({ title: 'down' }, { status: 503, statusText: 'Unavailable' });
    fixture.detectChanges();
    expect(element.querySelector('[role="alert"]')?.textContent).toContain(
      'Istoricul nu a putut fi încărcat.',
    );
    element.querySelector<HTMLButtonElement>('[role="alert"] button')!.click();
    http.expectOne('/api/scenarios/s-1/revisions').flush([]);
    fixture.detectChanges();
    expect(element.textContent).toContain('Nicio revizie înregistrată încă.');
  });

  it('brings an older revision back from its run as a new revision, only where a run captured it', () => {
    const { fixture, element, open } = render();
    fixture.componentRef.setInput('canRestore', true);
    const restored: ScenarioDetails[] = [];
    fixture.componentInstance.restored.subscribe((value) => restored.push(value));
    open();
    http.expectOne('/api/scenarios/s-1/revisions').flush(history);
    fixture.detectChanges();
    const rows = [...element.querySelectorAll<HTMLElement>('ol > li')];
    // The current revision and a revision without runs cannot be brought back.
    expect(rows[0].textContent).toContain('curentă');
    expect(rows[0].querySelector('.restore')).toBeNull();
    expect(rows[1].querySelector('.restore')).toBeNull();
    rows[2].querySelector<HTMLButtonElement>('.restore')!.click();
    const captured = {
      scenario: { revision: 1, layoutVersionId: 'layout-1' },
      inputs: { seed: 7 },
    };
    http
      .expectOne('/api/runs/r-1')
      .flush({ id: 'r-1', inputSnapshotJson: JSON.stringify({ scenario: captured }) });
    const save = http.expectOne('/api/scenarios/s-1');
    expect(save.request.method).toBe('PUT');
    expect(save.request.body).toMatchObject({
      layoutVersionId: 'layout-1',
      inputs: { seed: 7 },
      revision: 3,
    });
    save.flush({ ...scenario, scenario: { id: 's-1', revision: 4 } });
    fixture.detectChanges();
    expect(restored.map((value) => value.scenario.revision)).toEqual([4]);
    expect(element.textContent).toContain('Revizia 1 a fost restaurată ca revizia 4.');
  });

  it('says plainly when the scenario changed before the restore was saved', () => {
    const { fixture, element, open } = render();
    fixture.componentRef.setInput('canRestore', true);
    open();
    http.expectOne('/api/scenarios/s-1/revisions').flush(history);
    fixture.detectChanges();
    element
      .querySelectorAll<HTMLElement>('ol > li')[2]
      .querySelector<HTMLButtonElement>('.restore')!
      .click();
    http.expectOne('/api/runs/r-1').flush({
      id: 'r-1',
      inputSnapshotJson: JSON.stringify({ scenario: { scenario: { revision: 1 }, inputs: {} } }),
    });
    http
      .expectOne('/api/scenarios/s-1')
      .flush({ title: 'Conflict' }, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();
    expect(element.textContent).toContain('Scenariul s-a modificat între timp');
  });

  it('words counts, removed items and unnamed events plainly', () => {
    const label = (kind: string, action: 'added' | 'changed' | 'removed', ids: string[]) =>
      changeLabel({ kind, action, ids }, scenario);
    expect(label('flight', 'removed', ['gone'])).toBe('Zbor eliminat');
    expect(label('flight', 'added', ['a', 'b', 'c'])).toBe('3 zboruri adăugate');
    expect(
      label(
        'flight',
        'added',
        Array.from({ length: 20 }, (_, i) => `${i}`),
      ),
    ).toBe('20 de zboruri adăugate');
    expect(label('event', 'changed', ['e-2'])).toBe('Eveniment modificat: Aflux de pasageri');
    expect(label('event', 'removed', ['e-3'])).toBe('Eveniment eliminat');
    expect(label('settings', 'changed', [])).toBe('Setări de rulare modificate');
    expect(label('other', 'changed', [])).toBe('Configurație modificată');
  });
});

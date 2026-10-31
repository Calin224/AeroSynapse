import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { PickedRun, RunPicker } from './run-picker';

const scenario = (id: string, name: string) => ({ id, name, code: id.toUpperCase(), revision: 1 });
const run = (id: string, finishedAtUtc: string, status = 'completed') => ({
  id: `${id}-0000-0000-0000-000000000000`.slice(0, 36),
  status,
  seed: 42,
  revision: 1,
  scenarioRevision: 1,
  retryOfRunId: null,
  resultsValid: status === 'completed',
  finishedAtUtc,
});

describe('Run picker', () => {
  let http: HttpTestingController;
  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [RunPicker],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());

  function render(autoSelect: boolean, preferredScenarioId?: string, selected?: string) {
    const fixture = TestBed.createComponent(RunPicker);
    fixture.componentRef.setInput('autoSelect', autoSelect);
    if (preferredScenarioId)
      fixture.componentRef.setInput('preferredScenarioId', preferredScenarioId);
    if (selected) fixture.componentRef.setInput('selected', selected);
    const picked: PickedRun[] = [];
    fixture.componentInstance.picked.subscribe((value) => picked.push(value));
    fixture.detectChanges();
    http.expectOne('/api/scenarios').flush([scenario('a', 'Vechi'), scenario('b', 'Recent')]);
    http
      .expectOne('/api/scenarios/a/runs?limit=1')
      .flush([run('aaaaaaaa', '2026-10-01T10:00:00Z')]);
    http
      .expectOne('/api/scenarios/b/runs?limit=1')
      .flush([run('bbbbbbbb', '2026-10-20T10:00:00Z')]);
    return { fixture, picked };
  }

  it('opens the scenario with the newest run and picks its newest completed run', async () => {
    const { fixture, picked } = render(true);
    http
      .expectOne('/api/scenarios/b/runs?limit=8')
      .flush([
        run('cccccccc', '2026-10-21T10:00:00Z', 'failed'),
        run('bbbbbbbb', '2026-10-20T10:00:00Z'),
      ]);
    await fixture.whenStable();
    expect(fixture.componentInstance.scenarioId()).toBe('b');
    expect(picked).toEqual([
      { runId: run('bbbbbbbb', '').id, scenarioId: 'b', scenarioName: 'Recent' },
    ]);
    const cards = fixture.nativeElement.querySelectorAll(
      'button.run',
    ) as NodeListOf<HTMLButtonElement>;
    expect(cards).toHaveLength(2);
    expect(cards[0]!.disabled).toBe(true);
    expect(cards[0]!.textContent).toContain('Eșuată');
  });

  it('waits for a run card click without a manual identifier form', async () => {
    const { fixture, picked } = render(false);
    http
      .expectOne('/api/scenarios/b/runs?limit=8')
      .flush([run('bbbbbbbb', '2026-10-20T10:00:00Z')]);
    await fixture.whenStable();
    expect(picked).toEqual([]);
    (fixture.nativeElement.querySelector('button.run') as HTMLButtonElement).click();
    expect(picked.map(value => value.runId)).toEqual([run('bbbbbbbb', '').id]);
    expect(fixture.nativeElement.querySelector('.manual')).toBeNull();
  });

  it('shows the remembered scenario and historical run even when another scenario ran more recently', async () => {
    const historical = run('aaaaaaaa', '2026-10-01T10:00:00Z');
    const { fixture, picked } = render(true, 'a', historical.id);
    http.expectOne('/api/scenarios/a/runs?limit=8').flush([historical]);
    await fixture.whenStable();
    expect(fixture.componentInstance.scenarioId()).toBe('a');
    expect(fixture.nativeElement.querySelector('select').value).toBe('a');
    expect(fixture.nativeElement.querySelector('button.run').getAttribute('aria-pressed')).toBe(
      'true',
    );
    expect(picked).toEqual([]);
    http.expectNone('/api/scenarios/b/runs?limit=8');
  });

  it('aligns the selector when the selected run finishes loading after the scenario catalog', async () => {
    const { fixture } = render(false);
    http.expectOne('/api/scenarios/b/runs?limit=8').flush([]);
    fixture.componentRef.setInput('preferredScenarioId', 'a');
    fixture.detectChanges();
    http.expectOne('/api/scenarios/a/runs?limit=8').flush([]);
    await fixture.whenStable();
    expect(fixture.componentInstance.scenarioId()).toBe('a');
  });
});

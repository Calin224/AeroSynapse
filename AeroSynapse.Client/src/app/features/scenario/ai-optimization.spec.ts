import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { SessionState } from '../../core/auth/session-state';
import { apiErrorInterceptor } from '../../core/http/api-error.interceptor';
import { AiOptimization, RunOptimization } from './ai-optimization';

describe('AI optimization after a run', () => {
  let http: HttpTestingController;
  const canWrite = signal(true);
  beforeEach(() => {
    canWrite.set(true);
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([apiErrorInterceptor])),
        provideHttpClientTesting(),
        { provide: SessionState, useValue: { canWrite } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());

  const side = (meanWaitMinutes: number | null, maxQueue: number) => ({
    meanWaitMinutes,
    maxP95WaitMinutes: null,
    maxQueue,
    queueClearedMinute: null,
  });
  const answer: RunOptimization = {
    modelStatus: 'chosen',
    model: 'gemini-3.8-flash',
    summary:
      'Cea mai lungă coadă: 40 de pasageri la CP-1, în minutul 30. Mută un operator de la CP-3 la CP-1: scurtează așteptarea fără personal în plus.',
    baseline: side(10, 40),
    options: [
      {
        id: 'move',
        kind: 'move',
        action: 'Mută un operator de la CP-3 la CP-1',
        operatorDelta: 0,
        result: side(4, 12),
        explanation: 'Pasagerii așteaptă mult mai puțin, fără personal în plus.',
        recommended: true,
        staffDelta: { 'cp-3': -1, 'cp-1': 1 },
      },
      {
        id: 'save',
        kind: 'save',
        action: 'Retrage un operator de la CP-3',
        operatorDelta: -1,
        result: side(0.5, 40),
        explanation: 'Așteptarea rămâne practic aceeași și se economisește un operator.',
        recommended: false,
        staffDelta: { 'cp-3': -1 },
      },
      {
        id: 'desk-2',
        kind: 'desk',
        action: 'Deschide două ghișee în plus la CHECK-IN',
        operatorDelta: 2,
        result: side(3, 8),
        explanation: 'Pasagerii așteaptă în medie cu 7 min mai puțin, cu două ghișee de check-in în plus.',
        recommended: false,
        staffDelta: {},
        deskDelta: 2,
      },
    ],
    checkIn: { name: 'CHECK-IN', open: 8, desks: 12 },
    notice: 'Nu există niciun operator de securitate liber pentru CP-1: toți oamenii scenariului sunt deja alocați.',
  };

  it('says the scenario is already optimal and keeps the tested options folded away', () => {
    const fixture = TestBed.createComponent(AiOptimization);
    fixture.componentRef.setInput('runId', 'run-1');
    fixture.detectChanges();
    const calm = {
      ...answer,
      optimal: true,
      options: answer.options.map((option) => ({ ...option, recommended: false })),
    };
    http.expectOne('/api/runs/run-1/ai-optimization').flush(calm);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('.optimal')?.textContent).toContain('Scenariul este deja optim');
    expect(root.textContent).not.toContain(answer.summary!);
    expect(root.querySelector('.badge')).toBeNull();
    const tested = root.querySelector('details.tested') as HTMLDetailsElement;
    expect(tested.open).toBe(false);
    expect(tested.querySelectorAll('article')).toHaveLength(answer.options.length);
  });

  it('shows the simulated options for the run, the recommended one marked', () => {
    const fixture = TestBed.createComponent(AiOptimization);
    fixture.componentRef.setInput('runId', 'run-1');
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('testează variante');
    http.expectOne('/api/runs/run-1/ai-optimization').flush(answer);
    fixture.detectChanges();
    const cards = fixture.nativeElement.querySelectorAll('article') as NodeListOf<HTMLElement>;
    expect(cards).toHaveLength(3);
    expect(cards[0].classList).toContain('best');
    expect(cards[0].textContent).toContain('10 min → 4 min');
    expect(cards[0].textContent).toContain('-60%');
    expect(cards[0].textContent).toContain('Fără personal suplimentar');
    expect(cards[1].textContent).toContain('30 s');
    expect(cards[1].textContent).toContain('Economisește 1 operator');
    expect(cards[0].textContent).not.toContain('Economie estimată');
    // 600 passengers × 6 min = 60 h × 22,89 €, no staff added; only the recommended card says it.
    fixture.componentRef.setInput('passengers', 600);
    fixture.componentRef.setInput('durationMinutes', 120);
    fixture.detectChanges();
    expect(cards[0].textContent).toContain('Economie estimată: 1.373 €');
    expect(cards[1].textContent).not.toContain('Economie estimată');
    expect(fixture.nativeElement.querySelector('.summary').textContent).toContain(answer.summary!);
    // The action is the card's title, said once, with the explanation under the numbers.
    expect(cards[0].querySelector('h4')?.textContent).toBe('Mută un operator de la CP-3 la CP-1');
    expect(cards[0].textContent?.split('Mută un operator de la CP-3 la CP-1')).toHaveLength(2);
    expect(cards[0].querySelector('.why')?.textContent).toBe(answer.options[0].explanation);
    expect(fixture.nativeElement.querySelector('.note').textContent).toContain('AI-ul doar le explică');
  });

  it('says when the model did not answer, with the numbers and the server\'s own words still shown', () => {
    const fixture = TestBed.createComponent(AiOptimization);
    fixture.componentRef.setInput('runId', 'run-1');
    fixture.detectChanges();
    http.expectOne('/api/runs/run-1/ai-optimization').flush({ ...answer, modelStatus: 'timeout' });
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    const source = root.querySelector('.source') as HTMLElement;
    expect(source.textContent).toContain('calcul automat');
    expect(source.title).toBe('Modelul AI nu a răspuns la timp');
    expect(root.querySelector('.summary')?.textContent).toContain(answer.summary!);
    expect(root.querySelector('.note')?.textContent).not.toContain('AI-ul');
    expect(root.querySelectorAll('.why')).toHaveLength(answer.options.length);
  });

  it('offers a retry when the options cannot be computed', () => {
    const fixture = TestBed.createComponent(AiOptimization);
    fixture.componentRef.setInput('runId', 'run-1');
    fixture.detectChanges();
    http.expectOne('/api/runs/run-1/ai-optimization').flush(null, { status: 500, statusText: 'x' });
    fixture.detectChanges();
    (fixture.nativeElement.querySelector('button') as HTMLButtonElement).click();
    http.expectOne('/api/runs/run-1/ai-optimization').flush(answer);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelectorAll('article')).toHaveLength(3);
  });

  function shown() {
    const fixture = TestBed.createComponent(AiOptimization);
    fixture.componentRef.setInput('runId', 'run-1');
    fixture.detectChanges();
    http.expectOne('/api/runs/run-1/ai-optimization').flush(answer);
    fixture.detectChanges();
    const cards = fixture.nativeElement.querySelectorAll('article') as NodeListOf<HTMLElement>;
    const button = (card: HTMLElement) => card.querySelector('.apply button') as HTMLButtonElement;
    return { fixture, cards, button };
  }

  it('applies an option to the scenario and tells the page', () => {
    const { fixture, cards, button } = shown();
    const applied = vi.fn();
    fixture.componentInstance.applied.subscribe(applied);
    expect(button(cards[0]).textContent).toContain('Aplică în scenariu');
    button(cards[0]).click();
    fixture.detectChanges();
    expect(button(cards[1]).disabled).toBe(true);
    const request = http.expectOne('/api/runs/run-1/ai-optimization/apply');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ staffDelta: { 'cp-3': -1, 'cp-1': 1 }, deskDelta: 0 });
    request.flush({});
    expect(applied).toHaveBeenCalledTimes(1);
  });

  it('shows desk options as desks, applies their desks and says why no operator is added', () => {
    const { fixture, cards, button } = shown();
    expect(cards[2].textContent).toContain('+2 ghișee check-in');
    expect(cards[2].textContent).toContain('Așteptare medie (check-in + control)');
    expect(fixture.nativeElement.textContent).toContain('Nu există niciun operator de securitate liber pentru CP-1');
    button(cards[2]).click();
    const request = http.expectOne('/api/runs/run-1/ai-optimization/apply');
    expect(request.request.body).toEqual({ staffDelta: {}, deskDelta: 2 });
    request.flush({});
  });

  it('does not overwrite a scenario that changed since the run', () => {
    const { fixture, cards, button } = shown();
    const applied = vi.fn();
    fixture.componentInstance.applied.subscribe(applied);
    button(cards[1]).click();
    http
      .expectOne('/api/runs/run-1/ai-optimization/apply')
      .flush({ title: 'changed' }, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();
    expect(applied).not.toHaveBeenCalled();
    expect(cards[1].querySelector('[role="alert"]')?.textContent).toContain(
      'Scenariul s-a modificat după această simulare',
    );
  });

  it('hides the apply button from read-only users', () => {
    canWrite.set(false);
    const { cards, button } = shown();
    expect(button(cards[0]).closest('.apply')!.hasAttribute('hidden')).toBe(true);
  });

  it('offers no apply once the scenario moved past the simulated revision, and says why', () => {
    const fixture = TestBed.createComponent(AiOptimization);
    fixture.componentRef.setInput('runId', 'run-1');
    fixture.componentRef.setInput('stale', true);
    fixture.detectChanges();
    http.expectOne('/api/runs/run-1/ai-optimization').flush(answer);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelectorAll('article')).toHaveLength(answer.options.length);
    expect(fixture.nativeElement.querySelector('.apply')).toBeNull();
    expect(fixture.nativeElement.textContent).toContain('Scenariul a fost modificat după această simulare');
  });

  it('cancels the previous run\'s request when the run changes, so its answer never shows', () => {
    const fixture = TestBed.createComponent(AiOptimization);
    fixture.componentRef.setInput('runId', 'run-1');
    fixture.detectChanges();
    const first = http.expectOne('/api/runs/run-1/ai-optimization');
    fixture.componentRef.setInput('runId', 'run-2');
    fixture.detectChanges();
    expect(first.cancelled).toBe(true);
    http.expectOne('/api/runs/run-2/ai-optimization').flush(null);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('nu există variante');
    expect(fixture.componentInstance.loading()).toBe(false);
  });
});

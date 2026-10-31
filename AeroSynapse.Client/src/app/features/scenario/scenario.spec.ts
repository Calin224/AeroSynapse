import { Component, input, output } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, convertToParamMap, provideRouter, Router } from '@angular/router';
import { of } from 'rxjs';
import { Scenario } from './scenario';
import { By } from '@angular/platform-browser';
import { SimulationPanel } from './simulation-panel';

@Component({ selector: 'app-simulation-panel', template: '' })
class SimulationPanelStub {
  readonly scenarioId = input<string | null>(null);
  readonly finished = output<string>();
  readonly scenarioSelected = output<string>();
  readonly working = output<boolean>();
}

const scenarios = [
  { id: 'first', name: 'Orar de dimineață', code: 'AM', description: 'Dimineața', revision: 1 },
  { id: 'second', name: 'Orar de seară', code: 'PM', description: 'Seara', revision: 2 },
];

describe('Scenariile mele', () => {
  let http: HttpTestingController;
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });
    TestBed.overrideComponent(Scenario, {
      remove: { imports: [SimulationPanel] },
      add: { imports: [SimulationPanelStub] },
    });
    http = TestBed.inject(HttpTestingController);
    vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
  });
  afterEach(() => http.verify());
  async function render() {
    const fixture = TestBed.createComponent(Scenario);
    fixture.detectChanges();
    http.expectOne('/api/scenarios').flush(scenarios);
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture;
  }
  it('asks the user to choose and only loads results for the chosen scenario', async () => {
    const fixture = await render();
    expect(fixture.nativeElement.textContent).toContain('Alege un scenariu salvat');
    http.expectNone((request) => request.url.includes('/runs'));
    const button = Array.from(
      fixture.nativeElement.querySelectorAll('button') as NodeListOf<HTMLButtonElement>,
    ).find((item) => item.textContent?.includes('Orar de seară'))!;
    button.click();
    fixture.detectChanges();
    await fixture.whenStable();
    http.expectOne('/api/scenarios/second/runs?limit=100').flush([]);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Pregătit pentru prima simulare');
    const panel = fixture.debugElement.query(By.directive(SimulationPanelStub));
    expect(fixture.nativeElement.querySelector('select').value).toBe('second');
    expect(panel?.componentInstance.scenarioId()).toBe('second');
  });
  it('opens the exact saved scenario supplied in the link', async () => {
    Object.defineProperty(TestBed.inject(ActivatedRoute), 'snapshot', {
      value: { queryParamMap: convertToParamMap({ scenarioId: 'second' }) },
    });
    Object.defineProperty(TestBed.inject(ActivatedRoute), 'queryParamMap', {
      value: of(convertToParamMap({ scenarioId: 'second' })),
    });
    const fixture = await render();
    http.expectOne('/api/scenarios/second/runs?limit=100').flush([]);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('select').value).toBe('second');
    expect(fixture.nativeElement.textContent).toContain('Pregătit pentru prima simulare');
  });
});

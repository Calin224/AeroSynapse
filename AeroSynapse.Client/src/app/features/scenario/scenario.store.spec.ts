import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ScenarioStore } from './scenario.store';
import { storedRun } from '../control-tower/run-overview.testing';
import { SimulationContext } from '../../core/simulation-context';

describe('Scenario preview selection', () => {
  let http: HttpTestingController, store: ScenarioStore;
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [ScenarioStore, provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
    store = TestBed.inject(ScenarioStore);
    http.expectOne('/api/scenarios').flush([]);
  });
  afterEach(() => http.verify());
  it('shows the requested historical run instead of replacing it with the newest run', () => {
    const old = storedRun({ id: 'old', scenarioId: 'scenario-1' });
    const remember = vi.spyOn(TestBed.inject(SimulationContext), 'remember');
    let selected: string | undefined;
    store.preview('scenario-1', 'old').subscribe((value) => {
      if (value.status === 'ready') selected = value.overview.runId;
    });
    http
      .expectOne('/api/scenarios/scenario-1/runs?limit=100')
      .flush([storedRun({ id: 'new' }), old]);
    http.expectOne('/api/scenarios/scenario-1/runs/old').flush(old);
    expect(selected).toBe('old');
    expect(remember).toHaveBeenLastCalledWith('scenario-1', 'old');
    http.expectNone('/api/scenarios/scenario-1/runs/new');
  });
  it('reports an unavailable selected run rather than silently showing another one', () => {
    let status = '';
    store.preview('scenario-1', 'missing').subscribe((value) => (status = value.status));
    http.expectOne('/api/scenarios/scenario-1/runs?limit=100').flush([storedRun({ id: 'new' })]);
    expect(status).toBe('error');
    http.expectNone('/api/scenarios/scenario-1/runs/new');
  });
});

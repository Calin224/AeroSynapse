import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { Subject } from 'rxjs';
import { OperationsStream } from './core/http/operations-stream';
import { ids, towerFixture } from './features/control-tower/tower.testing';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { routes } from './app.routes';
import { AuthService } from './core/auth/auth-service';
import { apiErrorInterceptor } from './core/http/api-error.interceptor';
import { ControlTower } from './features/control-tower/control-tower';
import { noWebGl, stubScenarioPanels } from './features/scenario/scenario.testing';
import { NotFound } from './shared/not-found';

describe('Feature routes and data states', () => {
  let http: HttpTestingController;
  beforeEach(() => {
    sessionStorage.clear();
    TestBed.configureTestingModule({
      providers: [
        provideRouter(routes),
        {
          provide: OperationsStream,
          useValue: {
            state: signal(null),
            updates: new Subject(),
            connect: vi.fn(),
            status: signal('polling'),
            error: signal(''),
          },
        },
        provideHttpClient(withInterceptors([apiErrorInterceptor])),
        provideHttpClientTesting(),
        noWebGl,
      ],
    });
    stubScenarioPanels('polling');
    http = TestBed.inject(HttpTestingController);
    const auth = TestBed.inject(AuthService);
    auth.state.session.set({
      username: 'feature-test',
      roles: ['Operator'],
      expiresAtUtc: '2099-01-01T00:00:00Z',
    });
    vi.spyOn(auth, 'restore').mockResolvedValue(true);
  });
  afterEach(() => http.verify());
  it('redirects / to Control Tower and offers Scenario Studio when no scenarios exist', async () => {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/', ControlTower);
    expect(TestBed.inject(Router).url).toBe('/control-tower');
    expect(harness.routeNativeElement?.textContent).toContain('Se încarcă');
    http.expectOne('/api/scenarios').flush([]);
    harness.detectChanges();
    expect(harness.routeNativeElement?.textContent).toContain('Niciun scenariu disponibil');
    expect(
      harness.routeNativeElement?.querySelector('a[href="/control-tower/configure"]'),
    ).toBeTruthy();
  });
  it('recovers from HTTP failure and keeps incidents, KPI charts and resources usable after WebGL fails', async () => {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(
      `/control-tower?scenarioId=${ids.scenario}&runId=${ids.run}&zoneId=${ids.cp}`,
      ControlTower,
    );
    http.expectOne('/api/scenarios').flush({}, { status: 500, statusText: 'Failure' });
    harness.detectChanges();
    expect(harness.routeNativeElement?.textContent).toContain('Datele nu au putut fi încărcate');
    (harness.routeNativeElement?.querySelector('.notice button') as HTMLButtonElement).click();
    const { scenario, layout, run } = towerFixture();
    http.expectOne('/api/scenarios').flush([scenario.scenario]);
    http.expectOne(`/api/scenarios/${ids.scenario}`).flush(scenario);
    http.expectOne(`/api/scenes/${ids.scene}/layout-versions/${ids.layout}`).flush(layout);
    http.expectOne(`/api/scenarios/${ids.scenario}/runs?limit=100`).flush([run]);
    await vi.waitFor(() =>
      http.expectOne(`/api/scenarios/${ids.scenario}/runs/${ids.run}`).flush(run),
    );
    harness.detectChanges();
    await harness.fixture.whenStable();
    harness.detectChanges();
    const host = harness.routeNativeElement!;
    expect(host.textContent).toContain('Replay demo');
    expect(host.textContent).toContain('Vârf de sosire');
    expect(host.querySelector('app-terminal-map')).toBeTruthy();
    expect(host.querySelectorAll('app-tower-trends svg')).toHaveLength(2);
    expect(host.querySelector('.resource-panel tr.focused-zone th')?.textContent).toContain('CP1');
    expect(host.querySelector('app-run-summary')?.textContent).toContain('12 din 20');
    expect(host.querySelector('.simulation-workspace')?.getAttribute('data-run-id')).toBe(ids.run);
  });
  it("redirects saved scenarios to Control Tower and previews the selected scenario's run", async () => {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(
      `/scenario?scenarioId=${ids.scenario}&runId=${ids.run}#simulation`,
      ControlTower,
    );
    expect(TestBed.inject(Router).url).toBe(
      `/control-tower?scenarioId=${ids.scenario}&runId=${ids.run}#simulation`,
    );
    const { scenario, layout, run } = towerFixture();
    const second = structuredClone(scenario);
    second.scenario.id = ids.otherRun;
    second.scenario.name = 'Trafic redus';
    const catalog = [scenario.scenario, second.scenario];
    http.expectOne('/api/scenarios').flush(catalog);
    http.expectOne(`/api/scenarios/${ids.scenario}`).flush(scenario);
    http.expectOne(`/api/scenes/${ids.scene}/layout-versions/${ids.layout}`).flush(layout);
    http.expectOne(`/api/scenarios/${ids.scenario}/runs?limit=100`).flush([run]);
    await vi.waitFor(() =>
      http.expectOne(`/api/scenarios/${ids.scenario}/runs/${ids.run}`).flush(run),
    );
    await harness.fixture.whenStable();
    harness.detectChanges();
    expect(harness.routeNativeElement?.querySelector('#summary-title')?.textContent).toContain(
      scenario.scenario.name,
    );
    expect(harness.routeNativeElement?.querySelector('#run-summary-title')).toBeTruthy();
    expect(harness.routeNativeElement?.textContent).toContain('12 din 20');
    const browse = [
      ...harness.routeNativeElement!.querySelectorAll<HTMLButtonElement>('.page-actions button'),
    ].find((button) => button.textContent?.includes('Toate scenariile'))!;
    browse.click();
    harness.detectChanges();
    const alternative = [
      ...harness.routeNativeElement!.querySelectorAll<HTMLButtonElement>('.gallery .card'),
    ].find((button) => button.textContent?.includes(second.scenario.name))!;
    alternative.click();
    await vi.waitFor(() => http.expectOne('/api/scenarios').flush(catalog));
    http.expectOne(`/api/scenarios/${ids.otherRun}`).flush(second);
    http.expectOne(`/api/scenes/${ids.scene}/layout-versions/${ids.layout}`).flush(layout);
    http.expectOne(`/api/scenarios/${ids.otherRun}/runs?limit=100`).flush([]);
    await harness.fixture.whenStable();
    harness.detectChanges();
    expect(harness.routeNativeElement?.querySelector('#summary-title')?.textContent).toContain(
      second.scenario.name,
    );
    expect(harness.routeNativeElement?.querySelector('.gallery')).toBeNull();
    expect(harness.routeNativeElement?.textContent).toContain('Pregătit pentru prima simulare');
  });
  it.each([
    '/missing',
    '/passenger',
    '/passenger?scenarioId=scenario-1&runId=run-1&flightId=demo-202',
    '/evacuation-training',
    '/demo-replay',
    '/operation-bottleneck',
    '/external-data',
    '/audit',
    '/admin/ai-governance',
    '/admin/users',
  ])('renders a useful fallback for %s', async (url) => {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(url, NotFound);
    expect(harness.routeNativeElement?.textContent).toContain('Pagina nu există');
  });
  it('cancels the pending dashboard request when leaving the feature', async () => {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/control-tower', ControlTower);
    const request = http.expectOne('/api/scenarios');
    await harness.navigateByUrl('/missing', NotFound);
    expect(request.cancelled).toBe(true);
  });
});

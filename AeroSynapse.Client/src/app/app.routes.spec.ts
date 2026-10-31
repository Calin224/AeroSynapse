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
import { ControlTowerStore } from './features/control-tower/control-tower.store';
import { ControlTower } from './features/control-tower/control-tower';
import { Scenario } from './features/scenario/scenario';
import { TWIN_RENDERER_FACTORY } from './features/digital-twin/digital-twin';
import { NotFound } from './shared/not-found';
import { storedRun } from './features/control-tower/run-overview.testing';

describe('Feature routes and data states', () => {
  let http: HttpTestingController;
  beforeEach(() => {
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
        {
          provide: TWIN_RENDERER_FACTORY,
          useValue: () => {
            throw new Error('WebGL unavailable');
          },
        },
      ],
    });
    TestBed.overrideComponent(ControlTower, {
      set: {
        providers: [
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
          ControlTowerStore,
        ],
      },
    });
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
    expect(harness.routeNativeElement?.textContent).toContain('Deschide Scenario Studio');
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
    expect(host.textContent).toContain('13:06:00');
    expect(host.querySelector('app-terminal-map')).toBeTruthy();
    expect(host.querySelectorAll('app-tower-trends svg')).toHaveLength(2);
    expect(host.querySelector('[data-focused-zone]')?.getAttribute('data-focused-zone')).toBe(
      ids.cp,
    );
    expect(host.querySelector('[data-testid="tower-processed"]')?.textContent?.trim()).toBe('12');
    expect(host.querySelector('.workspace-grid')?.getAttribute('data-run-id')).toBe(ids.run);
  });
  it("lists saved scenarios and previews the selected scenario's latest run", async () => {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/scenario', Scenario);
    const scenarios = [
      { id: 'scenario-1', code: 'DEMO-1', name: 'Vârf de seară', revision: 1 },
      { id: 'scenario-2', code: 'DEMO-2', name: 'Trafic redus', revision: 2 },
    ];
    http.match('/api/scenarios').forEach((request) => request.flush(scenarios));
    harness.detectChanges();
    const choices =
      harness.routeNativeElement!.querySelectorAll<HTMLButtonElement>('.scenario-option');
    expect(choices).toHaveLength(2);
    http.expectNone('/api/scenarios/scenario-1/runs?limit=100');
    choices[0].click();
    harness.detectChanges();
    http.expectOne('/api/scenarios/scenario-1/runs?limit=100').flush([storedRun()]);
    http.expectOne('/api/scenarios/scenario-1/runs/run-1').flush(storedRun());
    await harness.fixture.whenStable();
    harness.detectChanges();
    const selected = harness.routeNativeElement!.querySelector<HTMLSelectElement>(
      '.selected-toolbar select',
    )!;
    expect(selected.value).toBe('scenario-1');
    expect(harness.routeNativeElement?.querySelector('#run-summary-title')?.textContent).toContain(
      'Flux fluent.',
    );
    expect(harness.routeNativeElement?.textContent).toContain('90 din 200');
    selected.value = 'scenario-2';
    selected.dispatchEvent(new Event('change'));
    harness.detectChanges();
    http.expectOne('/api/scenarios/scenario-2/runs?limit=100').flush([]);
    harness.detectChanges();
    expect(selected.value).toBe('scenario-2');
    expect(harness.routeNativeElement?.textContent).toContain('Pregătit pentru prima simulare');
  });
  it('shows an empty Scenario collection', async () => {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/scenario', Scenario);
    http.match('/api/scenarios').forEach((request) => request.flush([]));
    harness.detectChanges();
    expect(harness.routeNativeElement?.textContent).toContain('Nu există scenarii salvate');
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

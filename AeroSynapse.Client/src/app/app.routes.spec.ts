import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { routes } from './app.routes';
import { apiErrorInterceptor } from './core/http/api-error.interceptor';
import { ControlTower } from './features/control-tower/control-tower';
import { Scenario } from './features/scenario/scenario';
import { TWIN_RENDERER_FACTORY } from './features/digital-twin/digital-twin';
import { NotFound } from './shared/not-found';
import { Operations } from './features/operations/operations';

describe('Feature routes and data states', () => {
  let http: HttpTestingController;
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter(routes),
        provideHttpClient(withInterceptors([apiErrorInterceptor])),
        provideHttpClientTesting(),
        {
          provide: TWIN_RENDERER_FACTORY,
          useValue: () => ({ setScene: vi.fn(), resetCamera: vi.fn(), dispose: vi.fn() }),
        },
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());
  it('opens the operations page and explains empty airport and scenario lists', async () => {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/operations', Operations);
    http.expectOne('/api/scenes').flush([]);
    http.expectOne('/api/scenarios').flush([]);
    harness.detectChanges();
    expect(harness.routeNativeElement?.textContent).toContain('Scenarii și rulări');
    expect(harness.routeNativeElement?.textContent).toContain(
      'Nu există aeroporturi operaționale configurate',
    );
    expect(harness.routeNativeElement?.textContent).toContain('Creează primul scenariu');
  });
  it('redirects / to Control Tower and displays loading then empty', async () => {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/', ControlTower);
    expect(TestBed.inject(Router).url).toBe('/control-tower');
    expect(harness.routeNativeElement?.textContent).toContain('Se încarcă');
    http.expectOne('/data/control-tower.json').flush(null);
    harness.detectChanges();
    expect(harness.routeNativeElement?.textContent).toContain('Nu există un aeroport configurat');
  });
  it('shows HTTP failure and recovers using the retry button', async () => {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/control-tower', ControlTower);
    http.expectOne('/data/control-tower.json').flush({}, { status: 500, statusText: 'Failure' });
    harness.detectChanges();
    expect(harness.routeNativeElement?.textContent).toContain('Datele nu au putut fi încărcate');
    harness.routeNativeElement
      ?.querySelector('app-data-state button')
      ?.dispatchEvent(new MouseEvent('click'));
    http.expectOne('/data/control-tower.json').flush({
      airport: 'Demo Airport',
      code: 'TEST',
      metrics: [],
      flights: [],
      scene: { aircraft: [] },
    });
    harness.detectChanges();
    expect(harness.routeNativeElement?.textContent).toContain('Demo Airport');
  });
  it('opens Scenario and switches the selected scene', async () => {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/scenario', Scenario);
    http.expectOne('/data/scenarios.json').flush([
      {
        id: 'first',
        name: 'Standard',
        description: 'Standard',
        impact: '4 aircraft',
        scene: { aircraft: [] },
      },
      {
        id: 'second',
        name: 'Peak',
        description: 'Peak',
        impact: '6 aircraft',
        scene: { aircraft: [] },
      },
    ]);
    harness.detectChanges();
    const choices =
      harness.routeNativeElement!.querySelectorAll<HTMLButtonElement>('.scenario-option');
    expect(choices[0].getAttribute('aria-pressed')).toBe('true');
    choices[1].click();
    harness.detectChanges();
    expect(choices[1].getAttribute('aria-pressed')).toBe('true');
    expect(harness.routeNativeElement?.textContent).toContain('6 aircraft');
  });
  it('shows an empty Scenario collection', async () => {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/scenario', Scenario);
    http.expectOne('/data/scenarios.json').flush([]);
    harness.detectChanges();
    expect(harness.routeNativeElement?.textContent).toContain('Nu există scenarii disponibile');
  });
  it('renders a useful fallback for an unknown URL', async () => {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/missing', NotFound);
    expect(harness.routeNativeElement?.textContent).toContain('Pagina nu există');
  });
  it('cancels the pending dashboard request when leaving the feature', async () => {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/control-tower', ControlTower);
    const request = http.expectOne('/data/control-tower.json');
    await harness.navigateByUrl('/missing', NotFound);
    expect(request.cancelled).toBe(true);
  });
});

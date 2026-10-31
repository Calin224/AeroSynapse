import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { routes } from './app.routes';
import { AuthService } from './core/auth/auth-service';
import { AppHeader } from './core/layout/app-header';
import { EnergyAnalytics } from './features/energy/energy-analytics';
import { AccessDenied } from './features/identity/access-denied';
import { NotFound } from './shared/not-found';

describe('Optional energy analytics route', () => {
  let http: HttpTestingController;
  beforeEach(() => {
    window.__AEROSYNAPSE_CONFIG__ = {
      apiBaseUrl: '/api',
      intelligenceBaseUrl: '/intelligence',
      energyAnalyticsEnabled: false,
    };
    TestBed.configureTestingModule({
      providers: [provideRouter(routes), provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
    const auth = TestBed.inject(AuthService);
    auth.state.session.set({
      username: 'energy-test',
      roles: ['Operator'],
      expiresAtUtc: '2099-01-01T00:00:00Z',
    });
    vi.spyOn(auth, 'restore').mockResolvedValue(true);
  });
  afterEach(() => {
    http.verify();
    delete window.__AEROSYNAPSE_CONFIG__;
  });

  it('keeps energy analytics optional without starting simulations or altering security', async () => {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/energy', NotFound);
    http.expectNone((request) => request.url.includes('/energy/'));
  });

  it('opens the synthetic energy analyst page after enabling it', async () => {
    window.__AEROSYNAPSE_CONFIG__!.energyAnalyticsEnabled = true;
    const harness = await RouterTestingHarness.create();
    expect(await harness.navigateByUrl('/energy', EnergyAnalytics)).toBeInstanceOf(EnergyAnalytics);
    http.expectNone((request) => request.url.includes('/api/scenarios'));
  });

  it('shows the energy link independently of other optional simulation modules', () => {
    window.__AEROSYNAPSE_CONFIG__!.energyAnalyticsEnabled = true;
    const fixture = TestBed.createComponent(AppHeader);
    fixture.detectChanges();
    http.expectNone('/api/health');
    http.expectNone('/intelligence/health');
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('a[href="/energy"]')).toBeTruthy();
    expect(root.querySelector('a[href="/airside"]')).toBeNull();
    expect(root.querySelector('a[href="/control-tower"]')).toBeTruthy();
  });

  it('prevents Viewers from opening the energy calculations', async () => {
    window.__AEROSYNAPSE_CONFIG__!.energyAnalyticsEnabled = true;
    TestBed.inject(AuthService).state.session.update((session) => ({ ...session!, roles: ['Viewer'] }));
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/energy', AccessDenied);
    http.expectNone((request) => request.url.includes('/energy/'));
  });

  it('hides the energy calculation link from Viewers', () => {
    window.__AEROSYNAPSE_CONFIG__!.energyAnalyticsEnabled = true;
    TestBed.inject(AuthService).state.session.update((session) => ({ ...session!, roles: ['Viewer'] }));
    const fixture = TestBed.createComponent(AppHeader);
    fixture.detectChanges();
    http.expectNone('/api/health');
    http.expectNone('/intelligence/health');
    expect((fixture.nativeElement as HTMLElement).querySelector('a[href="/energy"]')).toBeNull();
  });
});

import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { routes } from './app.routes';
import { AuthService } from './core/auth/auth-service';
import { AppHeader } from './core/layout/app-header';
import { AirsideTwin } from './features/airside/airside-twin';
import { NotFound } from './shared/not-found';

describe('Optional airside route and navigation', () => {
  let http: HttpTestingController;
  beforeEach(() => {
    window.__AEROSYNAPSE_CONFIG__ = {
      apiBaseUrl: '/api',
      intelligenceBaseUrl: '/intelligence',
      airsideTwinEnabled: false,
    };
    TestBed.configureTestingModule({
      providers: [provideRouter(routes), provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
    const auth = TestBed.inject(AuthService);
    auth.state.session.set({
      username: 'airside-test',
      roles: ['Operator'],
      expiresAtUtc: '2099-01-01T00:00:00Z',
    });
    vi.spyOn(auth, 'restore').mockResolvedValue(true);
  });
  afterEach(() => {
    http.verify();
    delete window.__AEROSYNAPSE_CONFIG__;
  });

  it('keeps the optional planner unreachable by default without requesting operational data', async () => {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/airside', NotFound);
    http.expectNone((request) => request.url.includes('airside'));
  });

  it('opens the synthetic planner only after explicitly enabling it', async () => {
    window.__AEROSYNAPSE_CONFIG__!.airsideTwinEnabled = true;
    const harness = await RouterTestingHarness.create();
    const component = await harness.navigateByUrl('/airside', AirsideTwin);
    expect(component).toBeInstanceOf(AirsideTwin);
    http.expectNone((request) => request.url.includes('/api/scenarios'));
  });

  it('shows the airside navigation independently of baggage and terminal routes', () => {
    window.__AEROSYNAPSE_CONFIG__!.airsideTwinEnabled = true;
    const fixture = TestBed.createComponent(AppHeader);
    fixture.detectChanges();
    http.expectNone('/api/health');
    http.expectNone('/intelligence/health');
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('a[href="/airside"]')).toBeTruthy();
    expect(root.querySelector('a[href="/baggage"]')).toBeNull();
    expect(root.querySelector('a[href="/control-tower"]')).toBeTruthy();
  });
});

import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { AuthService } from './auth-service';
import { authInterceptor } from './auth-interceptor';
import { SessionState, WorkspaceSession } from './session-state';
import { WriteAccess } from './write-access';

const session = (roles: WorkspaceSession['roles'] = ['Viewer']): WorkspaceSession => ({
  username: 'test-user',
  roles,
  expiresAtUtc: new Date(Date.now() + 30 * 60_000).toISOString(),
});
async function microtasks() {
  for (let i = 0; i < 10; i++) await Promise.resolve();
}

@Component({ imports: [WriteAccess], template: '<button appWriteAccess>Creează incident</button>' })
class WriteControl {}

describe('Identity session and request protection', () => {
  let auth: AuthService, http: HttpTestingController;
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    auth = TestBed.inject(AuthService);
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => {
    http.verify();
    vi.useRealTimers();
  });

  it('restores the server session once and deduplicates concurrent guards', async () => {
    const a = auth.restore(),
      b = auth.restore();
    const request = http.expectOne('/api/auth/session');
    expect(request.request.withCredentials).toBe(true);
    request.flush(session());
    expect(await a).toBe(true);
    expect(await b).toBe(true);
    expect(await auth.restore()).toBe(true);
    http.expectNone('/api/auth/session');
    expect(auth.state.canWrite()).toBe(false);
  });

  it('uses an HttpOnly server session and refreshes CSRF after login without storing credentials', async () => {
    const storage = vi.spyOn(Storage.prototype, 'setItem');
    const login = auth.login('test-user', 'typed-in-form');
    http.expectOne('/api/auth/csrf').flush({ requestToken: 'anonymous-request-nonce' });
    await microtasks();
    const request = http.expectOne('/api/auth/login');
    expect(request.request.headers.get('X-CSRF-TOKEN')).toBe('anonymous-request-nonce');
    request.flush(session(['Operator']));
    await login;
    expect(auth.state.canWrite()).toBe(true);
    const token = auth.csrfToken();
    http.expectOne('/api/auth/csrf').flush({ requestToken: 'authenticated-request-nonce' });
    expect(await token).toBe('authenticated-request-nonce');
    expect(storage).not.toHaveBeenCalled();
    storage.mockRestore();
  });

  it('adds CSRF only to same-service writes and never retries a command', async () => {
    TestBed.inject(HttpClient).post('/api/scenarios/test/events', {}).subscribe();
    http.expectOne('/api/auth/csrf').flush({ requestToken: 'request-nonce' });
    await microtasks();
    const write = http.expectOne('/api/scenarios/test/events');
    expect(write.request.withCredentials).toBe(true);
    expect(write.request.headers.get('X-CSRF-TOKEN')).toBe('request-nonce');
    write.flush({});
    TestBed.inject(HttpClient).post('https://other.invalid/collect', {}).subscribe();
    const external = http.expectOne('https://other.invalid/collect');
    expect(external.request.withCredentials).toBe(false);
    expect(external.request.headers.has('X-CSRF-TOKEN')).toBe(false);
    external.flush({});
  });

  it('clears state and returns to login on a 401 from the protected API', async () => {
    auth.state.session.set(session());
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    TestBed.inject(HttpClient)
      .get('/api/scenarios')
      .subscribe({ error: () => {} });
    http.expectOne('/api/scenarios').flush({}, { status: 401, statusText: 'Unauthorized' });
    expect(auth.state.session()).toBeNull();
    expect(navigate).toHaveBeenCalledWith(['/login'], { queryParams: { expired: '1' } });
  });

  it('preserves a valid Viewer session after a forbidden write', async () => {
    auth.state.session.set(session());
    TestBed.inject(HttpClient)
      .post('/api/scenarios', {})
      .subscribe({ error: () => {} });
    http.expectOne('/api/auth/csrf').flush({ requestToken: 'request-nonce' });
    await microtasks();
    http.expectOne('/api/scenarios').flush({}, { status: 403, statusText: 'Forbidden' });
    expect(auth.state.session()?.roles).toEqual(['Viewer']);
  });

  it('expires at the server expiry and hides editing controls for Viewer and anonymous users', async () => {
    vi.useFakeTimers();
    vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const restore = auth.restore();
    http
      .expectOne('/api/auth/session')
      .flush({ ...session(['Admin']), expiresAtUtc: new Date(Date.now() + 1000).toISOString() });
    await restore;
    const fixture = TestBed.createComponent(WriteControl);
    fixture.detectChanges();
    const button = fixture.nativeElement.querySelector('button') as HTMLButtonElement;
    expect(button.hidden).toBe(false);
    auth.state.session.set(session());
    fixture.detectChanges();
    expect(button.hidden).toBe(true);
    await vi.advanceTimersByTimeAsync(1001);
    fixture.detectChanges();
    expect(auth.state.session()).toBeNull();
    expect(button.hidden).toBe(true);
  });
});

import { TestBed } from '@angular/core/testing';
import {
  ActivatedRouteSnapshot,
  provideRouter,
  Router,
  RouterStateSnapshot,
  UrlTree,
} from '@angular/router';
import { AuthService } from './auth-service';
import { workspaceGuard } from './auth-guard';
import { SessionState, WorkspaceRole } from './session-state';

describe('Workspace route access', () => {
  function setup(roles: WorkspaceRole[] | null) {
    const state = new SessionState();
    if (roles)
      state.session.set({ username: 'guard-test', roles, expiresAtUtc: '2099-01-01T00:00:00Z' });
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: AuthService, useValue: { state, restore: async () => roles !== null } },
      ],
    });
  }
  async function guard(data: Record<string, boolean> = {}) {
    return await TestBed.runInInjectionContext(() =>
      workspaceGuard(
        { data } as ActivatedRouteSnapshot,
        { url: '/operations?scenarioId=chosen' } as RouterStateSnapshot,
      ),
    );
  }
  function path(result: unknown) {
    return TestBed.inject(Router).serializeUrl(result as UrlTree);
  }
  it('redirects anonymous navigation to login preserving the local destination', async () => {
    setup(null);
    expect(path(await guard())).toContain('/login?returnUrl=');
  });
  it('allows Viewer to read, and refuses editor and admin screens', async () => {
    setup(['Viewer']);
    expect(await guard()).toBe(true);
    expect(path(await guard({ write: true }))).toBe('/access-denied');
    expect(path(await guard({ admin: true }))).toBe('/access-denied');
  });
  it('allows Operator to edit but keeps account management restricted', async () => {
    setup(['Operator']);
    expect(await guard({ write: true })).toBe(true);
    expect(path(await guard({ admin: true }))).toBe('/access-denied');
  });
  it('allows Admin and denies authenticated accounts with no workspace role', async () => {
    setup(['Admin']);
    expect(await guard({ admin: true })).toBe(true);
  });
  it('denies authenticated accounts with no workspace role', async () => {
    setup([]);
    expect(path(await guard())).toBe('/access-denied');
  });
});

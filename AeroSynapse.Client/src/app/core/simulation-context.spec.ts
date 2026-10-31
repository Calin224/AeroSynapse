import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { SessionState } from './auth/session-state';
import { AuthService } from './auth/auth-service';
import { workspaceGuard } from './auth/auth-guard';
import { SimulationContext } from './simulation-context';

@Component({ template: '' })
class TestPage {}

const scenario = '10000000-0000-0000-0000-000000000001';
const other = '10000000-0000-0000-0000-000000000002';
const run = '20000000-0000-0000-0000-000000000001';
const otherRun = '20000000-0000-0000-0000-000000000002';

describe('Shared simulation context', () => {
  let context: SimulationContext, router: Router, session: SessionState;
  beforeEach(() => {
    sessionStorage.clear();
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          ...[
            'control-tower',
            'scenario',
            'operations',
            'compare',
            'batches',
            'replay',
            'operational-report',
            'copilot',
            'scenario-studio',
          ].map((path) => ({
            path,
            component: TestPage,
            canActivate: [workspaceGuard],
          })),
        ]),
        {
          provide: AuthService,
          useFactory: () => ({ state: TestBed.inject(SessionState), restore: async () => true }),
        },
      ],
    });
    session = TestBed.inject(SessionState);
    session.session.set({
      username: 'operator.test',
      tenantId: 'demo',
      roles: ['Operator'],
      expiresAtUtc: '2099-01-01T00:00:00Z',
    });
    router = TestBed.inject(Router);
    context = TestBed.inject(SimulationContext);
  });
  afterEach(() => sessionStorage.clear());
  function restore(url: string) {
    const restored = context.restoreUrl(url, router);
    return router.parseUrl(restored ? router.serializeUrl(restored) : url);
  }
  it('carries the same scenario and exact run from simulation to analysis and back', async () => {
    const harness = await RouterTestingHarness.create();
    context.remember(scenario, run);
    for (const path of [
      'control-tower',
      'scenario',
      'replay',
      'operational-report',
      'operations',
      'copilot',
    ]) {
      await harness.navigateByUrl('/' + path);
      const params = router.parseUrl(router.url).queryParams;
      if (path === 'operational-report') expect(params['baselineRunId']).toBe(run);
      else expect(params['runId']).toBe(run);
      if (!['replay', 'operational-report'].includes(path))
        expect(params['scenarioId']).toBe(scenario);
    }
    await harness.navigateByUrl('/compare');
    expect(router.parseUrl(router.url).queryParams).toEqual({ scenarioId: scenario });
  });
  it('restores selection after the application is recreated in the same tab', () => {
    context.remember(scenario, run);
    const restored = TestBed.runInInjectionContext(() => new SimulationContext());
    expect(restored.current()).toEqual({ scenarioId: scenario, runId: run });
  });
  it('honors explicit scenario and run links without mixing scenarios', () => {
    context.remember(scenario, run);
    expect(restore(`/control-tower?scenarioId=${other}`).queryParams).toEqual({
      scenarioId: other,
    });
    expect(
      restore(`/control-tower?scenarioId=${scenario}&runId=${otherRun}`).queryParams['runId'],
    ).toBe(otherRun);
    expect(restore(`/replay?runId=${otherRun}`).queryParams).toEqual({ runId: otherRun });
    expect(restore(`/operations?runId=${otherRun}`).queryParams).toEqual({ runId: otherRun });
    expect(
      restore(`/operational-report?baselineRunId=${otherRun}&candidateRunId=${run}`).queryParams,
    ).toEqual({ baselineRunId: otherRun, candidateRunId: run });
  });
  it('preserves unrelated query filters and fragments and converges after a redirect', () => {
    context.remember(scenario, run);
    const target = restore('/control-tower?zoneId=zone#terminal');
    expect(target.queryParams).toEqual({ zoneId: 'zone', scenarioId: scenario, runId: run });
    expect(target.fragment).toBe('terminal');
    expect(context.restoreUrl(router.serializeUrl(target), router)).toBeNull();
  });
  it('clears the previous run when changing scenario and preserves it for scenario-only analysis', () => {
    context.remember(scenario, run);
    context.remember(scenario);
    expect(context.current()?.runId).toBe(run);
    context.remember(other);
    expect(context.current()).toEqual({ scenarioId: other, runId: null });
    expect(context.restoreUrl('/replay', router)).toBeNull();
    context.remember(other, otherRun);
    context.remember(other, null);
    expect(context.current()?.runId).toBeNull();
  });
  it('keeps a new scenario form and unrelated pages independent of remembered selection', async () => {
    context.remember(scenario, run);
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/scenario-studio');
    expect(router.url).toBe('/scenario-studio');
    expect(context.restoreUrl('/operation-bottleneck', router)).toBeNull();
    expect(context.restoreUrl('/login', router)).toBeNull();
  });
  it('isolates users, tenants and signed-out state', () => {
    context.remember(scenario, run);
    const original = session.session()!;
    session.session.set({ ...original, username: 'other' });
    expect(context.current()).toBeNull();
    context.remember(other, otherRun);
    session.session.set({ ...original, tenantId: 'another-tenant' });
    expect(context.current()).toBeNull();
    session.session.set(null);
    expect(context.current()).toBeNull();
    session.session.set(original);
    expect(context.current()).toEqual({ scenarioId: scenario, runId: run });
  });
  it('ignores malformed storage and retains in-memory navigation when storage is blocked', () => {
    sessionStorage.setItem('aerosynapse.simulation-context.v1:["demo","operator.test"]', '{broken');
    expect(context.current()).toBeNull();
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('disabled');
    });
    context.remember(scenario, run);
    expect(restore('/replay').queryParams['runId']).toBe(run);
  });
});

import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { SessionState, WorkspaceRole } from '../auth/session-state';
import { routes } from '../../app.routes';
import { WorkspaceNavigation } from './workspace-navigation';

@Component({ template: '' })
class TestPage {}

describe('Workspace navigation', () => {
  beforeEach(() => {
    window.__AEROSYNAPSE_CONFIG__ = { apiBaseUrl: '/api', intelligenceBaseUrl: '/intelligence' };
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'control-tower', component: TestPage },
          { path: 'control-tower/configure', component: TestPage },
          { path: 'notifications', component: TestPage },
        ]),
      ],
    });
  });
  afterEach(() => {
    delete window.__AEROSYNAPSE_CONFIG__;
  });
  function role(role: WorkspaceRole) {
    TestBed.inject(SessionState).session.set({
      username: 'test',
      roles: [role],
      expiresAtUtc: '2099-01-01T00:00:00Z',
    });
  }
  it('only exposes public destinations without a session', () => {
    expect(
      TestBed.inject(WorkspaceNavigation)
        .available()
        .map((item) => item.path),
    ).toEqual(['/login']);
  });
  for (const workspaceRole of ['Viewer', 'Operator', 'Admin'] as const) {
    it(`matches route access and feature gates for ${workspaceRole}`, () => {
      role(workspaceRole);
      const navigation = TestBed.inject(WorkspaceNavigation);
      const paths = navigation.available().map((item) => item.path);
      expect(paths).toContain('/control-tower');
      expect(paths).not.toContain('/operation-bottleneck');
      expect(paths).not.toContain('/passenger');
      expect(paths).not.toContain('/evacuation-training');
      expect(navigation.groups().some((group) => group.label === 'Explorare')).toBe(false);
      expect(routes.some((route) => route.path === 'operation-bottleneck')).toBe(false);
      expect(paths).not.toContain('/scenario-studio');
      expect(navigation.groups().some((group) => group.label === 'Simulare')).toBe(false);
      expect(navigation.groups().some((group) => group.label === 'Administrare')).toBe(false);
      for (const path of ['/external-data', '/audit', '/admin/ai-governance', '/admin/users'])
        expect(paths).not.toContain(path);
      expect(paths).not.toContain('/baggage');
      expect(paths).not.toContain('/airside');
      expect(paths).not.toContain('/energy');
      expect(paths).not.toContain('/login');
      for (const item of navigation.available()) {
        const route = routes.find((route) => '/' + route.path === item.path);
        expect(route, item.path).toBeDefined();
        if (route?.data?.['write']) expect(workspaceRole).not.toBe('Viewer');
        if (route?.data?.['admin']) expect(workspaceRole).toBe('Admin');
      }
    });
  }
  it('finds Romanian words without diacritics, regardless of order and case', () => {
    role('Operator');
    const navigation = TestBed.inject(WorkspaceNavigation);
    expect(navigation.search('  RULARI scenarii  ').map((item) => item.path)).toEqual([
      '/control-tower',
    ]);
    expect(navigation.search('alerte').map((item) => item.path)).toContain('/notifications');
    expect(navigation.search('guvernanta')).toEqual([]);
    expect(navigation.search('Zborul meu')).toEqual([]);
    expect(navigation.search('evacuare')).toEqual([]);
    expect(navigation.search('no-such-page')).toEqual([]);
  });
  it('honors enabled features and removes write destinations immediately when role changes', () => {
    window.__AEROSYNAPSE_CONFIG__!.baggageTwinEnabled = true;
    window.__AEROSYNAPSE_CONFIG__!.airsideTwinEnabled = true;
    role('Operator');
    const navigation = TestBed.inject(WorkspaceNavigation);
    expect(navigation.available().some((item) => item.path === '/airside')).toBe(true);
    role('Viewer');
    expect(navigation.available().some((item) => item.path === '/airside')).toBe(false);
    expect(navigation.available().some((item) => item.path === '/baggage')).toBe(true);
  });
  it('keeps recent destinations without query data and filters them after sign out', async () => {
    role('Operator');
    const navigation = TestBed.inject(WorkspaceNavigation),
      router = TestBed.inject(Router);
    await router.navigateByUrl('/control-tower/configure?scenarioId=private-context');
    expect(navigation.current()?.label).toBe('Control Tower');
    await router.navigateByUrl('/notifications');
    expect(navigation.recent().map((item) => item.path)).toEqual(['/control-tower']);
    expect(navigation.current()?.group).toBe('Operațiuni');
    TestBed.inject(SessionState).session.set(null);
    expect(navigation.recent()).toEqual([]);
  });
});

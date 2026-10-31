import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { SessionState } from '../auth/session-state';
import { AppHeader } from './app-header';
import { CommandPalette } from './command-palette';

@Component({ template: '<h1>Control Tower</h1>' })
class TestPage {}

describe('Workspace menu on the current route', () => {
  beforeEach(() => {
    window.__AEROSYNAPSE_CONFIG__ = {
      apiBaseUrl: '/api',
      intelligenceBaseUrl: '/intelligence',
      baggageTwinEnabled: true,
    };
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'control-tower', component: TestPage },
          { path: 'replay', component: TestPage },
          { path: 'baggage', component: TestPage },
        ]),
      ],
    });
    TestBed.inject(SessionState).session.set({
      username: 'operator.demo',
      roles: ['Operator'],
      expiresAtUtc: '2099-01-01T00:00:00Z',
    });
  });

  afterEach(() => {
    delete window.__AEROSYNAPSE_CONFIG__;
  });

  it('closes the mobile menu when selecting the current page with query parameters', async () => {
    const fixture = TestBed.createComponent(AppHeader);
    fixture.detectChanges();
    const dialogs = fixture.nativeElement.querySelectorAll(
      'dialog',
    ) as NodeListOf<HTMLDialogElement>;
    for (const dialog of dialogs) dialog.close = () => dialog.removeAttribute('open');
    const drawer = fixture.nativeElement.querySelector('.mobile-drawer') as HTMLDialogElement;
    await TestBed.inject(Router).navigateByUrl('/control-tower?scenarioId=example#terminal');
    drawer.setAttribute('open', '');

    fixture.componentInstance.closeOnCurrentPage('/control-tower');

    expect(drawer.open).toBe(false);
  });

  it('closes search when selecting the current page with query parameters', async () => {
    const fixture = TestBed.createComponent(CommandPalette);
    fixture.detectChanges();
    const dialog = fixture.nativeElement.querySelector('dialog') as HTMLDialogElement;
    dialog.close = () => dialog.removeAttribute('open');
    await TestBed.inject(Router).navigateByUrl('/control-tower?runId=example');
    dialog.setAttribute('open', '');

    fixture.componentInstance.closeOnCurrentPage('/control-tower');

    expect(dialog.open).toBe(false);
  });

  for (const [label, path] of [
    ['Analiză', '/replay'],
    ['Explorare', '/baggage'],
  ]) {
    it(`keeps ${label} expanded in both menus after leaving its pages`, async () => {
      const fixture = TestBed.createComponent(AppHeader);
      fixture.detectChanges();
      const host = fixture.nativeElement as HTMLElement;
      for (const dialog of host.querySelectorAll('dialog'))
        dialog.close = () => dialog.removeAttribute('open');
      const groups = () =>
        [...host.querySelectorAll<HTMLDetailsElement>('.nav-group')].filter(
          (group) => group.querySelector('summary')?.textContent?.trim() === label,
        );
      await TestBed.inject(Router).navigateByUrl('/control-tower');
      fixture.detectChanges();
      expect(groups().every((group) => !group.open)).toBe(true);

      groups()[0].querySelector('summary')!.click();
      fixture.detectChanges();
      expect(groups()).toHaveLength(2);
      expect(groups().every((group) => group.open)).toBe(true);

      await TestBed.inject(Router).navigateByUrl(path);
      await TestBed.inject(Router).navigateByUrl('/control-tower');
      fixture.detectChanges();
      expect(groups().every((group) => group.open)).toBe(true);

      groups()[0].querySelector('summary')!.click();
      fixture.detectChanges();
      expect(groups().every((group) => !group.open)).toBe(true);
    });

    it(`keeps ${label} expanded when navigation opened it automatically`, async () => {
      const fixture = TestBed.createComponent(AppHeader);
      fixture.detectChanges();
      const host = fixture.nativeElement as HTMLElement;
      for (const dialog of host.querySelectorAll('dialog'))
        dialog.close = () => dialog.removeAttribute('open');
      await TestBed.inject(Router).navigateByUrl(path);
      fixture.detectChanges();
      await TestBed.inject(Router).navigateByUrl('/control-tower');
      fixture.detectChanges();
      const groups = [...host.querySelectorAll<HTMLDetailsElement>('.nav-group')].filter(
        (group) => group.querySelector('summary')?.textContent?.trim() === label,
      );
      expect(groups).toHaveLength(2);
      expect(groups.every((group) => group.open)).toBe(true);
    });
  }
});

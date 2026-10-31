import { Component, ElementRef, inject, Injector, signal, viewChild } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  IsActiveMatchOptions,
  NavigationEnd,
  Router,
  RouterLink,
  RouterLinkActive,
} from '@angular/router';
import { filter } from 'rxjs';
import { SessionState } from '../auth/session-state';
import { AuthService } from '../auth/auth-service';
import { WorkspaceNavigation } from './workspace-navigation';
import { SimulationContext } from '../simulation-context';
import { CommandPalette } from './command-palette';

@Component({
  selector: 'app-header',
  imports: [NgTemplateOutlet, RouterLink, RouterLinkActive, CommandPalette],
  templateUrl: './app-header.html',
  styleUrl: './app-header.css',
})
export class AppHeader {
  readonly session = inject(SessionState);
  readonly navigation = inject(WorkspaceNavigation);
  readonly simulation = inject(SimulationContext);
  readonly exactPathMatch: IsActiveMatchOptions = {
    paths: 'exact',
    queryParams: 'ignored',
    matrixParams: 'ignored',
    fragment: 'ignored',
  };
  readonly expandedDestinations = signal(
    new Set(this.navigation.path().startsWith('/control-tower') ? ['/control-tower'] : []),
  );
  private readonly injector = inject(Injector);
  private readonly router = inject(Router);
  private readonly drawer = viewChild.required<ElementRef<HTMLDialogElement>>('drawer');
  private readonly menuButton = viewChild.required<ElementRef<HTMLButtonElement>>('menuButton');
  readonly logoutError = signal('');
  readonly loggingOut = signal(false);
  readonly expandedGroups = signal(
    new Set(
      ['Operațiuni', 'Acces rapid', this.navigation.current()?.group].filter(
        (group): group is string => !!group,
      ),
    ),
  );

  constructor() {
    this.router.events
      .pipe(
        filter((event) => event instanceof NavigationEnd),
        takeUntilDestroyed(),
      )
      .subscribe(() => {
        const group = this.navigation.current()?.group;
        if (group) this.expandedGroups.update((groups) => new Set([...groups, group]));
        const destination = this.navigation.current();
        if (destination?.children && this.navigation.path().startsWith(destination.path))
          this.expandedDestinations.update((paths) => new Set([...paths, destination.path]));
        this.drawer().nativeElement.close();
      });
  }

  toggleGroup(group: string, event: Event) {
    event.preventDefault();
    this.expandedGroups.update((groups) => {
      const next = new Set(groups);
      if (next.has(group)) next.delete(group);
      else next.add(group);
      return next;
    });
  }

  childQueryParams(path: string) {
    if (this.navigation.path() === path) return this.router.parseUrl(this.router.url).queryParams;
    if (path === '/control-tower/configure') return {};
    const selected = this.simulation.current();
    if (!selected) return {};
    return path === '/control-tower'
      ? { scenarioId: selected.scenarioId, runId: selected.runId }
      : { scenarioId: selected.scenarioId };
  }

  toggleDestination(path: string) {
    this.expandedDestinations.update((paths) => {
      const next = new Set(paths);
      if (next.has(path)) next.delete(path);
      else next.add(path);
      return next;
    });
  }

  dismissBackdrop(event: MouseEvent) {
    const dialog = event.currentTarget as HTMLDialogElement;
    if (event.target !== dialog) return;
    const bounds = dialog.getBoundingClientRect();
    if (
      event.clientX < bounds.left ||
      event.clientX > bounds.right ||
      event.clientY < bounds.top ||
      event.clientY > bounds.bottom
    )
      dialog.close();
  }

  closeOnCurrentPage(path: string) {
    if (this.navigation.path() === path) this.drawer().nativeElement.close();
  }

  restoreMenuFocus() {
    const active = document.activeElement;
    const menu = this.menuButton().nativeElement;
    if (
      (active === document.body || this.drawer().nativeElement.contains(active)) &&
      menu.checkVisibility()
    ) {
      menu.focus({ preventScroll: true });
    }
  }

  async logout() {
    if (this.loggingOut()) return;
    this.loggingOut.set(true);
    this.logoutError.set('');
    try {
      await this.injector.get(AuthService).logout();
    } catch {
      this.logoutError.set('Deconectarea nu a reușit. Încearcă din nou.');
    } finally {
      this.loggingOut.set(false);
    }
  }
}

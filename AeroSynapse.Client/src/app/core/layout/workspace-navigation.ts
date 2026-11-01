import { computed, inject, Injectable, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router } from '@angular/router';
import { filter } from 'rxjs';
import { SessionState } from '../auth/session-state';
import { runtimeConfig } from '../http/runtime-config';

export const CONTROL_TOWER_PAGES = [
  {
    id: 'create',
    label: 'Configurare scenarii',
    path: '/control-tower/configure',
    icon: 'file-edit',
  },
  { id: 'decision', label: 'Flux de decizie', path: '/control-tower/decision', icon: 'sitemap' },
  { id: 'compare', label: 'Compară personalul', path: '/control-tower/compare', icon: 'clone' },
  { id: 'repeat', label: 'Teste repetate', path: '/control-tower/batches', icon: 'chart-bar' },
] as const;

interface WorkspaceDestination {
  path: string;
  label: string;
  description: string;
  icon: string;
  group: string;
  access?: 'public' | 'write';
  feature?: 'baggageTwinEnabled' | 'airsideTwinEnabled' | 'energyAnalyticsEnabled';
  children?: typeof CONTROL_TOWER_PAGES;
}

// Shared by the sidebar and search. Route guards remain authoritative.
const destinations: WorkspaceDestination[] = [
  {
    path: '/control-tower',
    label: 'Control Tower',
    description: 'Scenarii, rulări de simulare și fluxul de decizie pentru incidente',
    icon: 'th-large',
    group: 'Operațiuni',
    children: CONTROL_TOWER_PAGES,
  },
  {
    path: '/notifications',
    label: 'Centru de alerte',
    description: 'Incidente și notificări pentru operatori',
    icon: 'bell',
    group: 'Operațiuni',
    access: 'write',
  },
  {
    path: '/replay',
    label: 'Time Travel',
    description: 'Revezi o rulare, minut cu minut',
    icon: 'history',
    group: 'Analiză',
  },
  {
    path: '/operational-report',
    label: 'Raport operațional',
    description: 'Indicatori KPI și rapoarte din rezultate validate',
    icon: 'chart-line',
    group: 'Analiză',
  },
  {
    path: '/copilot',
    label: 'AI Copilot',
    description: 'Întreabă despre o rulare și vezi sursele răspunsului',
    icon: 'sparkles',
    group: 'Analiză',
    access: 'write',
  },
  {
    path: '/baggage',
    label: 'Bagaje',
    description: 'Urmărește fluxurile de bagaje în Digital Twin',
    icon: 'briefcase',
    group: 'Explorare',
    feature: 'baggageTwinEnabled',
  },
  {
    path: '/airside',
    label: 'Airside',
    description: 'Handling, aeronave și resurse de platformă',
    icon: 'truck',
    group: 'Explorare',
    access: 'write',
    feature: 'airsideTwinEnabled',
  },
  {
    path: '/energy',
    label: 'Energie',
    description: 'Consum și eficiență în scenarii sintetice',
    icon: 'bolt',
    group: 'Explorare',
    access: 'write',
    feature: 'energyAnalyticsEnabled',
  },
  {
    path: '/login',
    label: 'Autentificare',
    description: 'Intră în spațiul de lucru al organizației',
    icon: 'sign-in',
    group: 'Acces rapid',
    access: 'public',
  },
];

export function normalizeNavigationSearch(value: string): string {
  return value
    .normalize('NFD')
    .replace(/[\u0300-\u036f]/g, '')
    .toLocaleLowerCase('ro')
    .trim();
}

@Injectable({ providedIn: 'root' })
export class WorkspaceNavigation {
  private readonly session = inject(SessionState);
  private readonly router = inject(Router);
  private readonly config = runtimeConfig();
  readonly path = signal(this.router.url.split(/[?#]/)[0]);
  private readonly visits = signal<string[]>([]);
  readonly available = computed(() =>
    destinations.filter((item) => {
      if (item.feature && this.config[item.feature] !== true) return false;
      if (item.path === '/login' && this.session.session()) return false;
      if (item.access === 'public') return true;
      if (
        !this.session
          .session()
          ?.roles.some((role) => ['Viewer', 'Operator', 'Admin'].includes(role))
      )
        return false;
      return item.access === 'write' ? this.session.canWrite() : true;
    }),
  );
  readonly groups = computed(() =>
    [...new Set(this.available().map((item) => item.group))].map((label) => ({
      label,
      items: this.available().filter((item) => item.group === label),
    })),
  );
  readonly current = computed(() =>
    this.available().find(
      (item) => item.path === this.path() || this.path().startsWith(item.path + '/'),
    ),
  );
  readonly recent = computed(() =>
    this.visits()
      .flatMap((path) => {
        const item = this.available().find((item) => item.path === path);
        return item && path !== this.current()?.path ? [item] : [];
      })
      .slice(0, 4),
  );

  constructor() {
    this.router.events
      .pipe(
        filter((event) => event instanceof NavigationEnd),
        takeUntilDestroyed(),
      )
      .subscribe((event) => {
        const path = event.urlAfterRedirects.split(/[?#]/)[0];
        this.path.set(path);
        const destination =
          this.available().find((item) => item.path === path || path.startsWith(item.path + '/'))
            ?.path ?? path;
        this.visits.update((paths) =>
          [destination, ...paths.filter((item) => item !== destination)].slice(0, 8),
        );
      });
  }

  search(query: string) {
    const words = normalizeNavigationSearch(query).split(/\s+/).filter(Boolean);
    return this.available().filter((item) => {
      const haystack = normalizeNavigationSearch(`${item.label} ${item.description} ${item.group}`);
      return words.every((word) => haystack.includes(word));
    });
  }
}

import { Routes } from '@angular/router';
import { runtimeConfig } from './core/http/runtime-config';
import { workspaceGuard } from './core/auth/auth-guard';
export const routes: Routes = [
  {
    path: 'login',
    title: 'Autentificare | AeroSynapse',
    loadComponent: () => import('./features/identity/login').then((m) => m.Login),
  },
  {
    path: 'access-denied',
    title: 'Acces restricționat | AeroSynapse',
    loadComponent: () => import('./features/identity/access-denied').then((m) => m.AccessDenied),
  },
  { path: '', pathMatch: 'full', redirectTo: 'control-tower' },
  {
    path: 'control-tower',
    title: 'Control Tower | AeroSynapse',
    loadComponent: () =>
      import('./features/control-tower/control-tower').then((m) => m.ControlTower),
  },
  {
    path: 'scenario',
    title: 'Scenario | AeroSynapse',
    loadComponent: () => import('./features/scenario/scenario').then((m) => m.Scenario),
  },
  {
    path: 'operations',
    title: 'Scenarii și rulări | AeroSynapse',
    loadComponent: () => import('./features/operations/operations').then((m) => m.Operations),
  },
  {
    path: 'airside',
    canActivate: [workspaceGuard],
    data: { write: true },
    title: 'Airside handling | AeroSynapse',
    canMatch: [() => runtimeConfig().airsideTwinEnabled === true],
    loadComponent: () => import('./features/airside/airside-twin').then((m) => m.AirsideTwin),
  },
  {
    path: 'energy',
    canActivate: [workspaceGuard],
    data: { write: true },
    title: 'Energie și eficiență | AeroSynapse',
    canMatch: [() => runtimeConfig().energyAnalyticsEnabled === true],
    loadComponent: () =>
      import('./features/energy/energy-analytics').then((m) => m.EnergyAnalytics),
  },
  {
    path: '**',
    title: 'Pagina nu există | AeroSynapse',
    loadComponent: () => import('./shared/not-found').then((m) => m.NotFound),
  },
];

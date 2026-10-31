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
    path: 'notifications',
    canActivate: [workspaceGuard],
    data: { write: true },
    title: 'Centru de alerte | AeroSynapse',
    loadComponent: () =>
      import('./features/notifications/notification-center/notification-center.component').then(
        (m) => m.NotificationCenterComponent,
      ),
  },
  {
    path: 'operator-workflow',
    canActivate: [workspaceGuard],
    data: { write: true },
    title: 'Incident → decizie | AeroSynapse',
    canDeactivate: [(component: { canLeave: () => boolean }) => component.canLeave()],
    loadComponent: () =>
      import('./features/operator-workflow/operator-workflow.component').then(
        (m) => m.OperatorWorkflowComponent,
      ),
  },
  {
    path: 'control-tower',
    canActivate: [workspaceGuard],
    title: 'Control Tower | AeroSynapse',
    loadComponent: () =>
      import('./features/control-tower/control-tower').then((m) => m.ControlTower),
  },
  {
    path: 'scenario',
    canActivate: [workspaceGuard],
    data: { write: true },
    title: 'Scenariile mele | AeroSynapse',
    loadComponent: () => import('./features/scenario/scenario').then((m) => m.Scenario),
  },
  {
    path: 'operations',
    canActivate: [workspaceGuard],
    title: 'Scenarii și rulări | AeroSynapse',
    loadComponent: () => import('./features/operations/operations').then((m) => m.Operations),
  },
  {
    path: 'scenario-studio',
    canActivate: [workspaceGuard],
    data: { write: true },
    canDeactivate: [(component: { canLeave: () => boolean }) => component.canLeave()],
    title: 'Configurare scenarii | AeroSynapse',
    loadComponent: () =>
      import('./features/scenario-studio/scenario-studio').then((m) => m.ScenarioStudio),
  },
  {
    path: 'batches',
    canActivate: [workspaceGuard],
    data: { write: true },
    title: 'Teste repetate | AeroSynapse',
    loadComponent: () => import('./features/batches/batches').then((m) => m.Batches),
  },
  {
    path: 'compare',
    canActivate: [workspaceGuard],
    data: { write: true },
    title: 'Compară personalul | AeroSynapse',
    loadComponent: () =>
      import('./features/compare/compare-workbench').then((m) => m.CompareWorkbench),
  },
  {
    path: 'replay',
    canActivate: [workspaceGuard],
    title: 'Time Travel și replay | AeroSynapse',
    loadComponent: () => import('./features/replay/replay-page').then((m) => m.ReplayPage),
  },
  {
    path: 'operational-report',
    canActivate: [workspaceGuard],
    title: 'Raport operațional KPI | AeroSynapse',
    loadComponent: () =>
      import('./features/operational-report/operational-report').then((m) => m.OperationalReport),
  },
  {
    path: 'copilot',
    canActivate: [workspaceGuard],
    data: { write: true },
    title: 'AI Copilot | AeroSynapse',
    loadComponent: () => import('./features/copilot/copilot').then((m) => m.Copilot),
  },
  {
    path: 'baggage',
    canActivate: [workspaceGuard],
    title: 'Baggage Digital Twin | AeroSynapse',
    canMatch: [() => runtimeConfig().baggageTwinEnabled === true],
    loadComponent: () => import('./features/baggage/baggage-twin').then((m) => m.BaggageTwin),
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

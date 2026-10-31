import { Routes } from '@angular/router';
export const routes: Routes = [
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
    path: '**',
    title: 'Pagina nu există | AeroSynapse',
    loadComponent: () => import('./shared/not-found').then((m) => m.NotFound),
  },
];

import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from './auth-service';
import { SimulationContext } from '../simulation-context';

export const workspaceGuard: CanActivateFn = async (route, state) => {
  const auth = inject(AuthService),
    router = inject(Router),
    simulation = inject(SimulationContext);
  if (!(await auth.restore()))
    return router.createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });
  const session = auth.state.session();
  if (!session?.roles.some((role) => ['Viewer', 'Operator', 'Admin'].includes(role)))
    return router.createUrlTree(['/access-denied']);
  if (route.data['admin'] && !auth.state.isAdmin()) return router.createUrlTree(['/access-denied']);
  if (route.data['write'] && !auth.state.canWrite())
    return router.createUrlTree(['/access-denied']);
  return simulation.restoreUrl(state.url, router) ?? true;
};

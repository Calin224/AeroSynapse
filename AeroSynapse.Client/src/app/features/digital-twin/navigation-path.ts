import { TwinNavigationRoute } from './twin-model';

/** Visual helpers never infer queue, walking or boarding time from frames or geometry. */
export function displayNavigationRoutes(routes: readonly TwinNavigationRoute[]): TwinNavigationRoute[] {
  return routes.filter(route => route.waypoints.length >= 2 && route.waypoints.every(point => [point.x, point.y, point.z].every(Number.isFinite)))
    .slice().sort((left, right) => right.passengerCount - left.passengerCount || left.routeKey.localeCompare(right.routeKey)).slice(0, 200);
}
export function routePoints2d(route: TwinNavigationRoute): string {
  return route.waypoints.map(point => `${point.x},${78 - point.z}`).join(' ');
}

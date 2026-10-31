import { TwinNavigationRoute } from './twin-model';

/** Visual helpers never infer queue, walking or boarding time from frames or geometry. */
export function displayNavigationRoutes(routes: readonly TwinNavigationRoute[]): TwinNavigationRoute[] {
  return routes.filter(route => route.waypoints.length >= 2 && route.waypoints.every(point => [point.x, point.y, point.z].every(Number.isFinite)))
    .slice().sort((left, right) => right.passengerCount - left.passengerCount || left.routeKey.localeCompare(right.routeKey)).slice(0, 200);
}
export function routePoints2d(route: TwinNavigationRoute): string {
  return route.waypoints.map(point => `${point.x},${78 - point.z}`).join(' ');
}
export function interpolateRoute(route: TwinNavigationRoute, progress: number): { x: number; y: number; z: number } | null {
  const points = route.waypoints;
  if (!points.length) return null;
  const fraction = Number.isFinite(progress) ? Math.max(0, Math.min(1, progress)) : 0;
  const lengths = points.slice(1).map((point, index) => Math.hypot(point.x - points[index].x, point.y - points[index].y, point.z - points[index].z));
  let remaining = lengths.reduce((sum, length) => sum + length, 0) * fraction;
  for (let index = 0; index < lengths.length; index++) {
    const length = lengths[index];
    if (length > 0 && remaining <= length) {
      const amount = remaining / length; const from = points[index]; const to = points[index + 1];
      return { x: from.x + (to.x - from.x) * amount, y: from.y + (to.y - from.y) * amount, z: from.z + (to.z - from.z) * amount };
    }
    remaining -= length;
  }
  const last = points[points.length - 1];
  return { x: last.x, y: last.y, z: last.z };
}

import { Line } from 'three';
import { NavigationLayer } from './navigation-layer';
import { displayNavigationRoutes, interpolateRoute, routePoints2d } from './navigation-path';
import { TwinNavigationRoute } from './twin-model';

const route: TwinNavigationRoute = { routeKey: 'nav-route', passengerCount: 20,
  waypoints: [{ zoneId: 'a', x: 0, y: 0, z: 0 }, { zoneId: 'b', x: 10, y: 0, z: 0 }, { zoneId: 'c', x: 10, y: 0, z: 10 }] };

describe('Operational routes displayed independently from simulation time', () => {
  it('reuses unchanged geometry, replaces closed-corridor paths and disposes owned geometry once', () => {
    const layer = new NavigationLayer(); layer.set([route]);
    const first = layer.group.children[0] as Line;
    const dispose = vi.spyOn(first.geometry, 'dispose');
    layer.set([structuredClone(route)]); expect(layer.group.children[0]).toBe(first);
    layer.set([{ ...route, routeKey: 'nav-alternative', waypoints: [route.waypoints[0], route.waypoints[2]] }]);
    expect(dispose).toHaveBeenCalledOnce(); expect(layer.group.children[0].name).toBe('nav-alternative');
    const lastDispose = vi.spyOn((layer.group.children[0] as Line).geometry, 'dispose');
    layer.dispose(); layer.dispose(); expect(lastDispose).toHaveBeenCalledOnce(); expect(layer.group.children).toHaveLength(0);
  });
  it('projects the same API waypoints into 2D and uses geometric interpolation only for display', () => {
    const original = structuredClone(route);
    expect(routePoints2d(route)).toBe('0,78 10,78 10,68');
    expect(interpolateRoute(route, .75)).toEqual({ x: 10, y: 0, z: 5 });
    expect(interpolateRoute(route, 0)).toEqual({ x: 0, y: 0, z: 0 });
    expect(interpolateRoute(route, 1)).toEqual({ x: 10, y: 0, z: 10 });
    expect(route).toEqual(original);
  });
  it('bounds rendering work and omits malformed visual coordinates without changing operational data', () => {
    const routes = Array.from({ length: 250 }, (_, index) => ({ ...route, routeKey: `nav-${index}`, passengerCount: index }));
    const invalid = { ...route, waypoints: [{ ...route.waypoints[0], x: NaN }, route.waypoints[1]] };
    expect(displayNavigationRoutes([...routes, invalid])).toHaveLength(200);
    expect(displayNavigationRoutes(routes)[0].passengerCount).toBe(249); expect(routes).toHaveLength(250);
  });
});

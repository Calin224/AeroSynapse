import { BufferGeometry, Group, Line, LineBasicMaterial, Vector3 } from 'three';
import { displayNavigationRoutes } from './navigation-path';
import { TwinNavigationRoute } from './twin-model';

export class NavigationLayer {
  readonly group = new Group();
  private readonly material = new LineBasicMaterial({ color: '#34d399', depthTest: false });
  private signature = '';
  private disposed = false;
  constructor() { this.group.name = 'operational-navigation-waypoints'; }
  set(routes: readonly TwinNavigationRoute[]) {
    if (this.disposed) return;
    const visible = displayNavigationRoutes(routes);
    const signature = JSON.stringify(visible.map(route => [route.routeKey, route.waypoints]));
    if (signature === this.signature) return;
    this.signature = signature; this.clear();
    for (const route of visible) {
      const geometry = new BufferGeometry().setFromPoints(route.waypoints.map(point => new Vector3(point.x, point.y + .5, point.z)));
      const line = new Line(geometry, this.material); line.name = route.routeKey; line.renderOrder = 100;
      line.raycast = () => {}; this.group.add(line);
    }
  }
  private clear() {
    for (const child of [...this.group.children]) { (child as Line).geometry.dispose(); this.group.remove(child); }
  }
  dispose() { if (this.disposed) return; this.disposed = true; this.clear(); this.group.removeFromParent(); this.material.dispose(); }
}

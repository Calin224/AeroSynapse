import {
  Box3,
  DataTexture,
  Group,
  LinearFilter,
  Mesh,
  MeshBasicMaterial,
  Object3D,
  PlaneGeometry,
  RGBAFormat,
} from 'three';
import { CONGESTION_BANDS, CongestionBand, HeatmapFrame } from './heatmap-model';
import { TERMINAL_MAP_ZONES } from './terminal-zones';

/** Colored ground surfaces; the supplied PBR materials remain shared and unchanged. */
export class HeatmapLayer {
  readonly group = new Group();
  private readonly roots = new Map<string, Object3D[]>();
  private readonly surfaces = new Map<Object3D, Mesh<PlaneGeometry, MeshBasicMaterial>>();
  private readonly geometry = new PlaneGeometry(1, 1);
  private readonly ink = this.createInk();
  private readonly materials = new Map<CongestionBand, MeshBasicMaterial>();
  private readonly reported = new Set<string>();
  constructor() {
    this.group.name = 'Congestion heatmap';
  }
  private createInk(): DataTexture {
    const size = 128,
      pixels = new Uint8Array(size * size * 4);
    for (let y = 0; y < size; y++)
      for (let x = 0; x < size; x++) {
        const qx = Math.abs(((x + 0.5) / size) * 2 - 1) - 0.82;
        const qy = Math.abs(((y + 0.5) / size) * 2 - 1) - 0.82;
        const distance =
          Math.hypot(Math.max(qx, 0), Math.max(qy, 0)) + Math.min(Math.max(qx, qy), 0) - 0.14;
        const alpha =
          distance > 0
            ? 0
            : 0.06 +
              0.24 * Math.exp((-distance * distance) / 0.055) +
              0.62 * Math.exp((-distance * distance) / 0.0009);
        const i = (y * size + x) * 4;
        pixels[i] = pixels[i + 1] = pixels[i + 2] = 255;
        pixels[i + 3] = Math.round(alpha * 255);
      }
    const texture = new DataTexture(pixels, size, size, RGBAFormat);
    texture.magFilter = texture.minFilter = LinearFilter;
    texture.needsUpdate = true;
    return texture;
  }
  register(asset: Object3D): void {
    asset.traverse((node) => {
      if (!node.userData['clickable'] || node.userData['viewMode'] === 'reference') return;
      const id = node.userData['zoneId'];
      if (typeof id !== 'string') return;
      const roots = this.roots.get(id) ?? [];
      roots.push(node);
      this.roots.set(id, roots);
    });
  }
  apply(frame: HeatmapFrame | null): void {
    this.surfaces.forEach((surface) => {
      surface.visible = false;
    });
    if (!frame) return;
    for (const zone of [
      ...frame.zones,
      ...frame.missingZones.map((missing) => ({
        ...missing,
        band: 'unavailable' as const,
        queuedPassengers: null,
      })),
    ]) {
      const roots = this.roots.get(zone.zoneId);
      if (!roots) {
        const key = `${frame.runId}:${zone.zoneId}`;
        if (!this.reported.has(key)) {
          this.reported.add(key);
          console.warn('ZoneSnapshot fără mesh', {
            zoneId: zone.zoneId,
            runId: frame.runId,
            minute: frame.minute,
          });
        }
        continue;
      }
      for (const root of roots) {
        let surface = this.surfaces.get(root);
        if (!surface) {
          const mapped = TERMINAL_MAP_ZONES.find(
            (entry) => entry.entityId === root.userData['entityId'],
          );
          const bounds = new Box3().setFromObject(root);
          if (!mapped && bounds.isEmpty()) continue;
          const rectangle = mapped
            ? [mapped.bounds[0], mapped.bounds[1] - 78, mapped.bounds[2], mapped.bounds[3]]
            : [
                bounds.min.x,
                bounds.min.z,
                Math.max(bounds.max.x - bounds.min.x, 1),
                Math.max(bounds.max.z - bounds.min.z, 1),
              ];
          surface = new Mesh(this.geometry, this.material(zone.band));
          surface.name = `Heatmap ${zone.zoneId}`;
          surface.userData = { raycastIgnore: true, zoneId: zone.zoneId };
          surface.rotation.x = -Math.PI / 2;
          surface.position.set(
            rectangle[0] + rectangle[2] / 2,
            0.515,
            rectangle[1] + rectangle[3] / 2,
          );
          surface.scale.set(rectangle[2], rectangle[3], 1);
          surface.renderOrder = 2;
          this.group.add(surface);
          this.surfaces.set(root, surface);
        }
        surface.material = this.material(zone.band);
        surface.visible = true;
        surface.userData = {
          ...surface.userData,
          runId: frame.runId,
          minute: frame.minute,
          queuedPassengers: zone.queuedPassengers,
          capacityPerMinute: zone.capacityPerMinute,
          band: zone.band,
        };
      }
    }
  }
  private material(band: CongestionBand): MeshBasicMaterial {
    let material = this.materials.get(band);
    if (!material) {
      material = new MeshBasicMaterial({
        color: CONGESTION_BANDS[band].color,
        map: this.ink,
        opacity: 0.8,
        transparent: true,
        depthWrite: false,
        toneMapped: false,
      });
      this.materials.set(band, material);
    }
    return material;
  }
  dispose(): void {
    this.group.removeFromParent();
    this.group.clear();
    this.geometry.dispose();
    this.ink.dispose();
    this.materials.forEach((material) => material.dispose());
    this.roots.clear();
    this.surfaces.clear();
    this.materials.clear();
    this.reported.clear();
  }
}

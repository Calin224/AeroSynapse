import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import {
  bagPosition,
  BaggageFrame,
  BaggageResult,
  heatColor,
  POSITIONS,
  RESOURCE_IDS,
  ResourceId,
} from './baggage.models';

/** Isolated schematic; does not load or modify the security twin's scene or assets. */
export class BaggageRenderer {
  private readonly scene = new THREE.Scene();
  private readonly camera = new THREE.PerspectiveCamera(42, 1, 0.1, 150);
  private readonly renderer = new THREE.WebGLRenderer({ antialias: true, alpha: false });
  private readonly controls: OrbitControls;
  private readonly platforms = new Map<ResourceId, THREE.Mesh>();
  private readonly bags = new THREE.InstancedMesh(
    new THREE.BoxGeometry(0.36, 0.35, 0.28),
    new THREE.MeshStandardMaterial({ color: '#e9c573', roughness: 0.55 }),
    300,
  );
  private readonly observer: ResizeObserver;
  private readonly lost = (event: Event) => {
    event.preventDefault();
    this.onError();
  };
  private disposed = false;

  constructor(
    private readonly host: HTMLElement,
    private readonly onError: () => void,
  ) {
    this.scene.background = new THREE.Color('#eef2ec');
    this.camera.position.set(5, 19, 22);
    this.renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    this.renderer.domElement.setAttribute(
      'aria-label',
      'Schemă 3D sintetică a fluxului de bagaje; valorile sunt disponibile în tabel.',
    );
    this.host.appendChild(this.renderer.domElement);
    this.controls = new OrbitControls(this.camera, this.renderer.domElement);
    this.controls.enablePan = false;
    this.controls.minDistance = 15;
    this.controls.maxDistance = 48;
    this.controls.maxPolarAngle = Math.PI / 2.25;
    this.controls.addEventListener('change', this.draw);
    this.scene.add(new THREE.HemisphereLight('#ffffff', '#8a9d88', 3));
    const sun = new THREE.DirectionalLight('#ffffff', 3);
    sun.position.set(-5, 15, 10);
    this.scene.add(sun);
    this.scene.add(new THREE.GridHelper(30, 30, '#cad4c7', '#dde4d9'));
    const linkMaterial = new THREE.LineBasicMaterial({ color: '#8eaa96' });
    for (const [from, to] of [
      ['checkIn', 'beltA'],
      ['checkIn', 'beltB'],
      ['beltA', 'sorting'],
      ['beltB', 'sorting'],
      ['sorting', 'loading'],
    ] as [ResourceId, ResourceId][]) {
      const a = POSITIONS[from],
        b = POSITIONS[to];
      this.scene.add(
        new THREE.Line(
          new THREE.BufferGeometry().setFromPoints([
            new THREE.Vector3(a[0], 0.2, a[1]),
            new THREE.Vector3(b[0], 0.2, b[1]),
          ]),
          linkMaterial,
        ),
      );
    }
    for (const key of RESOURCE_IDS) {
      const [x, z] = POSITIONS[key];
      const platform = new THREE.Mesh(
        new THREE.BoxGeometry(3.5, 0.5, 2),
        new THREE.MeshStandardMaterial({ color: '#568168' }),
      );
      platform.position.set(x, 0.45, z);
      this.platforms.set(key, platform);
      this.scene.add(platform);
    }
    this.bags.count = 0;
    this.bags.frustumCulled = false;
    this.scene.add(this.bags);
    this.renderer.domElement.addEventListener('webglcontextlost', this.lost);
    this.observer = new ResizeObserver(this.resize);
    this.observer.observe(host);
    this.resize();
  }

  set(result: BaggageResult, frame: BaggageFrame): void {
    if (this.disposed) return;
    for (const resource of frame.resources) {
      const platform = this.platforms.get(resource.resourceId)!;
      (platform.material as THREE.MeshStandardMaterial).color.set(heatColor(resource));
    }
    const dummy = new THREE.Object3D();
    let count = 0;
    for (const unit of result.units) {
      const position = bagPosition(unit, frame.minute);
      if (!position) continue;
      // Separate simultaneous bags slightly; these offsets do not imply physical positions.
      dummy.position.set(
        position[0],
        position[1] + Math.floor((count % 9) / 3) * 0.18,
        position[2] + ((count % 3) - 1) * 0.42,
      );
      dummy.updateMatrix();
      this.bags.setMatrixAt(count++, dummy.matrix);
      if (count === 300) break;
    }
    this.bags.count = count;
    this.bags.instanceMatrix.needsUpdate = true;
    this.draw();
  }

  private readonly draw = () => {
    if (!this.disposed) this.renderer.render(this.scene, this.camera);
  };
  private readonly resize = () => {
    if (this.disposed) return;
    const width = Math.max(1, this.host.clientWidth),
      height = Math.max(1, this.host.clientHeight);
    this.camera.aspect = width / height;
    this.camera.updateProjectionMatrix();
    this.renderer.setSize(width, height);
    this.draw();
  };

  dispose(): void {
    this.disposed = true;
    this.observer.disconnect();
    this.controls.dispose();
    this.renderer.domElement.removeEventListener('webglcontextlost', this.lost);
    this.scene.traverse((object) => {
      if (object instanceof THREE.Mesh || object instanceof THREE.Line) {
        object.geometry.dispose();
        for (const material of Array.isArray(object.material) ? object.material : [object.material])
          material.dispose();
      }
    });
    this.bags.dispose();
    this.renderer.dispose();
    this.renderer.domElement.remove();
  }
}

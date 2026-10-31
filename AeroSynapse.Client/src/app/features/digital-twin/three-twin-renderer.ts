import {
  AmbientLight,
  Color,
  DirectionalLight,
  GridHelper,
  Group,
  PerspectiveCamera,
  Scene,
  WebGLRenderer,
} from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { createAircraft, createAirport } from './airport-scene';
import { disposeSceneResources } from './scene-resources';
import { TwinRenderer, TwinScene } from './twin-model';

// Narrow browser ports allow lifecycle tests without creating a WebGL context.
export type GraphicsBackend = Pick<
  WebGLRenderer,
  | 'domElement'
  | 'setPixelRatio'
  | 'setSize'
  | 'render'
  | 'setAnimationLoop'
  | 'dispose'
  | 'forceContextLoss'
>;
type CameraControls = Pick<
  OrbitControls,
  | 'target'
  | 'enableDamping'
  | 'minDistance'
  | 'maxDistance'
  | 'maxPolarAngle'
  | 'update'
  | 'dispose'
>;
export interface RendererDependencies {
  graphics: () => GraphicsBackend;
  controls: (camera: PerspectiveCamera, canvas: HTMLCanvasElement) => CameraControls;
  observer: (callback: ResizeObserverCallback) => Pick<ResizeObserver, 'observe' | 'disconnect'>;
}
const browserDependencies: RendererDependencies = {
  graphics: () => new WebGLRenderer({ antialias: true, alpha: false }),
  controls: (camera, canvas) => new OrbitControls(camera, canvas),
  observer: (callback) => new ResizeObserver(callback),
};

export class ThreeTwinRenderer implements TwinRenderer {
  private readonly scene = new Scene();
  private readonly camera = new PerspectiveCamera(42, 1, 0.1, 400);
  private readonly aircraft = new Group();
  private readonly graphics: GraphicsBackend;
  private controls?: CameraControls;
  private observer?: Pick<ResizeObserver, 'observe' | 'disconnect'>;
  private disposed = false;

  constructor(
    private readonly host: HTMLElement,
    private readonly onError: () => void,
    dependencies = browserDependencies,
  ) {
    this.graphics = dependencies.graphics();
    try {
      this.graphics.domElement.setAttribute(
        'aria-label',
        'Model 3D demonstrativ: pistă, terminal, turn de control și aeronave',
      );
      this.graphics.domElement.setAttribute('role', 'img');
      this.graphics.domElement.addEventListener('webglcontextlost', this.contextLost);
      this.host.appendChild(this.graphics.domElement);
      this.graphics.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
      this.scene.background = new Color(0x0e1b27);
      this.scene.add(new AmbientLight(0xbde5ef, 2));
      const sunlight = new DirectionalLight(0xffffff, 3);
      sunlight.position.set(30, 60, -20);
      this.scene.add(sunlight, createAirport(), this.aircraft);
      const grid = new GridHelper(140, 28, 0x365565, 0x1c3444);
      grid.position.y = -0.6;
      this.scene.add(grid);
      this.controls = dependencies.controls(this.camera, this.graphics.domElement);
      this.controls.enableDamping = true;
      this.controls.minDistance = 35;
      this.controls.maxDistance = 180;
      this.controls.maxPolarAngle = Math.PI / 2.15;
      this.resetCamera();
      this.observer = dependencies.observer(this.resize);
      this.observer.observe(host);
      this.resize();
      document.addEventListener('visibilitychange', this.visibilityChanged);
      this.visibilityChanged();
    } catch (error) {
      this.dispose();
      throw error;
    }
  }

  setScene(data: TwinScene): void {
    if (this.disposed) return;
    disposeSceneResources(this.aircraft);
    for (const aircraft of data.aircraft) this.aircraft.add(createAircraft(aircraft));
    // Produce a first frame even when the browser panel is initially hidden.
    this.frame();
  }

  resetCamera(): void {
    if (this.disposed) return;
    this.camera.position.set(65, 65, 85);
    this.controls?.target.set(0, 0, 0);
    this.controls?.update();
  }

  private readonly resize = (): void => {
    if (this.disposed) return;
    const width = Math.max(this.host.clientWidth, 1);
    const height = Math.max(this.host.clientHeight, 1);
    this.camera.aspect = width / height;
    this.camera.updateProjectionMatrix();
    this.graphics.setSize(width, height);
  };

  private readonly frame = (): void => {
    if (this.disposed) return;
    try {
      this.controls?.update();
      this.graphics.render(this.scene, this.camera);
    } catch {
      this.graphics.setAnimationLoop(null);
      this.onError();
    }
  };

  private readonly visibilityChanged = (): void => {
    if (!this.disposed) this.graphics.setAnimationLoop(document.hidden ? null : this.frame);
  };

  private readonly contextLost = (event: Event): void => {
    event.preventDefault();
    this.graphics.setAnimationLoop(null);
    this.onError();
  };

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.graphics.setAnimationLoop(null);
    document.removeEventListener('visibilitychange', this.visibilityChanged);
    this.graphics.domElement.removeEventListener('webglcontextlost', this.contextLost);
    this.observer?.disconnect();
    this.controls?.dispose();
    disposeSceneResources(this.scene);
    this.graphics.dispose();
    this.graphics.forceContextLoss();
    this.graphics.domElement.remove();
  }
}

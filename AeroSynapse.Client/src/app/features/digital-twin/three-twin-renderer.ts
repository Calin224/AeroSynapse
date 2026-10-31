import {
  AmbientLight,
  Box3,
  BoxHelper,
  Color,
  DirectionalLight,
  Group,
  LOD,
  Mesh,
  Object3D,
  PerspectiveCamera,
  Raycaster,
  Scene,
  Vector2,
  Vector3,
  WebGLRenderer,
} from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';
import { MeshoptDecoder } from 'three/addons/libs/meshopt_decoder.module.js';
import { disposeSceneResources } from './scene-resources';
import {
  TwinDisplayMode,
  TwinRenderer,
  TwinResources,
  TwinScene,
  TwinSelection,
} from './twin-model';

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
  loadAsset: (url: string, signal: AbortSignal) => Promise<Group>;
}
const browserDependencies: RendererDependencies = {
  graphics: () => new WebGLRenderer({ antialias: true, alpha: false }),
  controls: (camera, canvas) => new OrbitControls(camera, canvas),
  observer: (callback) => new ResizeObserver(callback),
  loadAsset: async (path, signal) => {
    const url = new URL(path, document.baseURI);
    const response = await fetch(url, { signal });
    if (!response.ok) throw new Error(`Asset HTTP ${response.status}`);
    return (
      await new GLTFLoader()
        .setMeshoptDecoder(MeshoptDecoder)
        .parseAsync(await response.arrayBuffer(), new URL('.', url).href)
    ).scene;
  },
};

/** Display state is isolated from simulation inputs. One glTF unit is one metre. */
export class ThreeTwinRenderer implements TwinRenderer {
  readonly ready: Promise<void>;
  private readonly scene = new Scene();
  private readonly camera = new PerspectiveCamera(42, 1, 0.1, 2500);
  private readonly exterior = new LOD();
  private readonly highlight = new Group();
  private readonly request = new AbortController();
  private readonly graphics: GraphicsBackend;
  private readonly raycaster = new Raycaster();
  private readonly pointer = new Vector2();
  private controls?: CameraControls;
  private observer?: Pick<ResizeObserver, 'observe' | 'disconnect'>;
  private terminal?: Group;
  private exteriorLow?: Group;
  private exteriorDetail?: Group;
  private exteriorPending?: Promise<void>;
  private detailPending?: Promise<void>;
  private detailFailed = false;
  private disposed = false;
  private mode: TwinDisplayMode = 'interior';
  private modeVersion = 0;
  private resources: TwinResources = {
    aircraft: true,
    baggage: true,
    vehicles: true,
    runway: false,
  };
  private sceneData: TwinScene = { aircraft: [] };
  private onSelection?: (selection: TwinSelection | null) => void;
  private onAssetStatus?: (status: { loading: boolean; error?: string }) => void;
  private pointerStart?: [number, number];

  constructor(
    private readonly host: HTMLElement,
    private readonly onError: () => void,
    private readonly dependencies = browserDependencies,
  ) {
    this.graphics = dependencies.graphics();
    try {
      const canvas = this.graphics.domElement;
      canvas.setAttribute(
        'aria-label',
        'Aeroportul furnizat: 3 controale și 5 porți. Demo synthetic, fără telemetrie reală.',
      );
      canvas.setAttribute('role', 'img');
      canvas.addEventListener('webglcontextlost', this.contextLost);
      canvas.addEventListener('pointerdown', this.pointerDown);
      canvas.addEventListener('pointerup', this.pointerUp);
      this.host.appendChild(canvas);
      this.graphics.setPixelRatio(Math.min(window.devicePixelRatio || 1, 1.5));
      this.scene.background = new Color(0x0e1b27);
      this.scene.add(new AmbientLight(0xcde7ef, 2));
      const light = new DirectionalLight(0xffffff, 3);
      light.position.set(30, 80, 50);
      this.scene.add(light, this.exterior, this.highlight);
      this.exterior.visible = false;
      this.controls = dependencies.controls(this.camera, canvas);
      this.controls.enableDamping = true;
      this.controls.minDistance = 18;
      this.controls.maxDistance = 2000;
      this.controls.maxPolarAngle = Math.PI / 2.05;
      this.resetCamera();
      this.observer = dependencies.observer(this.resize);
      this.observer.observe(host);
      this.resize();
      document.addEventListener('visibilitychange', this.visibilityChanged);
      this.ready = this.loadTerminal();
      this.visibilityChanged();
    } catch (error) {
      this.dispose();
      throw error;
    }
  }

  private async loadTerminal(): Promise<void> {
    const asset = await this.dependencies.loadAsset(
      'models/aerosynapse/terminal.glb',
      this.request.signal,
    );
    if (this.disposed) {
      disposeSceneResources(asset);
      return;
    }
    this.terminal = asset;
    this.scene.add(asset);
    this.updateVisibility();
    this.resetCamera();
    this.frame();
  }
  setScene(data: TwinScene): void {
    if (this.disposed) return;
    this.sceneData = data;
    this.updateVisibility();
    this.frame();
  }
  async setDisplayMode(mode: TwinDisplayMode): Promise<void> {
    if (this.disposed) return;
    const version = ++this.modeVersion;
    this.mode = mode;
    this.detailFailed = false;
    this.clearSelection();
    this.updateVisibility();
    this.resetCamera();
    if (mode === 'exterior' && !this.exteriorLow) {
      this.onAssetStatus?.({ loading: true });
      try {
        this.exteriorPending ??= this.loadExterior();
        await this.exteriorPending;
      } catch (error) {
        if (!this.disposed && version === this.modeVersion) {
          this.mode = 'interior';
          this.updateVisibility();
          this.resetCamera();
          this.onAssetStatus?.({
            loading: false,
            error: 'Contextul exterior nu s-a încărcat. Terminalul rămâne disponibil.',
          });
          throw error;
        }
        return;
      } finally {
        this.exteriorPending = undefined;
      }
    }
    if (!this.disposed && version === this.modeVersion) {
      this.updateVisibility();
      this.resetCamera();
      this.onAssetStatus?.({ loading: false });
      this.frame();
    }
  }
  private async loadExterior(): Promise<void> {
    const asset = await this.dependencies.loadAsset(
      'models/aerosynapse/exterior-low.glb',
      this.request.signal,
    );
    if (this.disposed) {
      disposeSceneResources(asset);
      return;
    }
    this.exteriorLow = asset;
    this.exterior.addLevel(asset, 260);
  }
  private async loadDetails(): Promise<void> {
    this.onAssetStatus?.({ loading: true });
    try {
      const asset = await this.dependencies.loadAsset(
        'models/aerosynapse/exterior-detail.glb',
        this.request.signal,
      );
      if (this.disposed) {
        disposeSceneResources(asset);
        return;
      }
      this.exteriorDetail = asset;
      this.exterior.addLevel(asset, 0);
      this.updateVisibility();
      this.onAssetStatus?.({ loading: false });
    } catch {
      this.detailFailed = true;
      if (!this.disposed)
        this.onAssetStatus?.({
          loading: false,
          error: 'Detaliile exterioare nu sunt disponibile. Se păstrează modelul simplificat.',
        });
    } finally {
      this.detailPending = undefined;
    }
  }
  setResources(resources: TwinResources): void {
    const runwayChanged = this.resources.runway !== resources.runway;
    this.resources = { ...resources };
    this.clearSelection();
    this.updateVisibility();
    if (runwayChanged) this.resetCamera();
  }
  private updateVisibility(): void {
    this.terminal?.traverse((object) => {
      if (object.userData['kind'] === 'roof') object.visible = this.mode === 'exterior';
    });
    this.exterior.visible = this.mode === 'exterior';
    for (const asset of [this.exteriorLow, this.exteriorDetail])
      asset?.traverse((object) => {
        if (!object.userData['clickable']) return;
        const resource = object.userData['resource'] as keyof TwinResources;
        object.visible = resource in this.resources ? this.resources[resource] : true;
        const flight = object.userData['flightCode'];
        if (flight) {
          const data = this.sceneData.aircraft.find((aircraft) => aircraft.id === flight);
          object.visible = object.visible && !!data;
          if (data) {
            object.position.set(data.x, object.userData['baseHeight'] ?? 0, data.z);
            object.rotation.y = (data.heading * Math.PI) / 180;
          }
        }
      });
  }
  setSelectionHandler(handler: (selection: TwinSelection | null) => void): void {
    this.onSelection = handler;
  }
  setAssetStatusHandler(handler: (status: { loading: boolean; error?: string }) => void): void {
    this.onAssetStatus = handler;
  }
  private selection(object: Object3D): TwinSelection | undefined {
    for (let current: Object3D | null = object; current; current = current.parent) {
      if (current.userData['kind'] === 'roof') return undefined;
      if (current.userData['clickable'] === true) {
        const data = current.userData;
        return {
          entityId: data['entityId'],
          zoneId: data['zoneId'],
          zoneType: data['zoneType'],
          label: data['label'],
          synthetic: true,
          resource: data['resource'],
        };
      }
    }
    return undefined;
  }
  getSelectableZones(): TwinSelection[] {
    const choices = new Map<string, TwinSelection>();
    this.scene.traverseVisible((object) => {
      if (object.userData['clickable'] === true) {
        const selection = this.selection(object);
        if (selection) choices.set(selection.entityId, selection);
      }
    });
    return [...choices.values()];
  }
  selectEntity(entityId: string): void {
    let selected: Object3D | undefined;
    this.scene.traverseVisible((object) => {
      if (object.userData['entityId'] === entityId && object.userData['clickable'])
        selected ??= object;
    });
    if (!selected) return;
    disposeSceneResources(this.highlight);
    this.highlight.add(new BoxHelper(selected, 0x5ee2d3));
    this.onSelection?.(this.selection(selected) ?? null);
  }
  private clearSelection(): void {
    disposeSceneResources(this.highlight);
    this.onSelection?.(null);
  }
  private readonly pointerDown = (event: PointerEvent): void => {
    this.pointerStart = [event.clientX, event.clientY];
  };
  private readonly pointerUp = (event: PointerEvent): void => {
    const start = this.pointerStart;
    this.pointerStart = undefined;
    if (!start || Math.hypot(event.clientX - start[0], event.clientY - start[1]) > 5) return;
    const rect = this.graphics.domElement.getBoundingClientRect();
    if (!rect.width || !rect.height) return;
    this.pointer.set(
      ((event.clientX - rect.left) / rect.width) * 2 - 1,
      (-(event.clientY - rect.top) / rect.height) * 2 + 1,
    );
    this.scene.updateMatrixWorld();
    this.raycaster.setFromCamera(this.pointer, this.camera);
    const targets: Object3D[] = [];
    this.scene.traverseVisible((object) => {
      if (object instanceof Mesh && this.selection(object)) targets.push(object);
    });
    const hit = this.raycaster.intersectObjects(targets, false)[0];
    const selection = hit && this.selection(hit.object);
    if (selection) this.selectEntity(selection.entityId);
    else this.clearSelection();
  };
  resetCamera(): void {
    if (this.disposed) return;
    this.scene.updateMatrixWorld(true);
    const bounds = new Box3();
    for (const asset of [this.terminal, ...(this.mode === 'exterior' ? [this.exteriorLow] : [])]) {
      asset?.traverseVisible((object) => {
        if (!(object instanceof Mesh)) return;
        object.geometry.computeBoundingBox();
        if (object.geometry.boundingBox)
          bounds.union(object.geometry.boundingBox.clone().applyMatrix4(object.matrixWorld));
      });
    }
    const target = bounds.isEmpty() ? new Vector3() : bounds.getCenter(new Vector3());
    const direction = new Vector3(1, 1.1, 1);
    const halfVertical = (this.camera.fov * Math.PI) / 360;
    const halfAngle = Math.min(
      halfVertical,
      Math.atan(Math.tan(halfVertical) * this.camera.aspect),
    );
    const radius = Math.max(
      this.mode === 'interior' ? 140 : 170,
      bounds.isEmpty() ? 0 : bounds.getSize(new Vector3()).length() / 2,
    );
    this.camera.position.copy(
      direction
        .normalize()
        .multiplyScalar((radius / Math.sin(halfAngle)) * 1.08)
        .add(target),
    );
    this.controls?.target.copy(target);
    this.controls?.update();
  }
  private readonly resize = (): void => {
    if (this.disposed) return;
    const width = Math.max(this.host.clientWidth, 1),
      height = Math.max(this.host.clientHeight, 1);
    this.camera.aspect = width / height;
    this.camera.updateProjectionMatrix();
    this.graphics.setSize(width, height);
    this.resetCamera();
  };
  private readonly frame = (): void => {
    if (this.disposed) return;
    try {
      this.controls?.update();
      if (this.mode === 'exterior' && this.exteriorLow) {
        if (
          !this.exteriorDetail &&
          !this.detailPending &&
          !this.detailFailed &&
          this.camera.position.distanceTo(this.exterior.position) < 260
        )
          this.detailPending = this.loadDetails();
        this.exterior.update(this.camera);
      }
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
    this.request.abort();
    this.graphics.setAnimationLoop(null);
    document.removeEventListener('visibilitychange', this.visibilityChanged);
    const canvas = this.graphics.domElement;
    canvas.removeEventListener('webglcontextlost', this.contextLost);
    canvas.removeEventListener('pointerdown', this.pointerDown);
    canvas.removeEventListener('pointerup', this.pointerUp);
    this.observer?.disconnect();
    this.controls?.dispose();
    disposeSceneResources(this.scene);
    this.graphics.dispose();
    this.graphics.forceContextLoss();
    canvas.remove();
  }
}

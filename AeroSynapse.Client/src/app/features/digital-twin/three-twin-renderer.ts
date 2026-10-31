import {
  AmbientLight,
  ACESFilmicToneMapping,
  Box3,
  BoxHelper,
  CanvasTexture,
  Color,
  DataTexture,
  DirectionalLight,
  Group,
  HemisphereLight,
  EquirectangularReflectionMapping,
  LinearSRGBColorSpace,
  LinearFilter,
  RGBAFormat,
  RectAreaLight,
  Texture,
  LOD,
  Mesh,
  MeshStandardMaterial,
  PlaneGeometry,
  HalfFloatType,
  MOUSE,
  TOUCH,
  Object3D,
  PerspectiveCamera,
  PMREMGenerator,
  PCFShadowMap,
  Raycaster,
  Scene,
  Sprite,
  SpriteMaterial,
  SRGBColorSpace,
  Vector2,
  Vector3,
  WebGLRenderer,
  WebGLRenderTarget,
} from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';
import { MeshoptDecoder } from 'three/addons/libs/meshopt_decoder.module.js';
import { RoomEnvironment } from 'three/addons/environments/RoomEnvironment.js';
import { HDRLoader } from 'three/addons/loaders/HDRLoader.js';
import { RectAreaLightUniformsLib } from 'three/addons/lights/RectAreaLightUniformsLib.js';
import { EffectComposer } from 'three/addons/postprocessing/EffectComposer.js';
import { RenderPass } from 'three/addons/postprocessing/RenderPass.js';
import { GTAOPass } from 'three/addons/postprocessing/GTAOPass.js';
import { OutputPass } from 'three/addons/postprocessing/OutputPass.js';
import { UnrealBloomPass } from 'three/addons/postprocessing/UnrealBloomPass.js';
import { disposeSceneResources } from './scene-resources';
import { FrameBudget } from './frame-budget';
import { HeatmapLayer } from './heatmap-layer';
import { NavigationLayer } from './navigation-layer';
import { PassengerLayer } from './passenger-layer';
import { PassengerPacket, PassengerStats } from './passenger-model';
import { CONGESTION_BANDS, HeatmapFrame, TwinHover } from './heatmap-model';
import {
  DEFAULT_TWIN_RESOURCES,
  TwinDetailPolicy,
  TwinExteriorStatus,
  TwinDisplayMode,
  TwinCameraView,
  TwinRenderer,
  TwinResources,
  TwinScene,
  TwinSelection,
  TwinNavigationMode,
  TwinPerformance,
  TwinFailureReason,
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
> &
  Partial<Pick<OrbitControls, 'mouseButtons' | 'touches' | 'listenToKeyEvents'>>;
export interface RendererDependencies {
  animateCamera?: boolean;
  graphics: () => GraphicsBackend;
  controls: (camera: PerspectiveCamera, canvas: HTMLCanvasElement) => CameraControls;
  observer: (callback: ResizeObserverCallback) => Pick<ResizeObserver, 'observe' | 'disconnect'>;
  loadAsset: (url: string, signal: AbortSignal) => Promise<Group>;
  loadEnvironment?: (url: string, signal: AbortSignal) => Promise<Texture>;
}
const browserDependencies: RendererDependencies = {
  animateCamera: true,
  graphics: () => {
    RectAreaLightUniformsLib.init();
    const renderer = new WebGLRenderer({ antialias: true, alpha: false });
    renderer.toneMapping = ACESFilmicToneMapping;
    renderer.toneMappingExposure = 0.82;
    renderer.shadowMap.enabled = true;
    renderer.shadowMap.type = PCFShadowMap;
    return renderer;
  },
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
  loadEnvironment: async (path, signal) => {
    const response = await fetch(new URL(path, document.baseURI), { signal });
    if (!response.ok) throw new Error(`Environment HTTP ${response.status}`);
    const parsed = new HDRLoader().parse(await response.arrayBuffer());
    const texture = new DataTexture(
      parsed.data,
      parsed.width,
      parsed.height,
      RGBAFormat,
      parsed.type,
    );
    texture.colorSpace = LinearSRGBColorSpace;
    texture.mapping = EquirectangularReflectionMapping;
    texture.minFilter = parsed.minFilter ?? LinearFilter;
    texture.magFilter = parsed.magFilter ?? LinearFilter;
    texture.flipY = true;
    texture.needsUpdate = true;
    return texture;
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
  private readonly zoneLabels: Sprite[] = [];
  private readonly labelPosition = new Vector3();
  private environmentTarget?: WebGLRenderTarget;
  private daylightSky?: Texture;
  // A light studio, like the application shell around it.
  private readonly studioBackground = new Color(0xe4e9e2);
  private composer?: EffectComposer;
  private ambientOcclusion?: GTAOPass;
  private bloom?: UnrealBloomPass;
  private readonly aoExcluded: Object3D[] = [];
  private studioGround?: Mesh;
  private exteriorLow?: Group;
  private exteriorDetail?: Group;
  private exteriorPending?: Promise<void>;
  private detailPending?: Promise<void>;
  private detailFailed = false;
  private disposed = false;
  private mode: TwinDisplayMode = 'interior';
  private cameraView: TwinCameraView = 'overview';
  private modeVersion = 0;
  private resources = { ...DEFAULT_TWIN_RESOURCES };
  private detailPolicy: TwinDetailPolicy = 'auto';
  private exteriorLevel: TwinExteriorStatus['level'] = 'unloaded';
  private onExteriorStatus?: (status: TwinExteriorStatus) => void;
  private sceneData: TwinScene = { aircraft: [] };
  private onSelection?: (selection: TwinSelection | null) => void;
  private onAssetStatus?: (status: { loading: boolean; error?: string }) => void;
  private onPerformance?: (status: TwinPerformance) => void;
  private onHover?: (hover: TwinHover | null) => void;
  private heatmapLayer?: HeatmapLayer;
  private navigationLayer?: NavigationLayer;
  private heatmapData: HeatmapFrame | null = null;
  private passengerLayer?: PassengerLayer;
  private onPassengerStats?: (stats: PassengerStats) => void;
  private lastPassengerStats = '';
  private passengerBudget = 600;
  private lastHoverTime = 0;
  private readonly budget = new FrameBudget(window.devicePixelRatio || 1);
  private active = true;
  private selectedObject?: Object3D;
  private outline?: BoxHelper;
  private readonly pointers = new Set<number>();
  private pointerStart?: { id: number; x: number; y: number; dragged: boolean };
  private cameraInitialized = false;
  private cameraFlight?: {
    start: number;
    from: Vector3;
    fromTarget: Vector3;
    to: Vector3;
    target: Vector3;
  };

  constructor(
    private readonly host: HTMLElement,
    private readonly onError: (reason?: TwinFailureReason) => void,
    private readonly dependencies = browserDependencies,
  ) {
    this.graphics = dependencies.graphics();
    try {
      this.heatmapLayer = new HeatmapLayer();
      this.scene.add(this.heatmapLayer.group);
      this.navigationLayer = new NavigationLayer();
      this.scene.add(this.navigationLayer.group);
      this.passengerLayer = new PassengerLayer();
      this.scene.add(this.passengerLayer.group);
      const canvas = this.graphics.domElement;
      canvas.setAttribute(
        'aria-label',
        'Terminal demonstrativ: check-in, 3 controale și 5 porți. Configurație fictivă pentru AeroSynapse.',
      );
      canvas.setAttribute('role', 'img');
      canvas.tabIndex = 0;
      canvas.addEventListener('webglcontextlost', this.contextLost);
      canvas.addEventListener('pointerdown', this.pointerDown);
      canvas.addEventListener('pointerup', this.pointerUp);
      canvas.addEventListener('pointermove', this.pointerMove);
      canvas.addEventListener('pointercancel', this.pointerCancel);
      canvas.addEventListener('pointerleave', this.pointerLeave);
      canvas.addEventListener('wheel', this.cancelCameraFlight, { passive: true });
      canvas.addEventListener('keydown', this.cancelCameraFlight);
      this.host.appendChild(canvas);
      this.graphics.setPixelRatio(this.budget.pixelRatio);
      this.scene.background = this.studioBackground;
      this.scene.add(new AmbientLight(0xcde7ef, 0.15));
      this.scene.add(new HemisphereLight(0xdbe9f5, 0x9aa694, 0.38));
      const light = new DirectionalLight(0xffe5be, 1.7);
      light.position.set(-60, 85, 5);
      light.target.position.set(-31, 0, -38);
      light.castShadow = true;
      light.shadow.mapSize.set(2048, 2048);
      Object.assign(light.shadow.camera, {
        left: -90,
        right: 90,
        top: 90,
        bottom: -90,
        near: 1,
        far: 240,
      });
      light.shadow.bias = -0.00008;
      light.shadow.normalBias = 0.06;
      this.scene.add(light, light.target, this.exterior, this.highlight);
      this.studioGround = new Mesh(
        new PlaneGeometry(600, 600),
        new MeshStandardMaterial({ color: 0xd6ddd2, roughness: 0.96 }),
      );
      this.studioGround.rotation.x = -Math.PI / 2;
      this.studioGround.position.set(-31, -1.5, -38);
      this.studioGround.receiveShadow = true;
      this.studioGround.userData['raycastIgnore'] = true;
      this.scene.add(this.studioGround);
      for (const x of [-80, -66, -52, -38, -24]) {
        const fixture = new RectAreaLight(0xffebd2, 2.1, 10, 12);
        fixture.position.set(x, 6.8, -39);
        fixture.lookAt(x, 0.45, -39);
        this.scene.add(fixture);
      }
      const hallLight = new RectAreaLight(0xffebd2, 1.2, 25, 40);
      hallLight.position.set(3.5, 7, -38);
      hallLight.lookAt(3.5, 0.45, -38);
      this.scene.add(hallLight);
      for (const [x, z, width, depth] of [
        [9, -12.2, 9, 9],
        [3.5, -65, 34, 7],
        ...[-80, -66, -52, -38, -24].map((x) => [x, -50, 10, 4]),
      ]) {
        const featureLight = new RectAreaLight(0xffd39b, 4.2, width, depth);
        featureLight.position.set(x, 5.5, z);
        featureLight.lookAt(x, 0.45, z);
        this.scene.add(featureLight);
      }
      const vaultFill = new RectAreaLight(0xeef3ff, 1.5, 55, 18);
      vaultFill.position.set(-51, 3.3, -39);
      vaultFill.lookAt(-51, 9, -39);
      this.scene.add(vaultFill);
      if (this.graphics instanceof WebGLRenderer) {
        const environment = new RoomEnvironment();
        const generator = new PMREMGenerator(this.graphics);
        try {
          this.environmentTarget = generator.fromScene(environment, 0.04);
          this.scene.environment = this.environmentTarget.texture;
          this.scene.environmentIntensity = 0.7;
        } finally {
          generator.dispose();
          environment.dispose();
        }
        this.configurePostprocessing(this.graphics);
      }
      this.exterior.visible = false;
      this.controls = dependencies.controls(this.camera, canvas);
      this.controls.listenToKeyEvents?.(canvas);
      this.controls.enableDamping = true;
      this.controls.minDistance = 8;
      this.controls.maxDistance = 2000;
      this.controls.maxPolarAngle = Math.PI / 2.05;
      this.resetCamera();
      this.observer = dependencies.observer(this.resize);
      this.observer.observe(host);
      this.resize();
      document.addEventListener('visibilitychange', this.visibilityChanged);
      this.ready = this.loadTerminal();
      void this.loadDaylight();
      this.visibilityChanged();
    } catch (error) {
      this.dispose();
      throw error;
    }
  }
  private configurePostprocessing(renderer: WebGLRenderer): void {
    const target = new WebGLRenderTarget(1, 1, { type: HalfFloatType, samples: 4 });
    this.composer = new EffectComposer(renderer, target);
    this.composer.addPass(new RenderPass(this.scene, this.camera));
    const ao = new GTAOPass(this.scene, this.camera, 1, 1);
    ao.updateGtaoMaterial({ radius: 2.1, thickness: 0.8, samples: 8, distanceFallOff: 1 });
    ao.updatePdMaterial({ samples: 8, radius: 4 });
    ao.blendIntensity = 0.65;
    // Transparent glass and analytical overlays must not write opaque AO silhouettes.
    const render = ao.render.bind(ao);
    ao.render = (...args) => {
      const nodes = [
        ...this.aoExcluded,
        this.heatmapLayer!.group,
        this.passengerLayer!.group,
        this.highlight,
      ];
      const visibility = nodes.map((node) => node.visible);
      nodes.forEach((node) => (node.visible = false));
      try {
        render(...args);
      } finally {
        nodes.forEach((node, index) => (node.visible = visibility[index]));
      }
    };
    this.ambientOcclusion = ao;
    this.composer.addPass(ao);
    this.bloom = new UnrealBloomPass(new Vector2(1, 1), 0.16, 0.25, 2);
    this.composer.addPass(this.bloom);
    this.composer.addPass(new OutputPass());
  }
  private async loadDaylight(): Promise<void> {
    if (!this.dependencies.loadEnvironment) return;
    try {
      const texture = await this.dependencies.loadEnvironment(
        'models/aerosynapse/terminal-daylight.hdr',
        this.request.signal,
      );
      if (this.disposed || !(this.graphics instanceof WebGLRenderer)) {
        texture.dispose();
        return;
      }
      const generator = new PMREMGenerator(this.graphics);
      let target: WebGLRenderTarget;
      try {
        target = generator.fromEquirectangular(texture);
      } catch (error) {
        texture.dispose();
        throw error;
      } finally {
        generator.dispose();
      }
      this.daylightSky?.dispose();
      this.daylightSky = texture;
      this.environmentTarget?.dispose();
      this.environmentTarget = target;
      this.scene.environment = target.texture;
      this.scene.environmentIntensity = 0.35;
      this.updateVisibility();
      this.frame();
    } catch {
      // The built-in environment keeps the terminal usable if daylight is unavailable.
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
    asset.traverse((object) => {
      if (object instanceof Mesh) {
        const materials = Array.isArray(object.material) ? object.material : [object.material];
        const transparent = materials.every((material) => material.transparent);
        object.castShadow = !transparent;
        object.receiveShadow = !transparent;
        if (transparent) this.aoExcluded.push(object);
        for (const material of materials) {
          if (material instanceof MeshStandardMaterial && material.map)
            material.map.anisotropy =
              this.graphics instanceof WebGLRenderer
                ? Math.min(8, this.graphics.capabilities.getMaxAnisotropy())
                : 1;
        }
      }
    });
    this.addZoneLabels(asset);
    this.heatmapLayer?.register(asset);
    this.passengerLayer?.register(asset);
    this.heatmapLayer?.apply(this.heatmapData);
    this.refreshHeatmapLabels();
    this.updateVisibility();
    this.resetCamera();
    this.cameraInitialized = true;
    this.frame();
  }
  setScene(data: TwinScene): void {
    if (this.disposed) return;
    this.sceneData = data;
    this.navigationLayer?.set(data.navigationRoutes ?? []);
    this.updateVisibility();
    if (this.selectedObject) {
      let visible = true;
      for (let node: Object3D | null = this.selectedObject; node; node = node.parent)
        visible &&= node.visible;
      if (visible) {
        this.outline?.setFromObject(this.selectedObject);
        this.onSelection?.(this.selection(this.selectedObject) ?? null);
      } else this.clearSelection();
    }
    this.frame();
  }
  setHeatmap(frame: HeatmapFrame | null): void {
    if (this.disposed) return;
    this.heatmapData = frame;
    if (this.terminal) this.heatmapLayer?.apply(frame);
    this.refreshHeatmapLabels();
    this.updateVisibility();
    this.frame();
  }
  setHoverHandler(handler: (hover: TwinHover | null) => void): void {
    this.onHover = handler;
  }
  setPassengers(packet: PassengerPacket | null): void {
    if (this.disposed) return;
    this.passengerLayer?.set(packet);
    this.frame();
  }
  setPassengerStatsHandler(handler: (stats: PassengerStats) => void): void {
    this.onPassengerStats = handler;
  }
  private publishPassengerStats(stats: PassengerStats): void {
    const signature = JSON.stringify(stats);
    if (signature !== this.lastPassengerStats) {
      this.lastPassengerStats = signature;
      this.onPassengerStats?.(stats);
    }
  }
  private refreshHeatmapLabels(): void {
    for (const label of this.zoneLabels) {
      const material = label.material as SpriteMaterial;
      const canvas = material.map?.image as HTMLCanvasElement;
      const context = canvas?.getContext('2d');
      if (!context) continue;
      const zone = this.heatmapData?.zones.find(
        (snapshot) => snapshot.zoneId === label.userData['zoneId'],
      );
      const missing = this.heatmapData?.missingZones.some(
        (zone) => zone.zoneId === label.userData['zoneId'],
      );

      const gate = label.userData['zoneType'] === 'gate';
      const { width, height } = canvas;
      context.clearRect(0, 0, width, height);
      const color = zone
        ? CONGESTION_BANDS[zone.band].color
        : missing
          ? CONGESTION_BANDS.unavailable.color
          : label.userData['labelColor'];
      // One-line pills: dark ink on the light studio, the congestion colour on the dot only.
      context.beginPath();
      context.roundRect(3, 3, width - 6, height - 6, (height - 6) / 2);
      context.fillStyle = '#24352cf2';
      context.fill();
      context.textBaseline = 'middle';
      if (gate) {
        context.fillStyle = '#f4efe2';
        context.textAlign = 'center';
        context.font = '600 52px sans-serif';
        context.fillText(label.userData['code'], width / 2, height / 2 + 2);
        if (material.map) material.map.needsUpdate = true;
        continue;
      }
      const middle = height / 2 + 2;
      context.fillStyle = color;
      context.beginPath();
      context.arc(44, height / 2, 13, 0, Math.PI * 2);
      context.fill();
      context.textAlign = 'left';
      context.font = '600 50px sans-serif';
      // Longer codes (CHECK-IN) shrink so the value on the right keeps its room.
      const codeWidth = context.measureText(label.userData['code']).width;
      if (codeWidth > 200) context.font = `600 ${Math.floor((50 * 200) / codeWidth)}px sans-serif`;
      context.fillStyle = '#ffffff';
      context.fillText(label.userData['code'], 72, middle);
      context.textAlign = 'right';
      if (zone && this.heatmapData) {
        context.font = '600 38px sans-serif';
        context.fillStyle = color;
        // Operators on duty too, so staffing changes during a run show on the model.
        const staff = zone.staffCount == null ? '' : `${zone.staffCount} op · `;
        context.fillText(
          `${staff}${zone.queuedPassengers} în coadă`,
          width - 34,
          middle,
          width - 320,
        );
      } else {
        context.font = '500 22px sans-serif';
        context.fillStyle = '#c9d4cb';
        context.fillText(
          missing && this.heatmapData ? 'FĂRĂ DATE' : label.userData['caption'],
          width - 34,
          middle,
          width - 320,
        );
      }
      label.userData['runId'] = this.heatmapData?.runId;
      label.userData['minute'] = this.heatmapData?.minute;
      if (material.map) material.map.needsUpdate = true;
    }
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
    this.addExteriorLabels(asset);
    this.exterior.autoUpdate = false;
    this.exterior.addLevel(asset, 260);
    this.updateExteriorLOD();
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
      this.addExteriorLabels(asset);
      this.exterior.addLevel(asset, 0);
      this.updateVisibility();
      this.updateExteriorLOD();
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
    this.updateVisibility();
    if (this.selectedObject && !this.isVisible(this.selectedObject)) this.clearSelection();
    this.publishExteriorStatus();
    if (runwayChanged) this.resetCamera();
  }
  private updateVisibility(): void {
    this.graphics.domElement.setAttribute(
      'aria-label',
      this.mode === 'exterior'
        ? 'Context aeroportuar synthetic: standuri, platformă, aeronave, bagaje și vehicule demonstrative.'
        : 'Terminal demonstrativ: check-in, 3 controale și 5 porți. Configurație fictivă pentru AeroSynapse.',
    );
    if (this.studioGround) this.studioGround.visible = this.mode === 'interior';
    if (this.passengerLayer) this.passengerLayer.group.visible = this.mode === 'interior';
    if (this.heatmapLayer)
      this.heatmapLayer.group.visible = this.mode === 'interior' && this.heatmapData !== null;
    this.scene.background =
      this.cameraView === 'immersive' && this.daylightSky
        ? this.daylightSky
        : this.studioBackground;
    this.terminal?.traverse((object) => {
      const mode = object.userData['viewMode'];
      if (mode)
        object.visible =
          mode === 'both' ||
          mode === this.mode ||
          (mode === 'immersive' && (this.mode === 'exterior' || this.cameraView === 'immersive'));
      if (object.userData['kind'] === 'roof' && !mode) object.visible = this.mode === 'exterior';
      if (object instanceof Sprite) object.visible = this.mode === 'interior';
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
  setExteriorDetailPolicy(policy: TwinDetailPolicy): void {
    this.detailPolicy = policy;
    this.detailFailed = false;
    if (this.mode === 'exterior') this.updateExteriorLOD();
    this.publishExteriorStatus();
  }
  setExteriorStatusHandler(handler: (status: TwinExteriorStatus) => void): void {
    this.onExteriorStatus = handler;
    this.publishExteriorStatus();
  }
  private publishExteriorStatus(): void {
    this.onExteriorStatus?.({
      level: this.exteriorLevel,
      policy: this.detailPolicy,
      entities: this.getSelectableZones().filter((zone) => zone.exterior).length,
    });
  }
  private isVisible(object: Object3D): boolean {
    for (let current: Object3D | null = object; current; current = current.parent)
      if (!current.visible) return false;
    return true;
  }
  private isExterior(object: Object3D): boolean {
    for (let current: Object3D | null = object; current; current = current.parent)
      if (current === this.exterior) return true;
    return false;
  }
  private meshBounds(object: Object3D): Box3 {
    object.updateWorldMatrix(true, true);
    const bounds = new Box3();
    object.traverse((node) => {
      if (!(node instanceof Mesh) || node.userData['annotationOnly']) return;
      node.geometry.computeBoundingBox();
      if (node.geometry.boundingBox)
        bounds.union(node.geometry.boundingBox.clone().applyMatrix4(node.matrixWorld));
    });
    return bounds;
  }
  private exteriorLabel(object: Object3D): string {
    const data = object.userData;
    if (!this.isExterior(object)) return data['label'];
    if (data['flightCode']) return `Aeronava ${data['flightCode']} · demo`;
    if (data['code'] === 'APRON') return 'Platformă aeroportuară · demo';
    const source = (data['sourceObjects']?.[0] ?? '') as string;
    const names: Record<string, string> = {
      ControlTower: 'Turn de control',
      Hangar: 'Hangar',
      Tunnel: 'Pasarelă',
      AirplaneStairs: 'Scară mobilă',
      Firetruck: 'Autospecială',
      PassengerBus: 'Autobuz',
      BaggageTruck: 'Vehicul / remorcă bagaje',
      Container: 'Container bagaje',
    };
    const kind = Object.keys(names).find((name) => source.includes(name));
    return kind ? `${names[kind]} · ${data['code'].replace('SRC-', '')} · demo` : data['label'];
  }
  private exteriorDetails(object: Object3D): NonNullable<TwinSelection['exterior']> {
    const data = object.userData;
    const position = object.getWorldPosition(new Vector3());
    const bounds = this.meshBounds(object);
    const gateId = data['gateId'] as string | undefined;
    let gateCode: string | undefined;
    if (gateId)
      this.terminal?.traverse((node) => {
        if (node.userData['zoneId'] === gateId && node.userData['zoneType'] === 'gate')
          gateCode = node.userData['code'];
      });
    return {
      code: data['code'] ?? data['flightCode'] ?? data['entityId'],
      status: 'synthetic',
      positionMeters: position.toArray(),
      dimensionsMeters: bounds.getSize(new Vector3()).toArray(),
      headingDegrees: data['flightCode'] ? (object.rotation.y * 180) / Math.PI : undefined,
      flightCode: data['flightCode'],
      gateId,
      gateCode,
      level: this.exteriorLevel === 'detail' ? 'detail' : 'low',
      positionSource: data['flightCode'] ? 'scenario-demo' : 'authored-layout',
    };
  }
  private updateExteriorLOD(): void {
    if (!this.exteriorLow || this.mode !== 'exterior') return;
    const distance = this.camera.position.distanceTo(
      this.controls?.target ?? this.exterior.position,
    );
    const near = distance < (this.exteriorLevel === 'detail' ? 290 : 260);
    const allowDetail = this.detailPolicy === 'auto' && this.budget.pixelRatio > 0.75 && near;
    if (allowDetail && !this.exteriorDetail && !this.detailPending && !this.detailFailed)
      this.detailPending = this.loadDetails();
    const level = allowDetail && this.exteriorDetail ? 'detail' : 'low';
    this.exteriorLow.visible = level === 'low';
    if (this.exteriorDetail) this.exteriorDetail.visible = level === 'detail';
    if (this.exteriorLevel === level) return;
    this.exteriorLevel = level;
    // Rebind the outline to the same stable ID instead of keeping the now-hidden LOD object.
    const entityId = this.selectedObject?.userData['entityId'];
    if (entityId && this.isExterior(this.selectedObject!)) {
      const active = level === 'detail' ? this.exteriorDetail : this.exteriorLow;
      active?.traverseVisible((node) => {
        if (node.userData['entityId'] === entityId && node.userData['clickable']) {
          this.selectedObject = node;
          this.outline?.setFromObject(node);
          this.onSelection?.(this.selection(node) ?? null);
        }
      });
    }
    this.publishExteriorStatus();
  }
  private addExteriorLabels(asset: Group): void {
    const roots: Object3D[] = [];
    asset.traverse((node) => {
      if (node.userData['clickable'] && ['stands', 'aircraft'].includes(node.userData['resource']))
        roots.push(node);
    });
    for (const root of roots) {
      const canvas = document.createElement('canvas');
      canvas.width = 256;
      canvas.height = 80;
      const context = canvas.getContext('2d');
      if (!context) continue;
      context.fillStyle = '#12212aed';
      context.beginPath();
      context.roundRect(0, 0, 256, 80, 18);
      context.fill();
      context.strokeStyle = '#f0d191';
      context.lineWidth = 3;
      context.stroke();
      context.fillStyle = '#f7ddb0';
      context.font = '600 32px sans-serif';
      context.textAlign = 'center';
      context.textBaseline = 'middle';
      context.fillText(root.userData['flightCode'] ?? root.userData['code'], 128, 40);
      const texture = new CanvasTexture(canvas);
      texture.colorSpace = SRGBColorSpace;
      const label = new Sprite(
        new SpriteMaterial({
          map: texture,
          depthTest: false,
          depthWrite: false,
          sizeAttenuation: false,
          toneMapped: false,
        }),
      );
      const bounds = this.meshBounds(root),
        center = bounds.getCenter(new Vector3());
      center.y = bounds.max.y + 3;
      label.position.copy(root.worldToLocal(center));
      label.scale.set(0.065, 0.02, 1);
      label.name = `Exterior label ${root.userData['code']}`;
      label.userData['annotationOnly'] = true;
      label.renderOrder = 12;
      root.add(label);
      this.aoExcluded.push(label);
    }
  }
  private addZoneLabels(asset: Group): void {
    asset.updateMatrixWorld(true);
    const zones: Object3D[] = [];
    asset.traverse((object) => {
      if (Array.isArray(object.userData['labelAnchorBlender'])) zones.push(object);
    });
    for (const zone of zones) {
      const canvas = document.createElement('canvas');
      const gate = zone.userData['zoneType'] === 'gate';
      canvas.width = gate ? 176 : 560;
      canvas.height = 96;
      if (!canvas.getContext('2d')) continue;
      const texture = new CanvasTexture(canvas);
      texture.colorSpace = SRGBColorSpace;
      const sprite = new Sprite(
        new SpriteMaterial({
          map: texture,
          depthTest: false,
          depthWrite: false,
          sizeAttenuation: false,
          toneMapped: false,
        }),
      );
      const [x, y, z] = zone.userData['labelAnchorBlender'] as number[];
      sprite.position.copy(zone.worldToLocal(new Vector3(x, z, -y)));
      // Screen-sized (no attenuation); declutterLabels keeps them a fixed number of pixels tall.
      sprite.scale.set((0.04 * canvas.width) / canvas.height, 0.04, 1);
      sprite.renderOrder = 10;
      sprite.name = `Wayfinding ${zone.userData['code']}`;
      sprite.userData = {
        zoneId: zone.userData['zoneId'],
        code: zone.userData['code'],
        zoneType: zone.userData['zoneType'],
        caption: zone.userData['labelCaption'] ?? '',
        aspect: canvas.width / canvas.height,
        labelColor: zone.userData['zoneType'] === 'gate' ? '#ffc55c' : '#58e1e1',
      };
      zone.add(sprite);
      this.zoneLabels.push(sprite);
    }
  }
  setSelectionHandler(handler: (selection: TwinSelection | null) => void): void {
    this.onSelection = handler;
  }
  setAssetStatusHandler(handler: (status: { loading: boolean; error?: string }) => void): void {
    this.onAssetStatus = handler;
  }
  private selection(object: Object3D): TwinSelection | undefined {
    if (object.userData['raycastIgnore']) return undefined;
    for (let current: Object3D | null = object; current; current = current.parent) {
      if (current.userData['kind'] === 'roof') return undefined;
      if (current.userData['clickable'] === true) {
        const data = current.userData;
        return {
          entityId: data['entityId'],
          zoneId: data['zoneId'],
          zoneType: data['zoneType'],
          label: this.exteriorLabel(current),
          synthetic: true,
          resource: data['resource'],
          ...(this.isExterior(current) ? { exterior: this.exteriorDetails(current) } : {}),
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
    if (!entityId) {
      this.clearSelection();
      return;
    }
    let selected: Object3D | undefined;
    this.scene.traverseVisible((object) => {
      if (object.userData['entityId'] === entityId && object.userData['clickable'])
        selected ??= object;
    });
    if (!selected) return;
    this.cameraView = 'overview';
    this.camera.fov = 42;
    this.camera.updateProjectionMatrix();
    this.updateVisibility();
    if (this.controls) this.controls.maxPolarAngle = Math.PI / 2.05;
    this.selectedObject = selected;
    if (!this.outline) {
      this.outline = new BoxHelper(selected, 0x2f7d5b);
      this.outline.material.depthTest = false;
      this.outline.material.toneMapped = false;
      this.outline.material.transparent = true;
      this.outline.material.opacity = 0.5;
      this.outline.renderOrder = 20;
      this.highlight.add(this.outline);
    } else this.outline.setFromObject(selected);
    this.outline.visible = true;
    this.onSelection?.(this.selection(selected) ?? null);
    if (
      this.isExterior(selected) ||
      ['gate', 'checkpoint', 'checkIn'].includes(selected.userData['zoneType'])
    )
      this.frameBounds([this.meshBounds(selected)], this.isExterior(selected) ? 18 : 12);
  }
  private clearSelection(): void {
    this.selectedObject = undefined;
    if (this.outline) this.outline.visible = false;
    this.onSelection?.(null);
  }
  private readonly pointerDown = (event: PointerEvent): void => {
    this.cancelCameraFlight();
    this.onHover?.(null);
    this.pointers.add(event.pointerId);
    if (event.button !== 0 || this.pointers.size > 1) {
      this.pointerStart = undefined;
      return;
    }
    this.pointerStart = { id: event.pointerId, x: event.clientX, y: event.clientY, dragged: false };
  };
  private readonly pointerMove = (event: PointerEvent): void => {
    const start = this.pointerStart;
    if (
      start?.id === event.pointerId &&
      Math.hypot(event.clientX - start.x, event.clientY - start.y) > 5
    )
      start.dragged = true;
    if (
      this.mode === 'exterior' ||
      !this.heatmapData ||
      this.pointers.size ||
      event.pointerType === 'touch' ||
      event.timeStamp - this.lastHoverTime < 65
    )
      return;
    this.lastHoverTime = event.timeStamp;
    const selection = this.pick(event.clientX, event.clientY);
    this.onHover?.(
      selection ? { selection, clientX: event.clientX, clientY: event.clientY } : null,
    );
  };
  private readonly pointerLeave = (): void => {
    this.onHover?.(null);
  };
  private readonly pointerCancel = (event: PointerEvent): void => {
    this.pointers.delete(event.pointerId);
    this.pointerStart = undefined;
  };
  private readonly pointerUp = (event: PointerEvent): void => {
    const start = this.pointerStart;
    this.pointers.delete(event.pointerId);
    this.pointerStart = undefined;
    if (
      !start ||
      start.id !== event.pointerId ||
      start.dragged ||
      event.button !== 0 ||
      Math.hypot(event.clientX - start.x, event.clientY - start.y) > 5
    )
      return;
    const selection = this.pick(event.clientX, event.clientY);
    if (selection) this.selectEntity(selection.entityId);
    else this.clearSelection();
  };
  private pick(clientX: number, clientY: number): TwinSelection | null {
    const rect = this.graphics.domElement.getBoundingClientRect();
    if (!rect.width || !rect.height) return null;
    this.pointer.set(
      ((clientX - rect.left) / rect.width) * 2 - 1,
      (-(clientY - rect.top) / rect.height) * 2 + 1,
    );
    this.scene.updateMatrixWorld();
    this.raycaster.setFromCamera(this.pointer, this.camera);
    const targets: Object3D[] = [];
    const labels: Object3D[] = [];
    this.scene.traverseVisible((object) => {
      if (!this.selection(object)) return;
      if (object instanceof Sprite && this.isExterior(object)) labels.push(object);
      else if (object instanceof Mesh) targets.push(object);
    });
    // Screen labels render above geometry: their visible click target takes priority.
    const hit =
      this.raycaster.intersectObjects(labels, false)[0] ??
      this.raycaster.intersectObjects(targets, false)[0];
    const selection = hit && this.selection(hit.object);
    return selection ?? null;
  }
  resetCamera(): void {
    if (this.disposed) return;
    this.cameraView = 'overview';
    this.camera.fov = 42;
    this.camera.updateProjectionMatrix();
    this.updateVisibility();
    if (this.controls) this.controls.maxPolarAngle = Math.PI / 2.05;
    this.scene.updateMatrixWorld(true);
    const boxes: Box3[] = [];
    for (const asset of [this.terminal, ...(this.mode === 'exterior' ? [this.exteriorLow] : [])]) {
      asset?.traverseVisible((object) => {
        // Site context (lawn, roads, trees) surrounds the terminal; framing stays on the building.
        if (!(object instanceof Mesh) || object.userData['contextOnly']) return;
        object.geometry.computeBoundingBox();
        if (object.geometry.boundingBox)
          boxes.push(object.geometry.boundingBox.clone().applyMatrix4(object.matrixWorld));
      });
    }
    // Part boxes reach past roofs and slab edges, so the overview can sit a little closer than them.
    this.frameBounds(
      boxes,
      this.mode === 'interior' ? 40 : 170,
      this.mode === 'interior' ? 0.9 : 1.03,
    );
  }
  setCameraView(view: TwinCameraView): void {
    if (this.disposed) return;
    if (view === 'overview') {
      this.resetCamera();
      return;
    }
    this.clearSelection();
    this.cameraView = view;
    this.updateVisibility();
    if (view === 'immersive') {
      this.settleControls();
      this.camera.fov = 60;
      this.camera.updateProjectionMatrix();
      this.moveCamera(new Vector3(-52, 2.25, -25), new Vector3(-45, 2.15, -48));
      if (this.controls) this.controls.maxPolarAngle = Math.PI / 2 - 0.003;
      this.controls?.update();
    } else {
      this.camera.fov = 42;
      this.camera.updateProjectionMatrix();
      const boxes: Box3[] = [];
      this.scene.updateMatrixWorld(true);
      this.terminal?.traverseVisible((node) => {
        if (node.userData['zoneType'] === (view === 'gates' ? 'gate' : 'checkpoint'))
          boxes.push(new Box3().setFromObject(node));
      });
      if (this.controls) this.controls.maxPolarAngle = Math.PI / 2.05;
      this.frameBounds(boxes, 14);
    }
    this.frame();
  }
  /**
   * Frames the given parts as tightly as the view allows. Fitting each part's corners, not one box
   * around them all, keeps an L-shaped terminal from leaving its empty corner in the frame.
   */
  private frameBounds(boxes: Box3[], minimumRadius: number, margin = 1.03): void {
    this.settleControls();
    const corners = boxes
      .filter((box) => !box.isEmpty())
      .flatMap((box) =>
        [box.min.x, box.max.x].flatMap((x) =>
          [box.min.y, box.max.y].flatMap((y) =>
            [box.min.z, box.max.z].map((z) => new Vector3(x, y, z)),
          ),
        ),
      );
    const direction = new Vector3(0.42, 1.15, 1.25).normalize();
    const right = new Vector3().crossVectors(new Vector3(0, 1, 0), direction).normalize();
    const up = new Vector3().crossVectors(direction, right).normalize();
    // Centre the parts on screen: the midpoint of their extents across and up the view.
    const target = new Vector3();
    if (corners.length) {
      const span = (axis: Vector3) => {
        const values = corners.map((corner) => corner.dot(axis));
        return (Math.min(...values) + Math.max(...values)) / 2;
      };
      const centre = corners.reduce((sum, corner) => sum.add(corner), new Vector3());
      centre.divideScalar(corners.length);
      target
        .copy(centre)
        .addScaledVector(right, span(right) - centre.dot(right))
        .addScaledVector(up, span(up) - centre.dot(up));
    }
    const tanVertical = Math.tan((this.camera.fov * Math.PI) / 360);
    const tanHorizontal = tanVertical * this.camera.aspect;
    let distance =
      minimumRadius / Math.sin(Math.min(Math.atan(tanHorizontal), Math.atan(tanVertical)));
    for (const corner of corners) {
      const point = corner.clone().sub(target);
      distance = Math.max(
        distance,
        point.dot(direction) + Math.abs(point.dot(right)) / tanHorizontal,
        point.dot(direction) + Math.abs(point.dot(up)) / tanVertical,
      );
    }
    this.moveCamera(direction.multiplyScalar(distance * margin).add(target), target);
    this.controls?.update();
  }
  private moveCamera(position: Vector3, target: Vector3): void {
    const animate =
      this.dependencies.animateCamera &&
      this.cameraInitialized &&
      !window.matchMedia?.('(prefers-reduced-motion: reduce)').matches;
    this.cameraFlight = undefined;
    if (animate && this.controls) {
      this.cameraFlight = {
        start: performance.now(),
        from: this.camera.position.clone(),
        fromTarget: this.controls.target.clone(),
        to: position.clone(),
        target: target.clone(),
      };
    } else {
      this.camera.position.copy(position);
      this.controls?.target.copy(target);
    }
  }
  private readonly cancelCameraFlight = (): void => {
    this.cameraFlight = undefined;
  };
  private advanceCamera(): void {
    const flight = this.cameraFlight;
    if (!flight) return;
    const t = Math.min(1, Math.max(0, (performance.now() - flight.start) / 900));
    const eased = t * t * t * (t * (t * 6 - 15) + 10);
    this.camera.position.lerpVectors(flight.from, flight.to, eased);
    this.controls?.target.lerpVectors(flight.fromTarget, flight.target, eased);
    if (t === 1) this.cameraFlight = undefined;
  }
  private settleControls(): void {
    if (!this.controls) return;
    const damping = this.controls.enableDamping;
    this.controls.enableDamping = false;
    this.controls.update();
    this.controls.enableDamping = damping;
  }
  private readonly resize = (): void => {
    if (this.disposed) return;
    const width = Math.max(this.host.clientWidth, 1),
      height = Math.max(this.host.clientHeight, 1);
    this.camera.aspect = width / height;
    this.camera.updateProjectionMatrix();
    this.graphics.setSize(width, height);
    this.composer?.setSize(width, height);
    this.frame();
  };
  /**
   * Hides labels that would cover a more important one on screen: controls and check-in before
   * gates, longer queues first. Hidden labels return as soon as the camera separates them.
   */
  private declutterLabels(): void {
    const height = this.graphics.domElement.clientHeight;
    if (!height) return;
    const width = this.graphics.domElement.clientWidth;
    const pixels = height / 2 / Math.tan((this.camera.fov * Math.PI) / 360);
    // The same pill on every page, whatever the stage height: 24 px tall, 19 px on phones.
    const size = (width < 600 ? 19 : 24) / pixels;
    for (const label of this.zoneLabels)
      label.scale.set(size * (label.userData['aspect'] ?? 1), size, 1);
    const queue = (label: Sprite) =>
      this.heatmapData?.zones.find((zone) => zone.zoneId === label.userData['zoneId'])
        ?.queuedPassengers ?? 0;
    const placed: { left: number; right: number; top: number; bottom: number }[] = [];
    for (const label of this.zoneLabels
      .filter((label) => label.visible)
      .sort(
        (a, b) =>
          Number(a.userData['zoneType'] === 'gate') - Number(b.userData['zoneType'] === 'gate') ||
          queue(b) - queue(a),
      )) {
      const point = label.getWorldPosition(this.labelPosition).project(this.camera);
      const x = ((point.x + 1) / 2) * width,
        y = ((1 - point.y) / 2) * height;
      const halfWidth = (label.scale.x * pixels) / 2 + 1,
        halfHeight = (label.scale.y * pixels) / 2 + 1;
      const box = {
        left: x - halfWidth,
        right: x + halfWidth,
        top: y - halfHeight,
        bottom: y + halfHeight,
      };
      if (
        placed.some(
          (other) =>
            box.left < other.right &&
            other.left < box.right &&
            box.top < other.bottom &&
            other.top < box.bottom,
        )
      )
        label.visible = false;
      else placed.push(box);
    }
  }
  private readonly frame = (time?: number): void => {
    if (this.disposed || !this.active) return;
    try {
      this.advanceCamera();
      this.controls?.update();
      if (this.terminal && this.passengerLayer) {
        const stats = this.passengerLayer.update(
          this.camera.position,
          performance.now(),
          this.passengerBudget,
        );
        this.publishPassengerStats(this.mode === 'interior' ? stats : { ...stats, rendered: 0 });
      }
      for (const label of this.zoneLabels) {
        label.visible =
          this.mode === 'interior' &&
          this.cameraView !== 'immersive' &&
          (!!this.heatmapData?.zones.some((zone) => zone.zoneId === label.userData['zoneId']) ||
            !!this.heatmapData?.missingZones.some(
              (zone) => zone.zoneId === label.userData['zoneId'],
            ) ||
            this.camera.position.distanceTo(label.getWorldPosition(this.labelPosition)) > 80);
      }
      this.declutterLabels();
      if (this.mode === 'exterior' && this.exteriorLow) this.updateExteriorLOD();
      if (this.composer) this.composer.render();
      else this.graphics.render(this.scene, this.camera);
      if (time !== undefined && this.terminal) {
        const result = this.budget.record(time);
        if (result) {
          this.onPerformance?.(result);
          if (result.state === 'adjusting') {
            this.passengerBudget = this.budget.pixelRatio <= 0.75 ? 100 : 250;
            this.graphics.setPixelRatio(this.budget.pixelRatio);
            this.composer?.setPixelRatio(this.budget.pixelRatio);
            if (this.ambientOcclusion) this.ambientOcclusion.enabled = false;
            if (this.bloom) this.bloom.enabled = false;
            if (this.budget.pixelRatio <= 0.75 && this.graphics instanceof WebGLRenderer)
              this.graphics.shadowMap.enabled = false;
          } else if (result.state === 'fallback') {
            this.active = false;
            this.graphics.setAnimationLoop(null);
            this.onError('performance');
          }
        }
      }
    } catch {
      this.graphics.setAnimationLoop(null);
      this.onError();
    }
  };
  private readonly visibilityChanged = (): void => {
    this.budget.resetSample();
    this.pointers.clear();
    this.pointerStart = undefined;
    if (!this.disposed)
      this.graphics.setAnimationLoop(document.hidden || !this.active ? null : this.frame);
  };
  setPerformanceHandler(handler: (status: TwinPerformance) => void): void {
    this.onPerformance = handler;
  }
  setActive(active: boolean): void {
    this.cancelCameraFlight();
    this.active = active;
    if (!active && this.passengerLayer)
      this.publishPassengerStats({
        ...this.passengerLayer.update(this.camera.position),
        rendered: 0,
      });
    this.visibilityChanged();
  }
  setNavigationMode(mode: TwinNavigationMode): void {
    if (this.controls?.mouseButtons)
      this.controls.mouseButtons.LEFT = mode === 'pan' ? MOUSE.PAN : MOUSE.ROTATE;
    if (this.controls?.touches)
      this.controls.touches.ONE = mode === 'pan' ? TOUCH.PAN : TOUCH.ROTATE;
  }
  zoomCamera(direction: 'in' | 'out'): void {
    this.cancelCameraFlight();
    if (this.disposed || !this.controls) return;
    const offset = this.camera.position.clone().sub(this.controls.target);
    const distance = Math.min(
      this.controls.maxDistance,
      Math.max(this.controls.minDistance, offset.length() * (direction === 'in' ? 0.8 : 1.25)),
    );
    this.camera.position.copy(offset.setLength(distance).add(this.controls.target));
    this.controls.update();
    this.frame();
  }
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
    canvas.removeEventListener('pointermove', this.pointerMove);
    canvas.removeEventListener('pointercancel', this.pointerCancel);
    canvas.removeEventListener('pointerleave', this.pointerLeave);
    canvas.removeEventListener('wheel', this.cancelCameraFlight);
    canvas.removeEventListener('keydown', this.cancelCameraFlight);
    this.pointers.clear();
    this.observer?.disconnect();
    this.controls?.dispose();
    this.heatmapLayer?.dispose();
    this.navigationLayer?.dispose();
    this.passengerLayer?.dispose();
    this.composer?.passes.forEach((pass) => pass.dispose());
    this.composer?.dispose();
    this.aoExcluded.length = 0;
    disposeSceneResources(this.scene);
    this.scene.environment = null;
    this.environmentTarget?.dispose();
    this.scene.background = null;
    this.daylightSky?.dispose();
    this.zoneLabels.length = 0;
    this.graphics.dispose();
    this.graphics.forceContextLoss();
    canvas.remove();
  }
}

import {
  BoxGeometry,
  Group,
  Material,
  BoxHelper,
  InstancedMesh,
  MOUSE,
  TOUCH,
  Mesh,
  MeshStandardMaterial,
  Object3D,
  PerspectiveCamera,
  Scene,
  Sprite,
  SpriteMaterial,
  Texture,
  Vector3,
} from 'three';
import { disposeSceneResources } from './scene-resources';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { GraphicsBackend, RendererDependencies, ThreeTwinRenderer } from './three-twin-renderer';
import { buildHeatmapFrame } from './heatmap-model';
import { datasetFixture } from './heatmap.testing';
import { motionFixture } from './passenger.testing';
import { preparePassengerReplay } from './passenger-model';

describe('Three.js renderer lifecycle', () => {
  function asset(path: string): Group {
    const root = new Group();
    const zone = new Group();
    zone.userData = {
      clickable: true,
      synthetic: true,
      status: 'synthetic',
      entityId: 'zone-entity',
      zoneId: '60000000-0000-0000-0000-000000000101',
      zoneType: 'checkpoint',
      label: 'CP-1',
      resource: 'terminal',
    };
    if (path.includes('exterior'))
      zone.userData = {
        ...zone.userData,
        entityId: 'aircraft-entity',
        resource: 'aircraft',
        flightCode: 'AS 201',
        baseHeight: 8.7,
      };
    zone.add(new Mesh(new BoxGeometry(), new MeshStandardMaterial()));
    root.add(zone);
    return root;
  }
  function setup(
    loadAsset = vi.fn(async (path: string, _signal: AbortSignal) => asset(path)),
    loadEnvironment?: RendererDependencies['loadEnvironment'],
    actualOrbit = false,
    animateCamera = false,
  ) {
    const canvas = document.createElement('canvas');
    const host = document.createElement('div');
    Object.defineProperties(host, { clientWidth: { value: 800 }, clientHeight: { value: 400 } });
    const graphics: GraphicsBackend = {
      domElement: canvas,
      setPixelRatio: vi.fn(),
      setSize: vi.fn(),
      render: vi.fn(),
      setAnimationLoop: vi.fn(),
      dispose: vi.fn(),
      forceContextLoss: vi.fn(),
    };
    const controls = {
      target: new Vector3(),
      enableDamping: false,
      minDistance: 0,
      maxDistance: 0,
      maxPolarAngle: 0,
      mouseButtons: { LEFT: MOUSE.ROTATE, MIDDLE: MOUSE.DOLLY, RIGHT: MOUSE.PAN },
      touches: { ONE: TOUCH.ROTATE, TWO: TOUCH.DOLLY_PAN },
      listenToKeyEvents: vi.fn(),
      update: vi.fn(),
      dispose: vi.fn(),
    };
    const observer = { observe: vi.fn(), disconnect: vi.fn() };
    let orbit!: OrbitControls;
    let resized!: ResizeObserverCallback;
    const dependencies: RendererDependencies = {
      animateCamera,
      graphics: () => graphics,
      controls: (camera, element) =>
        actualOrbit ? (orbit = new OrbitControls(camera, element)) : controls,
      observer: (callback) => {
        resized = callback;
        return observer;
      },
      loadAsset,
      loadEnvironment,
    };
    const onError = vi.fn();
    const renderer = new ThreeTwinRenderer(host, onError, dependencies);
    return {
      host,
      canvas,
      graphics,
      controls,
      observer,
      renderer,
      onError,
      loadAsset,
      resize: () => resized([], {} as ResizeObserver),
      orbit: () => orbit,
      view: () => vi.mocked(graphics.render).mock.calls.at(-1)! as [Scene, PerspectiveCamera],
      loop: () =>
        vi.mocked(graphics.setAnimationLoop).mock.calls.find(([c]) => typeof c === 'function')![0]!,
    };
  }
  it('eases camera views, keeps snapshot changes independent and yields immediately to user input', async () => {
    let now = 0;
    const clock = vi.spyOn(performance, 'now').mockImplementation(() => now);
    const { renderer, graphics, controls, canvas, view, loop } = setup(
      undefined,
      undefined,
      false,
      true,
    );
    await renderer.ready;
    const [, camera] = view();
    const frame = loop();
    const start = camera.position.clone();
    renderer.selectEntity('zone-entity');
    expect(camera.position).toEqual(start);
    now = 450;
    frame(450, {} as XRFrame);
    const middle = camera.position.clone();
    expect(middle.distanceTo(controls.target)).toBeLessThan(start.distanceTo(controls.target));
    renderer.setHeatmap(buildHeatmapFrame(datasetFixture(), 'baseline', 2));
    expect(camera.position).toEqual(middle);
    now = 900;
    frame(900, {} as XRFrame);
    expect(camera.position.distanceTo(controls.target)).toBeLessThan(
      middle.distanceTo(controls.target),
    );
    renderer.resetCamera();
    now = 1100;
    frame(1100, {} as XRFrame);
    canvas.dispatchEvent(new WheelEvent('wheel'));
    const interrupted = camera.position.clone();
    now = 2100;
    frame(2100, {} as XRFrame);
    expect(camera.position).toEqual(interrupted);
    renderer.dispose();
    clock.mockRestore();
  });
  it('sizes the camera and releases loop, controls, observer, GPU and canvas exactly once', async () => {
    const { host, graphics, controls, observer, renderer } = setup();
    await renderer.ready;
    expect(graphics.setSize).toHaveBeenCalledWith(800, 400);
    renderer.setScene({ aircraft: [{ id: 'test', x: 0, z: 0, heading: 0 }] });
    const frame = vi
      .mocked(graphics.setAnimationLoop)
      .mock.calls.find((call) => typeof call[0] === 'function')?.[0];
    expect(frame).toBeTypeOf('function');
    frame?.(0, {} as XRFrame);
    const [scene, camera] = vi.mocked(graphics.render).mock.calls[0] as [Scene, PerspectiveCamera];
    expect(camera.aspect).toBe(2);
    const disposeGeometry = vi.fn();
    const disposeMaterial = vi.fn();
    scene.traverse((object: Object3D) => {
      if (object instanceof Mesh) {
        vi.spyOn(object.geometry, 'dispose').mockImplementation(disposeGeometry);
        for (const material of Array.isArray(object.material) ? object.material : [object.material])
          vi.spyOn(material as Material, 'dispose').mockImplementation(disposeMaterial);
      }
    });
    renderer.dispose();
    renderer.dispose();
    expect(graphics.setAnimationLoop).toHaveBeenLastCalledWith(null);
    expect(controls.dispose).toHaveBeenCalledOnce();
    expect(observer.disconnect).toHaveBeenCalledOnce();
    expect(graphics.dispose).toHaveBeenCalledOnce();
    expect(graphics.forceContextLoss).toHaveBeenCalledOnce();
    expect(disposeGeometry).toHaveBeenCalled();
    expect(disposeMaterial).toHaveBeenCalled();
    expect(scene.children).toHaveLength(0);
    expect(host.children).toHaveLength(0);
    const callCount = vi.mocked(graphics.setAnimationLoop).mock.calls.length;
    document.dispatchEvent(new Event('visibilitychange'));
    expect(vi.mocked(graphics.setAnimationLoop).mock.calls.length).toBe(callCount);
  });
  it('reports a lost context and removes its listener on destruction', () => {
    const { canvas, renderer, onError } = setup();
    canvas.dispatchEvent(new Event('webglcontextlost', { cancelable: true }));
    expect(onError).toHaveBeenCalledOnce();
    renderer.dispose();
    canvas.dispatchEvent(new Event('webglcontextlost'));
    expect(onError).toHaveBeenCalledOnce();
  });
  it('updates heatmap surfaces without moving the camera and raycasts through the overlay to the checkpoint', async () => {
    const { renderer, graphics, canvas, loadAsset, view } = setup();
    await renderer.ready;
    const [scene, camera] = view();
    camera.position.set(0, 5, 10);
    camera.lookAt(0, 0, 0);
    camera.updateMatrixWorld(true);
    const position = camera.position.clone(),
      targetFrame = buildHeatmapFrame(datasetFixture(), 'baseline', 2);
    renderer.setHeatmap(targetFrame);
    const surface = scene.getObjectByName('Congestion heatmap')!.children[0];
    expect(surface.userData).toMatchObject({ minute: 2, runId: 'base-run', band: 'critical' });
    renderer.setHeatmap(buildHeatmapFrame(datasetFixture(), 'optimized', 2));
    expect(surface.userData).toMatchObject({ minute: 2, runId: 'optimized-run', band: 'low' });
    const replay = preparePassengerReplay(motionFixture(1200));
    renderer.setPassengers({
      replay,
      timeMinutes: 1.75,
      snapshotMinute: 1,
      playing: false,
      speed: 1,
      budget: 600,
      enabled: true,
      endMinute: 5,
    });
    const crowd = scene
      .getObjectByName('Simulator passenger playback')!
      .children.find((mesh) => mesh.visible) as InstancedMesh;
    expect(crowd.count).toBe(600);
    expect(camera.position).toEqual(position);
    expect(loadAsset).toHaveBeenCalledOnce();
    vi.spyOn(canvas, 'getBoundingClientRect').mockReturnValue(new DOMRect(0, 0, 800, 400));
    const hover = vi.fn(),
      select = vi.fn();
    renderer.setHoverHandler(hover);
    renderer.setSelectionHandler(select);
    for (const type of ['pointermove', 'pointerdown', 'pointerup']) {
      const event = new MouseEvent(type, { clientX: 400, clientY: 200 });
      Object.defineProperties(event, {
        pointerId: { value: 1 },
        pointerType: { value: 'mouse' },
        timeStamp: { value: 1000 },
      });
      canvas.dispatchEvent(event);
    }
    expect(hover).toHaveBeenCalledWith(
      expect.objectContaining({ selection: expect.objectContaining({ entityId: 'zone-entity' }) }),
    );
    expect(select).toHaveBeenLastCalledWith(expect.objectContaining({ entityId: 'zone-entity' }));
    renderer.setPassengers(null);
    expect(crowd.count).toBe(0);
    renderer.dispose();
  });
  it('labels every zone the asset anchors, check-in included, with the caption the asset carries', async () => {
    const drawn = new Map<HTMLCanvasElement, string[]>();
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockImplementation(function (
      this: HTMLCanvasElement,
    ) {
      const texts = drawn.get(this) ?? [];
      drawn.set(this, texts);
      return new Proxy(
        { fillText: (text: string) => texts.push(text), measureText: () => ({ width: 100 }) },
        { get: (target, key) => (target as Record<string | symbol, unknown>)[key] ?? (() => {}) },
      ) as unknown as RenderingContext;
    });
    const zone = (code: string, zoneType: string, labelCaption: string, y: number) => {
      const node = new Group();
      node.userData = {
        clickable: true,
        synthetic: true,
        entityId: `${code}-entity`,
        zoneId: `${code}-zone`,
        zoneType,
        code,
        label: code,
        resource: 'terminal',
        labelAnchorBlender: [0, y, 6.7],
        labelCaption,
      };
      node.add(new Mesh(new BoxGeometry(), new MeshStandardMaterial()));
      return node;
    };
    const loader = vi.fn(async (path: string, _signal: AbortSignal) => {
      const root = asset(path);
      if (!path.includes('exterior'))
        root.add(
          zone('CHECK-IN', 'terminal', 'GHIȘEE CHECK-IN', 68),
          zone('CP-1', 'checkpoint', 'CONTROL SECURITATE', 27),
        );
      return root;
    });
    const { renderer, graphics } = setup(loader);
    await renderer.ready;
    const scene = vi.mocked(graphics.render).mock.calls.at(-1)![0] as Scene;
    const text = (code: string) => {
      const sprite = scene.getObjectByName(`Wayfinding ${code}`) as Sprite;
      return drawn.get((sprite.material as SpriteMaterial).map!.image as HTMLCanvasElement);
    };
    expect(text('CHECK-IN')).toEqual(['CHECK-IN', 'GHIȘEE CHECK-IN']);
    expect(text('CP-1')).toEqual(['CP-1', 'CONTROL SECURITATE']);
    // The fixture zone without an anchor stays unlabelled.
    expect(scene.getObjectByName('Wayfinding undefined')).toBeUndefined();
    renderer.dispose();
    vi.restoreAllMocks();
  });
  it('releases partial initialization if controls fail', () => {
    const canvas = document.createElement('canvas');
    const graphics: GraphicsBackend = {
      domElement: canvas,
      setPixelRatio: vi.fn(),
      setSize: vi.fn(),
      render: vi.fn(),
      setAnimationLoop: vi.fn(),
      dispose: vi.fn(),
      forceContextLoss: vi.fn(),
    };
    expect(
      () =>
        new ThreeTwinRenderer(document.createElement('div'), vi.fn(), {
          graphics: () => graphics,
          controls: () => {
            throw Error('unavailable');
          },
          observer: vi.fn(),
          loadAsset: vi.fn(),
        }),
    ).toThrow('unavailable');
    expect(graphics.dispose).toHaveBeenCalledOnce();
    expect(graphics.forceContextLoss).toHaveBeenCalledOnce();
    expect(canvas.parentNode).toBeNull();
  });
  it('disposes shared geometry, multi-material textures only once', () => {
    const scene = new Scene();
    const geometry = new BoxGeometry();
    const texture = new Texture();
    const material = new MeshStandardMaterial({ map: texture });
    const spies = [
      vi.spyOn(geometry, 'dispose'),
      vi.spyOn(texture, 'dispose'),
      vi.spyOn(material, 'dispose'),
    ];
    scene.add(new Mesh(geometry, [material, material]), new Mesh(geometry, material));
    disposeSceneResources(scene);
    for (const spy of spies) expect(spy).toHaveBeenCalledOnce();
  });
  it('loads exterior only on request, caches it, and keeps the simulation snapshot unchanged', async () => {
    const { renderer, loadAsset, graphics, view } = setup();
    await renderer.ready;
    const data = { aircraft: [Object.freeze({ id: 'AS 201', x: -28, z: -65, heading: 0 })] };
    renderer.setScene(data);
    expect(loadAsset).toHaveBeenCalledTimes(1);
    await renderer.setDisplayMode('exterior');
    const [scene] = view();
    let aircraft: Object3D | undefined;
    scene.traverse((object) => {
      if (object.userData['flightCode'] === 'AS 201') aircraft = object;
    });
    expect(aircraft?.position.toArray()).toEqual([-28, 8.7, -65]);
    expect(loadAsset.mock.calls.map(([path]) => path)).toEqual([
      'models/aerosynapse/terminal.glb',
      'models/aerosynapse/exterior-low.glb',
    ]);
    await renderer.setDisplayMode('interior');
    await renderer.setDisplayMode('exterior');
    expect(loadAsset).toHaveBeenCalledTimes(2);
    expect(data.aircraft[0]).toEqual({ id: 'AS 201', x: -28, z: -65, heading: 0 });
    renderer.dispose();
  });
  it('emits the database zone ID and filters optional aircraft without touching terminal zones', async () => {
    const { renderer } = setup();
    await renderer.ready;
    const select = vi.fn();
    renderer.setSelectionHandler(select);
    renderer.selectEntity('zone-entity');
    expect(select).toHaveBeenLastCalledWith(
      expect.objectContaining({
        zoneId: '60000000-0000-0000-0000-000000000101',
        synthetic: true,
      }),
    );
    renderer.setScene({ aircraft: [{ id: 'AS 201', x: -28, z: -65, heading: 0 }] });
    await renderer.setDisplayMode('exterior');
    expect(renderer.getSelectableZones().map((zone) => zone.entityId)).toContain('aircraft-entity');
    renderer.setResources({
      aircraft: false,
      baggage: true,
      vehicles: true,
      runway: false,
      stands: true,
      apron: true,
    });
    expect(renderer.getSelectableZones().map((zone) => zone.entityId)).toEqual(['zone-entity']);
    renderer.dispose();
  });
  it('keeps the terminal usable after exterior load failure and supports retry', async () => {
    const loader = vi.fn(async (path: string, _signal: AbortSignal) => asset(path));
    const { renderer, onError } = setup(loader);
    await renderer.ready;
    loader.mockRejectedValueOnce(new Error('offline'));
    await expect(renderer.setDisplayMode('exterior')).rejects.toThrow('offline');
    expect(renderer.getSelectableZones()).toHaveLength(1);
    expect(onError).not.toHaveBeenCalled();
    await renderer.setDisplayMode('exterior');
    expect(loader).toHaveBeenCalledTimes(3);
    renderer.dispose();
  });
  it.each(['mouse', 'touch'])(
    'allows a %s tap to pick a control through architectural glass',
    async (pointerType) => {
      const loader = vi.fn(async (path: string, _signal: AbortSignal) => {
        const group = asset(path);
        const glass = new Mesh(
          new BoxGeometry(20, 0.05, 20),
          new MeshStandardMaterial({ transparent: true, opacity: 0.26 }),
        );
        glass.position.y = 4;
        glass.userData = { raycastIgnore: true };
        group.children[0].add(glass);
        return group;
      });
      const { renderer, graphics, canvas, view } = setup(loader);
      await renderer.ready;
      const [, camera] = view();
      camera.lookAt(0, 0, 0);
      camera.updateMatrixWorld(true);
      const projected = new Vector3().project(camera);
      vi.spyOn(canvas, 'getBoundingClientRect').mockReturnValue({
        left: 0,
        top: 0,
        width: 800,
        height: 400,
        right: 800,
        bottom: 400,
        x: 0,
        y: 0,
        toJSON: () => ({}),
      });
      const selected = vi.fn();
      renderer.setSelectionHandler(selected);
      const pointer = { clientX: (projected.x + 1) * 400, clientY: (1 - projected.y) * 200 };
      for (const type of ['pointerdown', 'pointerup']) {
        const event = new MouseEvent(type, pointer);
        Object.defineProperties(event, {
          pointerId: { value: 1 },
          pointerType: { value: pointerType },
        });
        canvas.dispatchEvent(event);
      }
      expect(selected).toHaveBeenLastCalledWith(
        expect.objectContaining({ entityId: 'zone-entity' }),
      );
      renderer.dispose();
    },
  );
  function exteriorFixture(path: string): Group {
    if (!path.includes('exterior')) return asset(path);
    const group = new Group();
    for (const [id, resource, code] of [
      ['stand-id', 'stands', 'STAND-1'],
      ['apron-id', 'apron', 'APRON'],
      ['vehicle-id', 'vehicles', 'BUS-1'],
    ]) {
      const root = new Group();
      root.name = `${path}/${id}`;
      root.userData = {
        clickable: true,
        entityId: id,
        zoneId: 'scene-id',
        zoneType: 'scene',
        label: code,
        code,
        resource,
        synthetic: true,
        status: 'synthetic',
        ...(resource === 'stands' ? { gateId: 'gate-id' } : {}),
      };
      root.position.set(1000, 0, 1200);
      root.add(new Mesh(new BoxGeometry(32, 1, 50), new MeshStandardMaterial()));
      group.add(root);
    }
    return group;
  }
  it('pins low detail in economic mode, lazily loads near the camera target and rebinds a stable selection between LODs', async () => {
    const loader = vi.fn(async (path: string, _signal: AbortSignal) => exteriorFixture(path));
    const { renderer, graphics, controls, view, loop } = setup(loader);
    const selected = vi.fn(),
      status = vi.fn();
    renderer.setSelectionHandler(selected);
    renderer.setExteriorStatusHandler(status);
    await renderer.ready;
    renderer.setExteriorDetailPolicy('low');
    await renderer.setDisplayMode('exterior');
    renderer.selectEntity('stand-id');
    expect(selected.mock.calls.at(-1)![0]).toMatchObject({
      entityId: 'stand-id',
      zoneId: 'scene-id',
      synthetic: true,
      exterior: {
        status: 'synthetic',
        code: 'STAND-1',
        gateId: 'gate-id',
        level: 'low',
        positionMeters: [1000, 0, 1200],
        dimensionsMeters: [32, 1, 50],
      },
    });
    const [scene, camera] = view();
    const frame = loop();
    camera.position.copy(controls.target).add(new Vector3(0, 100, 0));
    frame(0, {} as XRFrame);
    expect(loader).toHaveBeenCalledTimes(2);
    renderer.setExteriorDetailPolicy('auto');
    await vi.waitFor(() => expect(selected.mock.calls.at(-1)![0]?.exterior?.level).toBe('detail'));
    expect(loader).toHaveBeenCalledTimes(3);
    expect(status).toHaveBeenLastCalledWith({ level: 'detail', policy: 'auto', entities: 3 });
    expect(camera.position.x).toBe(1000); // Far from the origin; near the investigated resource.
    const before = camera.position.clone();
    renderer.setExteriorDetailPolicy('low');
    expect(selected.mock.calls.at(-1)![0]).toMatchObject({
      entityId: 'stand-id',
      exterior: { level: 'low' },
    });
    expect(camera.position).toEqual(before);
    expect(scene.getObjectsByProperty('type', 'BoxHelper')).toHaveLength(1);
    renderer.setExteriorDetailPolicy('auto');
    expect(loader).toHaveBeenCalledTimes(3);
    renderer.dispose();
  });
  it('picks the visible exterior label before aircraft geometry behind that label', async () => {
    const loader = vi.fn(async (path: string, _signal: AbortSignal) => exteriorFixture(path));
    const { renderer, graphics, canvas, view } = setup(loader);
    await renderer.ready;
    renderer.setExteriorDetailPolicy('low');
    await renderer.setDisplayMode('exterior');
    const [scene, camera] = view();
    let stand!: Object3D;
    scene.traverse((node) => {
      if (node.userData['entityId'] === 'stand-id') stand = node;
    });
    const label = new Sprite(new SpriteMaterial({ depthTest: false }));
    label.scale.set(10, 10, 1);
    stand.add(label);
    camera.position.set(1000, 0, 1300);
    camera.lookAt(1000, 0, 1200);
    camera.updateMatrixWorld(true);
    // A separate selectable resource blocks the stand geometry, while its label stays visible.
    const vehicle = stand.parent!.children.find(
      (node) => node.userData['entityId'] === 'vehicle-id',
    )!;
    vehicle.position.z = 1250;
    vi.spyOn(canvas, 'getBoundingClientRect').mockReturnValue(new DOMRect(0, 0, 800, 400));
    const selected = vi.fn();
    renderer.setSelectionHandler(selected);
    for (const type of ['pointerdown', 'pointerup']) {
      const event = new MouseEvent(type, { clientX: 400, clientY: 200 });
      Object.defineProperties(event, { pointerId: { value: 1 }, pointerType: { value: 'mouse' } });
      canvas.dispatchEvent(event);
    }
    expect(selected).toHaveBeenLastCalledWith(expect.objectContaining({ entityId: 'stand-id' }));
    renderer.dispose();
  });
  it('filters stands and apron independently and preserves selection when another layer changes', async () => {
    const loader = vi.fn(async (path: string, _signal: AbortSignal) => exteriorFixture(path));
    const { renderer } = setup(loader);
    await renderer.ready;
    renderer.setExteriorDetailPolicy('low');
    await renderer.setDisplayMode('exterior');
    const selected = vi.fn();
    renderer.setSelectionHandler(selected);
    renderer.selectEntity('stand-id');
    selected.mockClear();
    const resources = {
      aircraft: true,
      baggage: true,
      vehicles: false,
      runway: false,
      stands: true,
      apron: true,
    };
    renderer.setResources(resources);
    expect(selected).not.toHaveBeenCalled();
    expect(
      renderer
        .getSelectableZones()
        .filter((zone) => zone.exterior)
        .map((zone) => zone.entityId),
    ).toEqual(['stand-id', 'apron-id']);
    renderer.setResources({ ...resources, stands: false });
    expect(selected).toHaveBeenLastCalledWith(null);
    expect(
      renderer
        .getSelectableZones()
        .filter((zone) => zone.exterior)
        .map((zone) => zone.entityId),
    ).toEqual(['apron-id']);
    renderer.dispose();
  });
  it('refreshes demonstrative aircraft metadata on a scenario update without moving the investigated camera', async () => {
    const { renderer, graphics, view } = setup();
    await renderer.ready;
    renderer.setExteriorDetailPolicy('low');
    renderer.setScene({ aircraft: [{ id: 'AS 201', x: 20, z: 40, heading: 90 }] });
    await renderer.setDisplayMode('exterior');
    const selected = vi.fn();
    renderer.setSelectionHandler(selected);
    renderer.selectEntity('aircraft-entity');
    expect(selected.mock.calls.at(-1)![0].exterior).toMatchObject({
      positionSource: 'scenario-demo',
      positionMeters: [20, 8.7, 40],
      headingDegrees: 90,
    });
    const [, camera] = view();
    const before = camera.position.clone();
    const snapshot = Object.freeze({
      aircraft: [Object.freeze({ id: 'AS 201', x: 42, z: 75, heading: 180 })],
    });
    renderer.setScene(snapshot);
    expect(selected.mock.calls.at(-1)![0].exterior).toMatchObject({
      positionMeters: [42, 8.7, 75],
      headingDegrees: 180,
    });
    expect(camera.position).toEqual(before);
    expect(snapshot.aircraft[0].x).toBe(42);
    renderer.setScene({ aircraft: [] });
    expect(selected).toHaveBeenLastCalledWith(null);
    renderer.dispose();
  });
  it('retains simplified exterior and selection when the optional detail load fails', async () => {
    const loader = vi.fn(async (path: string, _signal: AbortSignal) => {
      if (path.includes('exterior-detail')) throw new Error('detail unavailable');
      return exteriorFixture(path);
    });
    const { renderer, graphics, onError, controls, view } = setup(loader);
    await renderer.ready;
    renderer.setExteriorDetailPolicy('low');
    await renderer.setDisplayMode('exterior');
    renderer.selectEntity('stand-id');
    const selected = vi.fn(),
      status = vi.fn();
    renderer.setSelectionHandler(selected);
    renderer.setAssetStatusHandler(status);
    const [, camera] = view();
    camera.position.copy(controls.target).add(new Vector3(0, 100, 0));
    renderer.setExteriorDetailPolicy('auto');
    await vi.waitFor(() =>
      expect(status).toHaveBeenLastCalledWith(
        expect.objectContaining({ loading: false, error: expect.any(String) }),
      ),
    );
    expect(renderer.getSelectableZones().filter((zone) => zone.exterior)).toHaveLength(3);
    expect(selected).not.toHaveBeenCalled();
    expect(onError).not.toHaveBeenCalled();
    renderer.dispose();
  });
  it('disposes an asset that finishes loading after renderer destruction', async () => {
    let finish!: (group: Group) => void;
    const loader = vi.fn(
      (_path: string, _signal: AbortSignal) =>
        new Promise<Group>((resolve) => {
          finish = resolve;
        }),
    );
    const { renderer } = setup(loader);
    const late = asset('terminal');
    const mesh = late.children[0].children[0] as Mesh;
    const disposed = vi.spyOn(mesh.geometry, 'dispose');
    renderer.dispose();
    finish(late);
    await renderer.ready;
    expect(disposed).toHaveBeenCalledOnce();
    expect(late.children).toHaveLength(0);
    expect(loader.mock.calls[0][1].aborted).toBe(true);
  });
  it('shows ceiling and shell only in immersive/exterior views, focuses a control and restores the cutaway', async () => {
    const loader = vi.fn(async (path: string, _signal: AbortSignal) => {
      const group = asset(path);
      if (!path.includes('exterior')) {
        for (const mode of ['immersive', 'reference', 'exterior']) {
          const roof = new Group();
          roof.name = mode;
          roof.userData =
            mode === 'exterior'
              ? { viewMode: mode }
              : { viewMode: mode, kind: mode === 'reference' ? 'roof' : 'ceiling' };
          roof.add(new Mesh(new BoxGeometry(8, 0.2, 8), new MeshStandardMaterial()));
          group.add(roof);
        }
      }
      return group;
    });
    const { renderer, graphics, controls, view } = setup(loader);
    await renderer.ready;
    const [scene, camera] = view();
    const ceiling = scene.getObjectByName('immersive')!;
    const legacy = scene.getObjectByName('reference')!;
    const shell = scene.getObjectByName('exterior')!;
    expect(shell.visible).toBe(false);
    const overview = camera.position.distanceTo(controls.target);
    renderer.selectEntity('zone-entity');
    expect(camera.position.distanceTo(controls.target)).toBeLessThan(overview / 2);
    await renderer.setDisplayMode('exterior');
    expect(shell.visible).toBe(true);
    await renderer.setDisplayMode('interior');
    expect(shell.visible).toBe(false);
    expect(camera.position.distanceTo(controls.target)).toBeCloseTo(overview);
    const snapshot = { aircraft: [Object.freeze({ id: 'AS 201', x: 12, z: 8, heading: 0 })] };
    renderer.setScene(snapshot);
    expect(ceiling.visible).toBe(false);
    renderer.setCameraView('immersive');
    expect(ceiling.visible).toBe(true);
    expect(camera.fov).toBe(60);
    expect(camera.position.y).toBeCloseTo(2.25);
    renderer.setCameraView('security');
    expect(ceiling.visible).toBe(false);
    expect(camera.fov).toBe(42);
    expect(controls.target.toArray()).toEqual([0, 0, 0]);
    await renderer.setDisplayMode('exterior');
    expect(ceiling.visible).toBe(true);
    expect(legacy.visible).toBe(false);
    await renderer.setDisplayMode('interior');
    expect(ceiling.visible).toBe(false);
    expect(snapshot.aircraft[0]).toEqual({ id: 'AS 201', x: 12, z: 8, heading: 0 });
    renderer.dispose();
  });
  it('releases a daylight texture that finishes loading after destruction', async () => {
    let finish!: (texture: Texture) => void;
    const environment = vi.fn(
      (_path: string, _signal: AbortSignal) =>
        new Promise<Texture>((resolve) => {
          finish = resolve;
        }),
    );
    const { renderer } = setup(undefined, environment);
    await renderer.ready;
    renderer.dispose();
    const texture = new Texture();
    const dispose = vi.spyOn(texture, 'dispose');
    finish(texture);
    await vi.waitFor(() => expect(dispose).toHaveBeenCalledOnce());
    expect(environment.mock.calls[0][1].aborted).toBe(true);
  });
  it('reuses the highlight and preserves a navigated camera across scene updates and resize', async () => {
    const { renderer, graphics, controls, resize, view } = setup();
    await renderer.ready;
    renderer.selectEntity('zone-entity');
    const [scene, camera] = view();
    let outline!: BoxHelper;
    scene.traverse((node) => {
      if (node instanceof BoxHelper) outline = node;
    });
    const geometry = outline.geometry,
      material = outline.material;
    const origin = camera.position.clone();
    renderer.zoomCamera('in');
    expect(camera.position.distanceTo(controls.target)).toBeLessThan(
      origin.distanceTo(controls.target),
    );
    const navigated = camera.position.clone();
    renderer.setScene({ aircraft: [], scenarioId: 'peak' });
    resize();
    expect(camera.position).toEqual(navigated);
    renderer.selectEntity('');
    expect(outline.visible).toBe(false);
    renderer.selectEntity('zone-entity');
    expect(outline.geometry).toBe(geometry);
    expect(outline.material).toBe(material);
    renderer.setNavigationMode('pan');
    expect(controls.mouseButtons.LEFT).toBe(MOUSE.PAN);
    expect(controls.touches.ONE).toBe(TOUCH.PAN);
    renderer.setNavigationMode('orbit');
    expect(controls.mouseButtons.LEFT).toBe(MOUSE.ROTATE);
    expect(controls.listenToKeyEvents).toHaveBeenCalledOnce();
    renderer.dispose();
  });
  it('does not select after a drag returning to its start, a secondary click, a pinch or pointer cancellation', async () => {
    const { renderer, canvas } = setup();
    await renderer.ready;
    const select = vi.fn();
    renderer.setSelectionHandler(select);
    const pointer = (type: string, id: number, x = 10, button = 0) => {
      const event = new MouseEvent(type, { clientX: x, clientY: 10, button });
      Object.defineProperty(event, 'pointerId', { value: id });
      canvas.dispatchEvent(event);
    };
    pointer('pointerdown', 1);
    pointer('pointermove', 1, 50);
    pointer('pointermove', 1);
    pointer('pointerup', 1);
    pointer('pointerdown', 2, 10, 2);
    pointer('pointerup', 2, 10, 2);
    pointer('pointerdown', 3);
    pointer('pointerdown', 4);
    pointer('pointerup', 4);
    pointer('pointerup', 3);
    pointer('pointerdown', 5);
    pointer('pointercancel', 5);
    pointer('pointerup', 5);
    expect(select).not.toHaveBeenCalled();
    renderer.dispose();
  });
  it('reduces rendering resolution and stops 3D on sustained low FPS, with pause excluded from the sample', async () => {
    const { renderer, graphics, onError, loop } = setup();
    await renderer.ready;
    const sample = vi.fn();
    renderer.setPerformanceHandler(sample);
    const frame = loop();
    for (let time = 0; time < 14000; time += 50) frame(time, {} as XRFrame);
    expect(graphics.setPixelRatio).toHaveBeenCalledWith(0.75);
    expect(sample).toHaveBeenLastCalledWith(
      expect.objectContaining({ fps: 20, state: 'fallback' }),
    );
    expect(onError).toHaveBeenCalledWith('performance');
    expect(graphics.setAnimationLoop).toHaveBeenLastCalledWith(null);
    renderer.dispose();
  });
  it('resets the real OrbitControls camera without residual rotation from damping', async () => {
    const { renderer, graphics, orbit, view } = setup(undefined, undefined, true);
    await renderer.ready;
    const [, camera] = view();
    const initial = camera.position.clone(),
      target = orbit().target.clone();
    orbit().rotateLeft(0.7);
    orbit().update();
    expect(camera.position.distanceTo(initial)).toBeGreaterThan(1);
    renderer.resetCamera();
    for (let n = 0; n < 20; n++) orbit().update();
    expect(camera.position.distanceTo(initial)).toBeLessThan(0.000001);
    expect(orbit().target.distanceTo(target)).toBeLessThan(0.000001);
    renderer.dispose();
  });
});

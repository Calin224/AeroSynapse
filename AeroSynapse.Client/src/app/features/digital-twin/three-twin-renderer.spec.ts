import {
  BoxGeometry,
  Group,
  Material,
  Mesh,
  MeshStandardMaterial,
  Object3D,
  PerspectiveCamera,
  Scene,
  Texture,
  Vector3,
} from 'three';
import { disposeSceneResources } from './scene-resources';
import { GraphicsBackend, RendererDependencies, ThreeTwinRenderer } from './three-twin-renderer';

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
  function setup(loadAsset = vi.fn(async (path: string, _signal: AbortSignal) => asset(path))) {
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
      update: vi.fn(),
      dispose: vi.fn(),
    };
    const observer = { observe: vi.fn(), disconnect: vi.fn() };
    const loadTexture = vi.fn(async (_path: string) => new Texture());
    const dependencies: RendererDependencies = {
      graphics: () => graphics,
      controls: () => controls,
      observer: () => observer,
      loadAsset,
      loadTexture,
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
      loadTexture,
    };
  }
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
  it('applies a surface texture to the selected zone and restores its original material', async () => {
    const { renderer, graphics, loadTexture } = setup();
    await renderer.ready;
    const [scene] = vi.mocked(graphics.render).mock.calls.at(-1)! as [Scene, PerspectiveCamera];
    let mesh: Mesh | undefined;
    scene.traverse((object) => {
      if (object instanceof Mesh && object.parent?.userData['entityId'] === 'zone-entity')
        mesh = object;
    });
    expect(mesh).toBeDefined();
    const original = mesh!.material;

    await renderer.applySurface('zone-entity', 'marble');

    const texture = await loadTexture.mock.results[0].value;
    expect(loadTexture).toHaveBeenCalledWith('materials/airport/marble_01_Diffuse.jpg');
    expect(renderer.getAppliedSurface('zone-entity')).toBe('marble');
    expect(mesh!.material).not.toBe(original);
    expect((mesh!.material as MeshStandardMaterial).map).toBe(texture);

    renderer.resetSurface('zone-entity');
    expect(mesh!.material).toBe(original);
    expect(renderer.getAppliedSurface('zone-entity')).toBeNull();
    renderer.dispose();
  });
  it('reports a lost context and removes its listener on destruction', () => {
    const { canvas, renderer, onError } = setup();
    canvas.dispatchEvent(new Event('webglcontextlost', { cancelable: true }));
    expect(onError).toHaveBeenCalledOnce();
    renderer.dispose();
    canvas.dispatchEvent(new Event('webglcontextlost'));
    expect(onError).toHaveBeenCalledOnce();
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
          loadTexture: vi.fn(),
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
    const { renderer, loadAsset, graphics } = setup();
    await renderer.ready;
    const data = { aircraft: [Object.freeze({ id: 'AS 201', x: -28, z: -65, heading: 0 })] };
    renderer.setScene(data);
    expect(loadAsset).toHaveBeenCalledTimes(1);
    await renderer.setDisplayMode('exterior');
    const [scene] = vi.mocked(graphics.render).mock.calls.at(-1)! as [Scene, PerspectiveCamera];
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
    renderer.setResources({ aircraft: false, baggage: true, vehicles: true, runway: false });
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
  it('loads detail only near the exterior and reuses it across mode changes', async () => {
    const { renderer, loadAsset, graphics } = setup();
    await renderer.ready;
    await renderer.setDisplayMode('exterior');
    expect(loadAsset).toHaveBeenCalledTimes(2);
    const [, camera] = vi.mocked(graphics.render).mock.calls.at(-1)! as [Scene, PerspectiveCamera];
    const frame = vi
      .mocked(graphics.setAnimationLoop)
      .mock.calls.find(([callback]) => typeof callback === 'function')![0]!;
    camera.position.set(0, 70, 0);
    frame(0, {} as XRFrame);
    await vi.waitFor(() => expect(loadAsset).toHaveBeenCalledTimes(3));
    expect(loadAsset.mock.calls[2][0]).toContain('exterior-detail.glb');
    await renderer.setDisplayMode('interior');
    await renderer.setDisplayMode('exterior');
    camera.position.set(0, 70, 0);
    frame(0, {} as XRFrame);
    expect(loadAsset).toHaveBeenCalledTimes(3);
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
});

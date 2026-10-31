import {
  BoxGeometry,
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
  function setup() {
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
    const dependencies: RendererDependencies = {
      graphics: () => graphics,
      controls: () => controls,
      observer: () => observer,
    };
    const onError = vi.fn();
    const renderer = new ThreeTwinRenderer(host, onError, dependencies);
    return { host, canvas, graphics, controls, observer, renderer, onError };
  }
  it('sizes the camera and releases loop, controls, observer, GPU and canvas exactly once', () => {
    const { host, graphics, controls, observer, renderer } = setup();
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
});

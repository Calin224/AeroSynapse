import { Group, InstancedMesh, Material, Matrix4, Vector3 } from 'three';
import { PassengerLayer } from './passenger-layer';
import { PassengerPacket, preparePassengerReplay } from './passenger-model';
import { motionFixture } from './passenger.testing';
import { datasetFixture } from './heatmap.testing';
import { TERMINAL_MAP_ZONES } from './terminal-zones';
import { buildHeatmapFrame } from './heatmap-model';

describe('Instanced passengers and aggregate fallback', () => {
  /** `authored` adds asset userData (Blender paths) to the zone with that code. */
  function setup(packet: PassengerPacket, authored: Record<string, Record<string, unknown>> = {}) {
    const asset = new Group();
    for (const zone of TERMINAL_MAP_ZONES) {
      const root = new Group();
      root.userData = { clickable: true, ...zone, ...authored[zone.code] };
      asset.add(root);
    }
    const layer = new PassengerLayer();
    layer.register(asset);
    layer.set(packet, 0);
    return layer;
  }
  const packet = (individual = true): PassengerPacket => ({
    replay: preparePassengerReplay(individual ? motionFixture(1200) : datasetFixture().baseline),
    timeMinutes: 1.75,
    snapshotMinute: 1,
    playing: false,
    speed: 1,
    budget: 600,
    enabled: true,
    endMinute: 5,
  });
  it('batches 600 sampled agents, reduces count/LOD independently of the source and keeps the same GPU resources', () => {
    const state = packet(),
      layer = setup(state),
      original = state.replay.tracks.length;
    expect(layer.update(new Vector3(0, 20, 20), 0)).toMatchObject({
      rendered: 600,
      represented: 1200,
      lod: 'avatars',
    });
    const mesh = layer.group.children.find((child) => child.visible) as InstancedMesh;
    const matrix = new Matrix4();
    mesh.getMatrixAt(0, matrix);
    const location = new Vector3().setFromMatrixPosition(matrix);
    expect(location.x).toBe(10);
    expect(location.y).toBeCloseTo(0.45, 6);
    expect(location.z).toBe(5);
    const geometry = mesh.geometry,
      material = mesh.material;
    layer.set({ ...state, budget: 100 }, 0);
    expect(layer.update(new Vector3(0, 20, 20), 0).rendered).toBe(100);
    expect(mesh.geometry).toBe(geometry);
    expect(mesh.material).toBe(material);
    expect(state.replay.tracks.length).toBe(original);
    layer.set(state, 0);
    expect(layer.update(new Vector3(0, 200, 200), 0)).toMatchObject({
      rendered: 150,
      represented: 1200,
      lod: 'markers',
    });
    const releases = layer.group.children.map((child) =>
      vi.spyOn(child as InstancedMesh, 'dispose'),
    );
    const releaseGeometry = vi.spyOn(geometry, 'dispose'),
      releaseMaterial = vi.spyOn(material as Material, 'dispose');
    layer.dispose();
    for (const release of [...releases, releaseGeometry, releaseMaterial])
      expect(release).toHaveBeenCalledOnce();
  });
  it('renders snapshot-derived aggregate groups, interpolates visual density and never alters the KPI', () => {
    const data = datasetFixture(),
      source = JSON.stringify(data),
      state = packet(false),
      layer = setup(state);
    const first = layer.update(new Vector3(0, 20, 20), 0);
    expect(first).toMatchObject({ represented: 6, mode: 'aggregate' });
    expect(first.rendered).toBeGreaterThan(6);
    layer.set({ ...state, budget: 1 }, 0);
    expect(layer.update(new Vector3(0, 20, 20), 0)).toMatchObject({ rendered: 1, represented: 6 });
    expect(buildHeatmapFrame(data, 'baseline', 1).kpis.queuedPassengers).toBe(6);
    expect(JSON.stringify(data)).toBe(source);
    layer.dispose();
  });
  it('stands aggregate queues as people along the authored checkpoint path, doubling up when a lane is full', () => {
    const state = { ...packet(false), interpolateAggregates: false };
    // Blender Z-up world metres: head at x=6, 6 m lane heading east.
    const layer = setup(state, { 'CP-1': { queuePathBlender: [6, 27, 0.45, 12, 27, 0.45] } });
    expect(layer.update(new Vector3(0, 20, 20), 0)).toMatchObject({ rendered: 6, represented: 6 });
    const mesh = layer.group.children.find((child) => child.visible) as InstancedMesh;
    expect(mesh.geometry.attributes['position'].count).toBeGreaterThan(300);
    const at = (i: number) => {
      const matrix = new Matrix4();
      mesh.getMatrixAt(i, matrix);
      return new Vector3().setFromMatrixPosition(matrix);
    };
    expect(at(0).x).toBeCloseTo(6, 0);
    expect(at(0).y).toBeCloseTo(0.462, 3);
    expect(at(0).z).toBeCloseTo(-27, 0);
    expect(at(1).x - at(0).x).toBeCloseTo(0.62, 1);
    // 21 queued on a 6 m path cannot form a single file: pairs share a rank across the lane.
    layer.set({ ...state, snapshotMinute: 2 }, 0);
    expect(layer.update(new Vector3(0, 20, 20), 0)).toMatchObject({
      rendered: 21,
      represented: 21,
    });
    expect(Math.abs(at(0).z - at(1).z)).toBeGreaterThan(0.35);
    expect(Math.max(...Array.from({ length: 21 }, (_, i) => at(i).x))).toBeLessThanOrEqual(12.01);
    layer.dispose();
  });
  it('stands the simulated check-in queue at the desks, like a control queue', () => {
    const desks = TERMINAL_MAP_ZONES.find((zone) => zone.zoneType === 'checkIn')!;
    const run = datasetFixture().baseline;
    run.capacities.push({
      zoneId: desks.zoneId,
      label: 'CHECK-IN',
      serviceRatePerMinute: 0.5,
      staffCount: 8,
      zoneType: 'checkIn',
    });
    for (const minute of new Set(run.snapshots.map((row) => row.elapsedMinutes)))
      run.snapshots.push({
        zoneId: desks.zoneId,
        elapsedMinutes: minute,
        queuedPassengers: 4,
        processedPassengers: minute,
      });
    // The desk line authored in Blender: head in front of the middle desks, running west.
    const layer = setup(
      { ...packet(false), replay: preparePassengerReplay(run), interpolateAggregates: false },
      { 'CHECK-IN': { queuePathBlender: [3.5, 61.1, 0.45, -9.6, 61.1, 0.45] } },
    );
    // The fixture's 6 control passengers plus the 4 waiting at check-in, none unmapped.
    expect(layer.update(new Vector3(0, 20, 20), 0)).toMatchObject({
      rendered: 10,
      represented: 10,
      unmapped: 0,
    });
    const mesh = layer.group.children.find((child) => child.visible) as InstancedMesh;
    const atDesks = Array.from({ length: 10 }, (_, i) => {
      const matrix = new Matrix4();
      mesh.getMatrixAt(i, matrix);
      return new Vector3().setFromMatrixPosition(matrix);
    }).filter((at) => Math.abs(at.z + 61.1) < 0.3);
    expect(atDesks).toHaveLength(4);
    expect(atDesks[0].x).toBeCloseTo(3.5, 0);
    layer.dispose();
  });
  it('walks arrivals and screened passengers along authored routes as a pure function of time', () => {
    const camera = new Vector3(0, 20, 20);
    const state = { ...packet(false), timeMinutes: 1.75 };
    const layer = setup(state, {
      'CP-1': {
        queuePathBlender: [6, 27, 0.45, 12, 27, 0.45],
        flowPathsBlender: JSON.stringify({
          arrive: [[40, 27, 0.45, 13, 27, 0.45]],
          depart: [[6, 27, 0.45, -60, 27, 0.45]],
        }),
      },
    });
    const moving = layer.update(camera, 0);
    // Screened 3 → 6 and arrivals 9 → 27 between minutes 1 and 2 put people on both routes.
    expect(moving.walking).toBeGreaterThan(0);
    expect(moving).toMatchObject({ represented: 6, mode: 'aggregate' });
    const mesh = layer.group.children.find((child) => child.visible) as InstancedMesh;
    const frame = [...mesh.instanceMatrix.array.slice(0, moving.rendered * 16)];
    layer.set({ ...state, timeMinutes: 1.2 }, 0);
    layer.update(camera, 0);
    layer.set(state, 0);
    expect(layer.update(camera, 0).rendered).toBe(moving.rendered);
    expect([...mesh.instanceMatrix.array.slice(0, moving.rendered * 16)]).toEqual(frame);
    layer.set({ ...state, interpolateAggregates: false }, 0);
    expect(layer.update(camera, 0).walking).toBe(0);
    layer.dispose();
  });
  it('sends screened walkers only to gates whose flights have passengers', () => {
    const state = { ...packet(false), timeMinutes: 1.9 };
    state.replay.gateDemand = new Map([['gate-b', 120]]);
    const layer = setup(state, {
      'CP-1': {
        flowPathsBlender: JSON.stringify({
          arrive: [[40, 27, 0.45, 13, 27, 0.45]],
          // Route to gate A heads west (negative x); route to gate B heads north from the head.
          depart: [
            [6, 27, 0.45, -60, 27, 0.45],
            [6, 27, 0.45, 6, 90, 0.45],
          ],
          departGates: ['gate-a', 'gate-b'],
        }),
      },
    });
    const stats = layer.update(new Vector3(0, 20, 20), 0);
    expect(stats.walking).toBeGreaterThan(0);
    const mesh = layer.group.children.find((child) => child.visible) as InstancedMesh;
    // Walkers follow the queue figures in the instance buffer.
    const xs = Array.from({ length: stats.walking! }, (_, i) => {
      const matrix = new Matrix4();
      mesh.getMatrixAt(stats.rendered - stats.walking! + i, matrix);
      return new Vector3().setFromMatrixPosition(matrix).x;
    });
    expect(Math.min(...xs)).toBeGreaterThan(3);
    layer.dispose();
  });
  it('holds replay aggregate geometry on the observed snapshot without interpolating future queue counts', () => {
    const state = { ...packet(false), interpolateAggregates: false };
    const layer = setup(state),
      camera = new Vector3(0, 20, 20);
    const first = layer.update(camera, 0);
    expect(first).toMatchObject({ represented: 6, rendered: 6, mode: 'aggregate' });
    const mesh = layer.group.children.find((child) => child.visible) as InstancedMesh;
    const matrices = [...mesh.instanceMatrix.array];
    layer.set({ ...state, timeMinutes: 1.99, playing: true }, 0);
    expect(layer.update(camera, 190)).toMatchObject({ represented: 6, rendered: 6 });
    expect([...mesh.instanceMatrix.array]).toEqual(matrices);
    layer.set({ ...state, snapshotMinute: 99 }, 0);
    expect(layer.update(camera, 0)).toMatchObject({ represented: 0, rendered: 0 });
    layer.dispose();
  });
  it('reports an unknown zone safely and clears stale instances for absent data', () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {}),
      state = packet(false);
    state.replay.snapshots.get(1)![0] = { ...state.replay.snapshots.get(1)![0], zoneId: 'unknown' };
    const layer = setup(state);
    expect(layer.update(new Vector3(0, 20, 20), 0).rendered).toBe(0);
    expect(warn).toHaveBeenCalledWith(
      'Zonă pasageri fără mesh',
      expect.objectContaining({ zoneId: 'unknown' }),
    );
    // No fallback queue footprint exists, so this ID cannot receive a fabricated placement.
    layer.set(null);
    expect(layer.update(new Vector3()).rendered).toBe(0);
    layer.dispose();
    warn.mockRestore();
  });
});

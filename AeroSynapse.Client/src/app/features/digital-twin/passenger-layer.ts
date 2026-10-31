import {
  BufferGeometry,
  Color,
  CylinderGeometry,
  DynamicDrawUsage,
  Group,
  IcosahedronGeometry,
  InstancedMesh,
  MeshStandardMaterial,
  MeshBasicMaterial,
  Object3D,
  SphereGeometry,
  Vector3,
} from 'three';
import { mergeGeometries } from 'three/addons/utils/BufferGeometryUtils.js';
import {
  MAX_PASSENGER_INSTANCES,
  PassengerPacket,
  PassengerStats,
  Position3,
  samplePassenger,
} from './passenger-model';
import { TERMINAL_MAP_ZONES, TerminalMapZone } from './terminal-zones';

function merge(parts: BufferGeometry[]): BufferGeometry {
  const compatible = parts.map((part) => (part.index ? part.toNonIndexed() : part));
  const geometry = mergeGeometries(compatible)!;
  compatible.forEach((part, i) => {
    if (part !== parts[i]) part.dispose();
  });
  parts.forEach((part) => part.dispose());
  return geometry;
}

/** 1.72 m standing figure: legs, tapered torso, arms and head, about 190 triangles. */
function avatarGeometry(): BufferGeometry {
  const parts: BufferGeometry[] = [
    new CylinderGeometry(0.21, 0.17, 0.62, 8).translate(0, 1.13, 0),
    new SphereGeometry(0.12, 8, 6).translate(0, 1.6, 0),
  ];
  for (const side of [-1, 1]) {
    parts.push(new CylinderGeometry(0.075, 0.062, 0.84, 6).translate(side * 0.1, 0.42, 0));
    parts.push(
      new CylinderGeometry(0.055, 0.048, 0.6, 5)
        .rotateZ(side * 0.08)
        .translate(side * 0.27, 1.1, 0),
    );
  }
  return merge(parts);
}

/** Far LOD pin: a chunky body and head that stays legible from the overview camera. */
function markerGeometry(): BufferGeometry {
  return merge([
    new CylinderGeometry(0.26, 0.22, 1.45, 6).translate(0, 0.73, 0),
    new IcosahedronGeometry(0.24, 0).translate(0, 1.72, 0),
  ]);
}

// Queued people wear warm tones so a line reads at a glance against the cool lane floor.
const QUEUE_CLOTHING = ['#ffc45b', '#ff9f4a', '#f6e7c8', '#ffb38a', '#f2d05a', '#e8845c'];
const QUEUE_SPACING = 0.62;
const QUEUE_FILE_OFFSET = 0.27;
const MAX_QUEUE_FIGURES = 160;
// Zones whose snapshots are waiting lines: the security controls and the check-in desks before them.
const QUEUE_ZONES = new Set<TerminalMapZone['zoneType']>(['checkpoint', 'checkIn']);
// People in motion are cyan, matching individual trajectories; 1.3 m/s walking pace in simulated time.
const WALKING_CLOTHING = ['#5dd8ed', '#86e6f4', '#3fc1e0', '#b5f1f9'];
const WALK_METRES_PER_MINUTE = 78;
const WALKER_SHARE = 0.4;
const WALK_LANE_WIDTH = 1.8;

interface QueuePath {
  points: Vector3[];
  distances: number[];
  length: number;
}
/** Illustrative walking routes authored in Blender; flow volumes come from snapshot deltas. */
interface ZoneFlows {
  arrive: QueuePath[];
  depart: QueuePath[];
  /** Gate zone each departure route ends at, parallel to `depart` (empty for older models). */
  departGates: string[];
}
/** One checkpoint's snapshots, sorted, for piecewise-linear cumulative curves. */
interface ZoneSeries {
  minutes: number[];
  processed: number[];
  arrived: number[];
}

/** Blender-authored world polyline (flat x,y,z triplets, Z-up) converted to Three.js Y-up metres. */
function queuePath(value: unknown): QueuePath | null {
  if (
    !Array.isArray(value) ||
    value.length < 6 ||
    value.length % 3 ||
    !value.every((n) => typeof n === 'number' && Number.isFinite(n) && Math.abs(n) < 10000)
  )
    return null;
  const points: Vector3[] = [];
  for (let i = 0; i < value.length; i += 3)
    points.push(new Vector3(value[i], value[i + 2] + 0.012, -value[i + 1]));
  const distances = [0];
  for (let i = 1; i < points.length; i++)
    distances.push(distances[i - 1] + points[i].distanceTo(points[i - 1]));
  return { points, distances, length: distances.at(-1)! };
}

function flowPaths(value: unknown): ZoneFlows | null {
  if (typeof value !== 'string') return null;
  try {
    const raw = JSON.parse(value) as Record<string, unknown>;
    const parse = (list: unknown) =>
      Array.isArray(list) ? list.map(queuePath).filter((p): p is QueuePath => p !== null) : [];
    const depart = parse(raw['depart']);
    const gates = raw['departGates'];
    const departGates =
      Array.isArray(gates) &&
      gates.length === depart.length &&
      gates.every((id) => typeof id === 'string')
        ? (gates as string[])
        : [];
    const flows = { arrive: parse(raw['arrive']), depart, departGates };
    return flows.arrive.length && flows.depart.length ? flows : null;
  } catch {
    return null;
  }
}

function sample(path: QueuePath, distance: number): { at: Vector3; dx: number; dz: number } {
  const d = Math.max(0, Math.min(path.length, distance));
  let segment = 1;
  while (segment < path.points.length - 1 && path.distances[segment] < d) segment++;
  const a = path.points[segment - 1],
    b = path.points[segment];
  const span = path.distances[segment] - path.distances[segment - 1] || 1;
  const t = (d - path.distances[segment - 1]) / span;
  return {
    at: new Vector3(a.x + (b.x - a.x) * t, a.y, a.z + (b.z - a.z) * t),
    dx: (b.x - a.x) / span,
    dz: (b.z - a.z) / span,
  };
}

function valueAt(minutes: number[], values: number[], time: number): number {
  if (time <= minutes[0]) return values[0];
  for (let i = 1; i < minutes.length; i++)
    if (time <= minutes[i])
      return (
        values[i - 1] +
        ((values[i] - values[i - 1]) * (time - minutes[i - 1])) / (minutes[i] - minutes[i - 1])
      );
  return values.at(-1)!;
}

/** First time the cumulative curve reaches `target`, or undefined if it never does. */
function timeOf(minutes: number[], values: number[], target: number): number | undefined {
  if (values[0] >= target) return minutes[0];
  for (let i = 1; i < minutes.length; i++)
    if (values[i] >= target)
      return (
        minutes[i - 1] +
        ((target - values[i - 1]) * (minutes[i] - minutes[i - 1])) / (values[i] - values[i - 1])
      );
  return undefined;
}

function zoneSeries(replay: PassengerPacket['replay']): Map<string, ZoneSeries> {
  const series = new Map<string, ZoneSeries>();
  for (const minute of replay.minutes)
    for (const row of replay.snapshots.get(minute) ?? []) {
      const zone = series.get(row.zoneId) ?? { minutes: [], processed: [], arrived: [] };
      // Cumulative curves must not run backwards even if a snapshot is revised downwards.
      const processed = Math.max(row.processedPassengers, zone.processed.at(-1) ?? 0);
      zone.minutes.push(minute);
      zone.processed.push(processed);
      zone.arrived.push(Math.max(processed + row.queuedPassengers, zone.arrived.at(-1) ?? 0));
      series.set(row.zoneId, zone);
    }
  return series;
}

function jitter(i: number, salt: number): number {
  const x = Math.sin(i * 12.9898 + salt * 78.233) * 43758.5453;
  return x - Math.floor(x) - 0.5;
}

/**
 * Slot 0 stands at the checkpoint; the line doubles up once a single file would overflow the lanes.
 * `advance` in [0,1) slides everyone forward between two screenings, so the line moves continuously.
 */
function queueSlot(
  path: QueuePath,
  index: number,
  total: number,
  advance?: number,
): [Position3, number] {
  const single = Math.floor(path.length / QUEUE_SPACING) + 1;
  const double = total > single;
  const rank = double ? Math.floor(index / 2) : index;
  const ranks = double ? Math.ceil(total / 2) : total;
  const spacing = Math.min(QUEUE_SPACING, path.length / Math.max(1, ranks - 1));
  const { at, dx, dz } = sample(path, (rank + (advance === undefined ? 0 : 1 - advance)) * spacing);
  const lateral = (double ? (index % 2 ? 1 : -1) * QUEUE_FILE_OFFSET : 0) + jitter(index, 1) * 0.12;
  return [
    [at.x - dz * lateral, at.y, at.z + dx * lateral],
    // Face the head of the line, with a little natural variation.
    Math.atan2(-dx, -dz) + jitter(index, 2) * 0.5,
  ];
}

/** A walker `distance` metres along a route, spread across a walking lane and facing forward. */
function walkSlot(path: QueuePath, distance: number, id: number): [Position3, number] {
  const { at, dx, dz } = sample(path, distance);
  const lateral = jitter(id, 3) * WALK_LANE_WIDTH;
  return [[at.x - dz * lateral, at.y, at.z + dx * lateral], Math.atan2(dx, dz)];
}

/** Bundled-zone fallback for models without an authored queue path. */
function gridSlot(zone: TerminalMapZone, index: number): [Position3, number] {
  const columns = 6,
    line = Math.floor(index / columns),
    column = line % 2 ? columns - 1 - (index % columns) : index % columns;
  return [
    [
      zone.bounds[0] + 0.55 + column * QUEUE_SPACING + (line % 2) * 0.12,
      0.45,
      zone.bounds[1] - 78 + 0.45 + line * QUEUE_SPACING,
    ],
    0,
  ];
}

/**
 * One visible draw call: avatars near, bright pins far. Aggregate queues stand people along the
 * GLB's checkpoint queue path; each figure represents `weight` queued passengers, never a trajectory.
 */
export class PassengerLayer {
  readonly group = new Group();
  private readonly material = new MeshStandardMaterial({
    color: 0xffffff,
    roughness: 0.45,
    metalness: 0.15,
  });
  private readonly densityMaterial = new MeshBasicMaterial({ color: 0xffffff, toneMapped: false });
  private readonly geometries = [avatarGeometry(), markerGeometry()];
  private readonly meshes: InstancedMesh[];
  private readonly transform = new Object3D();
  private readonly color = new Color();
  private readonly center = new Vector3(-10, 0.45, -38);
  private readonly knownZones = new Set<string>();
  private readonly reported = new Set<string>();
  private readonly queuePaths = new Map<string, QueuePath>();
  private readonly flows = new Map<string, ZoneFlows>();
  private series = new Map<string, ZoneSeries>();
  private seriesReplay?: PassengerPacket['replay'];
  private packet: PassengerPacket | null = null;
  private received = 0;
  private lastTime = -1;
  private lastLod = '';
  private candidates: PassengerPacket['replay']['tracks'] = [];
  private stats: PassengerStats = {
    rendered: 0,
    represented: 0,
    unmapped: 0,
    lod: 'avatars',
    mode: 'unavailable',
  };
  constructor() {
    this.meshes = this.geometries.map((geometry, index) => {
      // Far pins are unlit so a queue still reads as people from the overview camera.
      const mesh = new InstancedMesh(
        geometry,
        index ? this.densityMaterial : this.material,
        MAX_PASSENGER_INSTANCES,
      );
      mesh.name = index ? 'Passenger marker instances' : 'Passenger avatar instances';
      mesh.userData = { raycastIgnore: true };
      mesh.instanceMatrix.setUsage(DynamicDrawUsage);
      mesh.count = 0;
      mesh.frustumCulled = false;
      mesh.castShadow = false;
      this.group.add(mesh);
      return mesh;
    });
    this.group.name = 'Simulator passenger playback';
  }
  register(asset: Object3D): void {
    asset.traverse((node) => {
      if (
        node.userData['clickable'] &&
        typeof node.userData['zoneId'] === 'string' &&
        node.userData['viewMode'] !== 'reference'
      ) {
        this.knownZones.add(node.userData['zoneId']);
        const path = queuePath(node.userData['queuePathBlender']);
        if (path) this.queuePaths.set(node.userData['zoneId'], path);
        const flows = flowPaths(node.userData['flowPathsBlender']);
        if (flows) this.flows.set(node.userData['zoneId'], flows);
      }
    });
  }
  set(packet: PassengerPacket | null, now = performance.now()): void {
    this.packet = packet;
    this.received = now;
    this.lastTime = -1;
    this.candidates =
      packet?.replay.mode === 'individual'
        ? packet.replay.tracks.filter(
            (track) => samplePassenger(track, packet.replay, packet.timeMinutes) !== null,
          )
        : [];
    if (packet && packet.replay !== this.seriesReplay) {
      // Playback republishes ~10×/s; cumulative curves are rebuilt only for a new replay.
      this.seriesReplay = packet.replay;
      this.series = zoneSeries(packet.replay);
    }
    this.meshes.forEach((mesh) => (mesh.count = 0));
  }
  update(
    camera: Vector3,
    now = performance.now(),
    qualityBudget = MAX_PASSENGER_INSTANCES,
  ): PassengerStats {
    const packet = this.packet;
    if (!packet?.enabled) return this.empty();
    const far = camera.distanceTo(this.center) > (this.lastLod.startsWith('markers:') ? 115 : 135);
    const lod = far ? 'markers' : 'avatars';
    const budget = Math.max(
      1,
      Math.min(
        MAX_PASSENGER_INSTANCES,
        Math.floor(packet.budget),
        qualityBudget,
        far ? 150 : MAX_PASSENGER_INSTANCES,
      ),
    );
    const time = Math.min(
      packet.endMinute,
      packet.timeMinutes +
        (packet.playing
          ? (Math.max(0, Math.min(now - this.received, 200)) * packet.speed) / 60000
          : 0),
    );
    const signature = `${lod}:${budget}:${packet.replay.mode}`;
    if (time === this.lastTime && signature === this.lastLod) return this.stats;
    this.lastTime = time;
    this.lastLod = signature;
    for (const mesh of this.meshes) {
      mesh.visible = false;
      mesh.count = 0;
      mesh.userData['runId'] = packet.replay.runId;
      mesh.userData['snapshotMinute'] = packet.snapshotMinute;
    }
    const mesh = this.meshes[far ? 1 : 0];
    mesh.visible = true;
    mesh.userData['passengerMode'] = packet.replay.mode;
    let rendered = 0,
      represented = 0,
      unmapped = 0,
      walking = 0;
    if (packet.replay.mode === 'individual') {
      represented = packet.replay.tracks.length;
      for (const track of this.candidates) {
        if (rendered >= budget) break;
        const agent = samplePassenger(track, packet.replay, time);
        if (!agent) continue;
        if (!this.known(agent.zoneId, packet)) {
          unmapped++;
          continue;
        }
        this.write(
          mesh,
          rendered++,
          agent.position,
          agent.heading,
          1,
          agent.state === 'queued' ? '#ffc45b' : '#5dd8ed',
        );
      }
    } else if (packet.replay.mode === 'aggregate') {
      const rows = packet.replay.snapshots.get(packet.snapshotMinute) ?? [];
      const nextMinute = packet.replay.minutes.find((minute) => minute > packet.snapshotMinute);
      const nextRows =
        nextMinute === undefined || packet.interpolateAggregates === false
          ? undefined
          : packet.replay.snapshots.get(nextMinute);
      const alpha =
        nextMinute === undefined
          ? 0
          : Math.max(
              0,
              Math.min(1, (time - packet.snapshotMinute) / (nextMinute - packet.snapshotMinute)),
            );
      // Replay pages hold the exact snapshot (no interpolation), so they also show no motion.
      const moving = packet.interpolateAggregates !== false;
      const flowing = moving
        ? rows.filter((row) => this.flows.has(row.zoneId) && this.series.has(row.zoneId))
        : [];
      const queueBudget = flowing.length
        ? Math.max(1, Math.floor(budget * (1 - WALKER_SHARE)))
        : budget;
      // One figure stands for `weight` passengers when the budget or a full lane cannot show everyone.
      const weight = Math.max(
        1,
        Math.ceil(rows.reduce((sum, row) => sum + row.queuedPassengers, 0) / queueBudget),
        ...rows.map((row) => Math.ceil(row.queuedPassengers / MAX_QUEUE_FIGURES)),
      );
      for (const row of rows) {
        represented += row.queuedPassengers;
        const zone = TERMINAL_MAP_ZONES.find(
          (zone) => zone.zoneId === row.zoneId && QUEUE_ZONES.has(zone.zoneType),
        );
        if (!this.known(row.zoneId, packet) || !zone) {
          unmapped += row.queuedPassengers;
          continue;
        }
        const next = nextRows?.find((item) => item.zoneId === row.zoneId);
        // Density transitions are display-only; source counts/KPI remain exact.
        const visualQueue =
          row.queuedPassengers +
          ((next?.queuedPassengers ?? row.queuedPassengers) - row.queuedPassengers) * alpha;
        // Ceil: a non-empty queue always shows at least one person.
        const people = Math.min(MAX_QUEUE_FIGURES, Math.ceil(visualQueue / weight - 1e-9));
        const path = this.queuePaths.get(row.zoneId);
        const curve = flowing.includes(row) ? this.series.get(row.zoneId) : undefined;
        // Between two screenings the whole line slides one place forward.
        const screened = curve ? valueAt(curve.minutes, curve.processed, time) / weight : undefined;
        const advance = screened === undefined ? undefined : screened - Math.floor(screened);
        for (let i = 0; i < people && rendered < queueBudget; i++) {
          const [position, heading] = path
            ? queueSlot(path, i, people, advance)
            : gridSlot(zone, i);
          this.write(
            mesh,
            rendered++,
            position,
            heading,
            1,
            QUEUE_CLOTHING[i % QUEUE_CLOTHING.length],
          );
        }
      }
      walking = this.writeWalkers(mesh, flowing, time, rendered, budget);
      rendered += walking;
    }
    mesh.count = rendered;
    mesh.instanceMatrix.needsUpdate = true;
    if (mesh.instanceColor) mesh.instanceColor.needsUpdate = true;
    this.stats = { rendered, represented, unmapped, lod, mode: packet.replay.mode, walking };
    return this.stats;
  }
  /**
   * Walkers are the k-th screened (or arriving) passenger, placed by how long ago the cumulative
   * curve crossed k: a pure function of time, so seeking and pausing stay consistent. Volumes are
   * the simulator's; the routes are the illustrative ones authored in Blender.
   */
  private writeWalkers(
    mesh: InstancedMesh,
    rows: readonly { zoneId: string }[],
    time: number,
    first: number,
    budget: number,
  ): number {
    const plans = rows.map((row, zone) => {
      const flows = this.flows.get(row.zoneId)!,
        curve = this.series.get(row.zoneId)!;
      const departWindow = Math.max(...flows.depart.map((p) => p.length)) / WALK_METRES_PER_MINUTE;
      const arriveWindow = Math.max(...flows.arrive.map((p) => p.length)) / WALK_METRES_PER_MINUTE;
      const screened = valueAt(curve.minutes, curve.processed, time);
      const arrived = valueAt(curve.minutes, curve.arrived, time);
      return {
        zone,
        flows,
        curve,
        screened: [valueAt(curve.minutes, curve.processed, time - departWindow), screened],
        arriving: [arrived, valueAt(curve.minutes, curve.arrived, time + arriveWindow)],
      };
    });
    const expected = plans.reduce(
      (sum, p) => sum + p.screened[1] - p.screened[0] + p.arriving[1] - p.arriving[0],
      0,
    );
    const room = budget - first;
    if (room <= 0 || expected <= 0) return 0;
    const weight = Math.max(1, Math.ceil(expected / room));
    let written = 0;
    const place = (route: QueuePath, distance: number, id: number) => {
      if (distance < 0 || distance > route.length || first + written >= budget) return;
      const [position, heading] = walkSlot(route, distance, id);
      this.write(
        mesh,
        first + written++,
        position,
        heading,
        1,
        WALKING_CLOTHING[id % WALKING_CLOTHING.length],
      );
    };
    const pick = (routes: QueuePath[], id: number, weights?: number[]) => {
      const total = weights?.reduce((sum, weight) => sum + weight, 0) ?? 0;
      let u = jitter(id, 4) + 0.5;
      if (!weights || total <= 0)
        return routes[Math.min(routes.length - 1, Math.floor(u * routes.length))];
      // Screened passengers walk to their flights' gates in proportion to each gate's demand.
      u *= total;
      let chosen = 0;
      for (let index = 0; index < weights.length; index++) {
        if (weights[index] <= 0) continue;
        chosen = index;
        if ((u -= weights[index]) < 0) break;
      }
      return routes[chosen];
    };
    const demand = this.packet?.replay.gateDemand;
    for (const { zone, flows, curve, screened, arriving } of plans) {
      const weights =
        demand?.size && flows.departGates.length
          ? flows.departGates.map((gate) => demand.get(gate) ?? 0)
          : undefined;
      for (let k = Math.floor(screened[0] / weight) + 1; k * weight <= screened[1]; k++) {
        const left = timeOf(curve.minutes, curve.processed, k * weight);
        const id = k * 7 + zone * 100003;
        if (left !== undefined)
          place(pick(flows.depart, id, weights), (time - left) * WALK_METRES_PER_MINUTE, id);
      }
      for (let j = Math.floor(arriving[0] / weight) + 1; j * weight <= arriving[1]; j++) {
        const joins = timeOf(curve.minutes, curve.arrived, j * weight);
        const id = j * 11 + zone * 100019;
        const route = pick(flows.arrive, id);
        if (joins !== undefined)
          place(route, route.length - (joins - time) * WALK_METRES_PER_MINUTE, id);
      }
    }
    return written;
  }
  private known(zoneId: string, packet: PassengerPacket): boolean {
    if (this.knownZones.has(zoneId)) return true;
    const id = `${packet.replay.runId}:${zoneId}`;
    if (!this.reported.has(id)) {
      this.reported.add(id);
      console.warn('Zonă pasageri fără mesh', { runId: packet.replay.runId, zoneId });
    }
    return false;
  }
  private write(
    mesh: InstancedMesh,
    index: number,
    position: Position3,
    heading: number,
    scale: number,
    color: string,
  ): void {
    this.transform.position.set(...position);
    this.transform.rotation.set(0, heading, 0);
    this.transform.scale.setScalar(scale);
    this.transform.updateMatrix();
    mesh.setMatrixAt(index, this.transform.matrix);
    mesh.setColorAt(index, this.color.set(color));
  }
  private empty(): PassengerStats {
    this.meshes.forEach((mesh) => {
      mesh.count = 0;
      mesh.visible = false;
    });
    return (this.stats = {
      rendered: 0,
      represented: 0,
      unmapped: 0,
      lod: 'avatars',
      mode: this.packet?.replay.mode ?? 'unavailable',
    });
  }
  dispose(): void {
    this.group.removeFromParent();
    this.meshes.forEach((mesh) => mesh.dispose());
    this.geometries.forEach((geometry) => geometry.dispose());
    this.material.dispose();
    this.densityMaterial.dispose();
    this.group.clear();
    this.knownZones.clear();
    this.queuePaths.clear();
    this.flows.clear();
    this.series.clear();
    this.reported.clear();
    this.packet = null;
  }
}

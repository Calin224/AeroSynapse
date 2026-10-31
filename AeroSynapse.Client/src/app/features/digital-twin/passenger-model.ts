import type { HeatmapRun, ZoneSnapshot } from './heatmap-model';

export type Position3 = readonly [number, number, number];
/** Optional simulator output. Coordinates are authoritative GLB metres, Y-up. */
export interface PassengerMotion {
  runId: string;
  layoutId: string;
  source: 'simulator';
  units: 'meters';
  upAxis: 'Y';
  routes: { routeId: string; zoneIds: string[]; points: Position3[] }[];
  trajectories: { agentId: string; keyframes: PassengerKeyframe[] }[];
}
export interface PassengerKeyframe {
  elapsedMinutes: number;
  routeId: string;
  zoneId: string;
  progress: number;
  state: 'walking' | 'queued' | 'exited';
  queuePosition?: Position3;
}
export interface PreparedRoute {
  routeId: string;
  zoneIds: string[];
  points: Position3[];
  distances: number[];
  length: number;
}
export interface PassengerReplay {
  runId: string;
  layoutId: string;
  mode: 'individual' | 'aggregate' | 'unavailable';
  routes: Map<string, PreparedRoute>;
  tracks: PassengerMotion['trajectories'];
  snapshots: Map<number, ZoneSnapshot[]>;
  minutes: number[];
  rejectedTracks: number;
  /** Passengers per gate zone; empty when the run captured no flows. */
  gateDemand: Map<string, number>;
}
/** Playback multipliers of simulated time; 60x shows a 90-minute run in 90 seconds. */
export type PlaybackSpeed = 1 | 4 | 16 | 60;
export interface PassengerPacket {
  interpolateAggregates?: boolean;
  replay: PassengerReplay;
  timeMinutes: number;
  snapshotMinute: number;
  playing: boolean;
  speed: PlaybackSpeed;
  budget: number;
  enabled: boolean;
  endMinute: number;
}
export interface PassengerStats {
  rendered: number;
  represented: number;
  unmapped: number;
  lod: 'avatars' | 'markers';
  mode: PassengerReplay['mode'];
  /** Figures walking illustrative routes, derived from aggregate arrival/screening deltas. */
  walking?: number;
}
export const MAX_PASSENGER_INSTANCES = 600;
const position = (p: unknown): p is Position3 =>
  Array.isArray(p) &&
  p.length === 3 &&
  p.every((n) => typeof n === 'number' && Number.isFinite(n) && Math.abs(n) < 10000);
function hash(id: string): number {
  let value = 2166136261;
  for (const char of id) value = Math.imul(value ^ char.charCodeAt(0), 16777619);
  return value >>> 0;
}
/** Validate once per immutable run update; never infer paths from counts or airport geometry. */
export function preparePassengerReplay(run: HeatmapRun): PassengerReplay {
  const snapshots = new Map<number, ZoneSnapshot[]>();
  for (const row of run.snapshots) {
    if (
      !Number.isFinite(row.elapsedMinutes) ||
      row.elapsedMinutes < 0 ||
      !Number.isInteger(row.queuedPassengers) ||
      row.queuedPassengers < 0 ||
      !Number.isInteger(row.processedPassengers) ||
      row.processedPassengers < 0
    )
      continue;
    const rows = snapshots.get(row.elapsedMinutes) ?? [];
    const previous = rows.findIndex((item) => item.zoneId === row.zoneId);
    if (previous >= 0) rows[previous] = row;
    else rows.push(row);
    snapshots.set(row.elapsedMinutes, rows);
  }
  const routes = new Map<string, PreparedRoute>();
  const motion = run.passengerMotion;
  const validEnvelope =
    motion?.source === 'simulator' &&
    motion.runId === run.runId &&
    motion.layoutId === run.layoutId &&
    motion.units === 'meters' &&
    motion.upAxis === 'Y';
  if (motion && !validEnvelope)
    console.warn('Traiectorii incompatibile', {
      runId: run.runId,
      layoutId: run.layoutId,
    });
  if (validEnvelope && Array.isArray(motion.routes))
    for (const route of motion.routes) {
      if (
        !route ||
        typeof route.routeId !== 'string' ||
        routes.has(route.routeId) ||
        !Array.isArray(route.zoneIds) ||
        !route.zoneIds.length ||
        !route.zoneIds.every((id) => typeof id === 'string') ||
        !Array.isArray(route.points) ||
        route.points.length < 2 ||
        route.points.length > 2048 ||
        !route.points.every(position)
      )
        continue;
      const distances = [0];
      for (let i = 1; i < route.points.length; i++)
        distances.push(
          distances[i - 1] +
            Math.hypot(...route.points[i].map((n, axis) => n - route.points[i - 1][axis])),
        );
      const length = distances.at(-1)!;
      if (length > 0) routes.set(route.routeId, { ...route, distances, length });
    }
  const tracks: PassengerMotion['trajectories'] = [];
  let rejectedTracks = 0;
  const ids = new Set<string>();
  if (validEnvelope && Array.isArray(motion.trajectories))
    for (const track of motion.trajectories) {
      if (
        !track ||
        typeof track.agentId !== 'string' ||
        ids.has(track.agentId) ||
        !Array.isArray(track.keyframes) ||
        !track.keyframes.length ||
        track.keyframes.length > 4096 ||
        !track.keyframes.every((frame, i) => {
          const route = routes.get(frame?.routeId);
          return (
            route &&
            route.zoneIds.includes(frame.zoneId) &&
            Number.isFinite(frame.elapsedMinutes) &&
            frame.elapsedMinutes >= 0 &&
            (!i || frame.elapsedMinutes > track.keyframes[i - 1].elapsedMinutes) &&
            Number.isFinite(frame.progress) &&
            frame.progress >= 0 &&
            frame.progress <= 1 &&
            ['walking', 'queued', 'exited'].includes(frame.state) &&
            (frame.queuePosition === undefined ||
              (frame.state === 'queued' && position(frame.queuePosition)))
          );
        })
      ) {
        rejectedTracks++;
        continue;
      }
      ids.add(track.agentId);
      tracks.push(track);
    }
  if (rejectedTracks)
    console.warn('Traiectorii respinse', { runId: run.runId, count: rejectedTracks });
  tracks.sort((a, b) => hash(a.agentId) - hash(b.agentId) || a.agentId.localeCompare(b.agentId));
  const minutes = [...snapshots.keys()].sort((a, b) => a - b);
  return {
    runId: run.runId,
    layoutId: run.layoutId,
    mode: tracks.length ? 'individual' : snapshots.size ? 'aggregate' : 'unavailable',
    tracks,
    routes,
    snapshots,
    minutes,
    rejectedTracks,
    gateDemand: new Map(
      (run.gateDemand ?? [])
        .filter((row) => typeof row.gateId === 'string' && Number.isFinite(row.passengers) && row.passengers > 0)
        .map((row) => [row.gateId, row.passengers]),
    ),
  };
}
/** Distance interpolation follows every simulator polyline corner, never a straight shortcut. */
export function pointOnRoute(
  route: PreparedRoute,
  progress: number,
): { position: Position3; heading: number } {
  const distance = Math.min(1, Math.max(0, progress)) * route.length;
  let i = 1;
  while (i < route.distances.length - 1 && route.distances[i] < distance) i++;
  const a = route.points[i - 1],
    b = route.points[i],
    span = route.distances[i] - route.distances[i - 1];
  const alpha = span ? (distance - route.distances[i - 1]) / span : 0;
  return {
    position: a.map((v, axis) => v + (b[axis] - v) * alpha) as unknown as Position3,
    heading: Math.atan2(b[0] - a[0], b[2] - a[2]),
  };
}
export function samplePassenger(
  track: PassengerMotion['trajectories'][number],
  replay: PassengerReplay,
  time: number,
): {
  position: Position3;
  heading: number;
  zoneId: string;
  state: PassengerKeyframe['state'];
} | null {
  const frames = track.keyframes;
  if (time < frames[0].elapsedMinutes || time > frames.at(-1)!.elapsedMinutes) return null;
  let low = 0,
    high = frames.length;
  while (low < high) {
    const mid = (low + high) >>> 1;
    if (frames[mid].elapsedMinutes <= time) low = mid + 1;
    else high = mid;
  }
  const a = frames[Math.max(0, low - 1)],
    b = frames[low];
  if (a.state === 'exited') return null;
  const route = replay.routes.get(a.routeId)!;
  // Route changes and queue transitions are discrete simulator events. Holding
  // the previous keyframe avoids inventing a cross-route or queue movement.
  const alpha =
    b && b.routeId === a.routeId && a.state === 'walking'
      ? (time - a.elapsedMinutes) / (b.elapsedMinutes - a.elapsedMinutes)
      : 0;
  const sampled = pointOnRoute(
    route,
    a.progress + ((b?.progress ?? a.progress) - a.progress) * alpha,
  );
  return {
    ...sampled,
    position: a.queuePosition ?? sampled.position,
    zoneId: a.zoneId,
    state: a.state,
  };
}
export function snapshotAtOrBefore(minutes: number[], time: number): number | undefined {
  let low = 0,
    high = minutes.length;
  while (low < high) {
    const mid = (low + high) >>> 1;
    if (minutes[mid] <= time + 1e-8) low = mid + 1;
    else high = mid;
  }
  return minutes[low - 1];
}

/** Display clock only. It does not generate passengers, queues, physics or KPI. */
export class PassengerClock {
  time = 0;
  playing = false;
  speed: PlaybackSpeed = 1;
  private previous?: number;
  private start = 0;
  private end = 0;
  configure(start: number, end: number): void {
    this.start = start;
    this.end = Math.max(start, end);
    this.time = Math.max(this.start, Math.min(this.end, this.time));
  }
  seek(time: number): void {
    this.time = Math.max(this.start, Math.min(this.end, time));
    this.previous = undefined;
  }
  play(): void {
    if (this.time >= this.end) this.seek(this.start);
    this.playing = this.end > this.start;
    this.previous = undefined;
  }
  pause(): void {
    this.playing = false;
    this.previous = undefined;
  }
  step(timestamp: number): boolean {
    if (!this.playing) return false;
    if (this.previous !== undefined)
      this.time = Math.min(
        this.end,
        this.time + (Math.max(0, Math.min(timestamp - this.previous, 1000)) * this.speed) / 60000,
      );
    this.previous = timestamp;
    if (this.time >= this.end) this.pause();
    return true;
  }
}

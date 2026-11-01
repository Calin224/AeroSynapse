import { DecimalPipe } from '@angular/common';
import {
  Component,
  computed,
  DestroyRef,
  effect,
  inject,
  input,
  NgZone,
  signal,
  untracked,
} from '@angular/core';
import { DigitalTwin } from '../digital-twin/digital-twin';
import { PassengerClock, PlaybackSpeed } from '../digital-twin/passenger-model';
import { clockTime } from '../../shared/absolute-time';
import { eventLabel } from '../operations/events.models';
import { PreparedReplay, replayEvents, replayFrame } from './replay.models';

@Component({
  selector: 'app-replay-player',
  imports: [DecimalPipe, DigitalTwin],
  templateUrl: './replay-player.html',
  styleUrl: './replay-player.css',
})
export class ReplayPlayer {
  readonly replay = input.required<PreparedReplay>();
  /** Optional shared cursor when embedded in a before/after presentation. */
  readonly initialMinute = input(0);
  /** The parent presentation supplies its own report and zone selector. */
  readonly compact = input(false);
  readonly time = signal(0);
  readonly playing = signal(false);
  readonly speed = signal<PlaybackSpeed>(16);
  /** Highlights a zone's row; set by events that target one zone. */
  readonly zoneId = signal('');
  readonly selectedEvent = signal('');
  readonly speeds = [1, 4, 16, 60] as const;
  readonly duration = computed(() => this.replay().data.captured.scenario.inputs.durationMinutes);
  readonly minuteValue = computed(() => Math.round(this.time() * 100) / 100);
  readonly frame = computed(() => replayFrame(this.replay(), this.time()));
  readonly events = computed(() => replayEvents(this.replay()));
  readonly eventGroups = computed(() => {
    // The comparison view has no event list, so retain one accessible marker per event there.
    if (this.compact())
      return this.events().map((event) => ({ minute: event.minute, events: [event] }));
    const groups = new Map<number, ReturnType<typeof replayEvents>>();
    for (const event of this.events()) {
      const group = groups.get(event.minute) ?? [];
      group.push(event);
      groups.set(event.minute, group);
    }
    return [...groups].map(([minute, events]) => ({ minute, events }));
  });
  readonly timelineTicks = computed(() =>
    [0, 0.25, 0.5, 0.75, 1].map((fraction) => fraction * this.duration()),
  );
  /** The largest queue any zone reached, so bars compare across the whole run. */
  readonly peakQueue = computed(() =>
    Math.max(1, ...this.replay().data.samples.map((sample) => sample.queuedPassengers)),
  );
  /** Area path (viewBox 1000x40) of everyone queued per minute; a shape for finding busy moments. */
  readonly queueCurve = computed(() => {
    const replay = this.replay();
    const totals = replay.minutes.map((minute) => {
      let queued = 0;
      for (const sample of replay.byMinute.get(minute)!.values()) queued += sample.queuedPassengers;
      return [minute, queued] as const;
    });
    const peak = Math.max(1, ...totals.map(([, queued]) => queued));
    const x = (minute: number) => ((minute / this.duration()) * 1000).toFixed(1);
    const line = totals.map(
      ([minute, queued]) => `L${x(minute)},${(40 - (queued / peak) * 36).toFixed(1)}`,
    );
    return `M${x(totals[0]?.[0] ?? 0)},40 ${line.join(' ')} L${x(totals.at(-1)?.[0] ?? 0)},40 Z`;
  });
  readonly overview = computed(() => {
    const frame = this.frame();
    const samples = frame.zones.map((zone) => zone.sample);
    if (frame.desks) samples.push(frame.desks.sample);
    const available = samples.filter((sample) => sample !== null);
    const waits = samples.map(
      (sample) => sample?.kpi?.meanWaitMinutes ?? sample?.checkIn?.meanWaitMinutes ?? null,
    );
    return {
      available: available.length,
      total: samples.length,
      queued:
        samples.length && available.length === samples.length
          ? available.reduce((total, sample) => total + sample.queuedPassengers, 0)
          : null,
      maxWait:
        waits.length && waits.every((wait) => wait !== null)
          ? Math.max(...(waits as number[]))
          : null,
    };
  });
  /** One row per captured zone at the snapshot: the controls, then the check-in desks if simulated. */
  readonly zoneRows = computed(() => {
    const frame = this.frame();
    const rows = frame.zones.map(({ checkpoint, sample }) => ({
      id: checkpoint.id,
      code: checkpoint.code,
      name: checkpoint.name,
      queued: sample?.queuedPassengers ?? null,
      processed: sample?.processedPassengers ?? null,
      wait: sample?.kpi?.meanWaitMinutes ?? null,
      p95: sample?.kpi?.p95WaitMinutes ?? null,
      utilization: sample?.kpi?.utilization ?? null,
      cumulative: false,
    }));
    if (frame.desks) {
      const { area, sample } = frame.desks;
      rows.push({
        id: area.id,
        code: area.code,
        name: area.name,
        queued: sample?.queuedPassengers ?? null,
        processed: sample?.processedPassengers ?? null,
        wait: sample?.checkIn?.meanWaitMinutes ?? null,
        p95: sample?.checkIn?.p95WaitMinutes ?? null,
        // Desk utilization is reported since the start of the run, not per minute.
        utilization: sample?.checkIn?.utilization ?? null,
        cumulative: true,
      });
    }
    return rows;
  });
  readonly highlights = computed(() => {
    const frame = this.frame();
    return {
      screened: frame.complete
        ? frame.zones.reduce((total, zone) => total + zone.sample!.processedPassengers, 0)
        : null,
      busiest:
        this.zoneRows()
          .filter((row) => row.queued)
          .sort((a, b) => b.queued! - a.queued!)
          .at(0) ?? null,
    };
  });
  /** Wall-clock time of a replay minute in the scenario's time zone. */
  wallClock(minute: number) {
    const inputs = this.replay().data.captured.scenario.inputs;
    return clockTime(
      new Date(Date.parse(inputs.startsAtUtc) + minute * 60000).toISOString(),
      inputs.timeZone,
    );
  }
  eventLabel(type: string) {
    return type === 'incident.passenger-surge' ? 'Aflux de pasageri' : eventLabel(type);
  }
  eventZone(id: string | null | undefined) {
    if (!id) return 'Întregul terminal';
    const layout = this.replay().data.captured.layout.layout;
    const zone = [...layout.checkpoints, ...(layout.checkInAreas ?? [])].find(
      (zone) => zone.id === id,
    );
    return zone ? `${zone.code} · ${zone.name}` : id;
  }
  readonly renderFrame = computed(() => ({
    snapshotMinute: this.frame().anchor,
    packet: {
      replay: this.replay().passengers,
      timeMinutes: this.time(),
      snapshotMinute: this.frame().anchor,
      playing: this.playing(),
      speed: this.speed(),
      budget: 250,
      enabled: true,
      endMinute: this.duration(),
    },
  }));
  readonly scene = { aircraft: [] };
  private readonly clock = new PassengerClock();
  private readonly zone = inject(NgZone);
  private context = '';
  private request?: number;
  private published = -Infinity;
  constructor() {
    effect(() => {
      const replay = this.replay();
      untracked(() => {
        const key = replay.data.run.id + ':' + replay.envelope.contentHash;
        if (key !== this.context) {
          this.context = key;
          this.pause();
          this.clock.speed = 16;
          this.speed.set(16);
          this.clock.configure(0, replay.data.captured.scenario.inputs.durationMinutes);
          this.clock.seek(this.initialMinute());
          this.zoneId.set('');
          this.selectedEvent.set('');
          this.time.set(this.clock.time);
        }
      });
    });
    const hidden = () => {
      if (document.hidden) this.pause();
    };
    document.addEventListener('visibilitychange', hidden);
    inject(DestroyRef).onDestroy(() => {
      this.pause();
      document.removeEventListener('visibilitychange', hidden);
    });
  }
  seek(minute: number, zoneId?: string | null, eventId = '') {
    if (!Number.isFinite(minute)) return;
    this.pause();
    this.clock.seek(minute);
    this.time.set(this.clock.time);
    this.selectedEvent.set(eventId);
    if (zoneId !== undefined) this.zoneId.set(zoneId ?? '');
  }
  seekInput(event: Event) {
    this.seek((event.target as HTMLInputElement).valueAsNumber);
  }
  toggle() {
    if (this.playing()) {
      this.pause();
      return;
    }
    this.clock.play();
    this.playing.set(this.clock.playing);
    this.selectedEvent.set('');
    this.published = -Infinity;
    this.time.set(this.clock.time);
    this.zone.runOutsideAngular(() => {
      this.request = requestAnimationFrame(this.tick);
    });
  }
  setSpeed(value: PlaybackSpeed) {
    this.clock.speed = value;
    this.speed.set(value);
  }
  pause() {
    this.clock.pause();
    this.playing.set(false);
    if (this.request !== undefined) cancelAnimationFrame(this.request);
    this.request = undefined;
  }
  private readonly tick = (timestamp: number) => {
    this.request = undefined;
    this.clock.step(timestamp);
    if (timestamp - this.published >= 100 || !this.clock.playing) {
      this.published = timestamp;
      this.zone.run(() => {
        this.time.set(this.clock.time);
        this.playing.set(this.clock.playing);
      });
    }
    if (this.clock.playing) this.request = requestAnimationFrame(this.tick);
  };
}

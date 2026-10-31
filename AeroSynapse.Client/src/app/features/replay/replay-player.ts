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
import { PassengerClock } from '../digital-twin/passenger-model';
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
  readonly speed = signal<1 | 4 | 16>(1);
  readonly zoneId = signal('');
  readonly selectedEvent = signal('');
  readonly speeds = [1, 4, 16] as const;
  readonly observationPage = signal(0);
  readonly observationMinutes = computed(() =>
    this.replay().minutes.slice(this.observationPage() * 100, (this.observationPage() + 1) * 100),
  );
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
    [0, 0.25, 0.5, 0.75, 1].map(
      (fraction) => fraction * this.replay().data.captured.scenario.inputs.durationMinutes,
    ),
  );
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
  eventLabel(type: string) {
    return type === 'incident.passenger-surge' ? 'Aflux de pasageri' : type;
  }
  eventZone(id: string | null | undefined) {
    if (!id) return 'Întregul terminal';
    const layout = this.replay().data.captured.layout.layout;
    const zone = [...layout.checkpoints, ...(layout.checkInAreas ?? [])].find(
      (zone) => zone.id === id,
    );
    return zone ? `${zone.code} · ${zone.name}` : id;
  }
  readonly visibleZones = computed(() =>
    this.frame().zones.filter((row) => !this.zoneId() || row.checkpoint.id === this.zoneId()),
  );
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
      endMinute: this.replay().data.captured.scenario.inputs.durationMinutes,
      interpolateAggregates: false,
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
          this.clock.speed = 1;
          this.speed.set(1);
          this.clock.configure(0, replay.data.captured.scenario.inputs.durationMinutes);
          this.clock.seek(this.initialMinute());
          this.zoneId.set('');
          this.selectedEvent.set('');
          this.observationPage.set(0);
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
  setSpeed(value: 1 | 4 | 16) {
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

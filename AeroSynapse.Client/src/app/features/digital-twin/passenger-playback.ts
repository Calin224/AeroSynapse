import {
  Component,
  computed,
  DestroyRef,
  effect,
  inject,
  input,
  NgZone,
  output,
  signal,
  untracked,
} from '@angular/core';
import { CONGESTION_BANDS, HeatmapRun } from './heatmap-model';
import { congestionAt, congestionGradient, congestionTimeline, CONGESTION_SLIDER_STYLES } from './heatmap-timeline';
import {
  PassengerClock,
  PassengerPacket,
  PassengerReplay,
  PassengerStats,
  PlaybackSpeed,
  preparePassengerReplay,
  snapshotAtOrBefore,
} from './passenger-model';

@Component({
  selector: 'app-passenger-playback',
  template: `
    <section
      aria-label="Redare pasageri din simulare"
      [attr.data-run-id]="run().runId"
      [attr.data-playhead]="time()"
      [attr.data-passenger-mode]="mode()"
    >
      <div class="transport">
        <button type="button" [attr.aria-pressed]="enabled()" (click)="toggle()">
          Arată pasagerii
        </button>
        <button
          type="button"
          [disabled]="!canPlay()"
          (click)="togglePlayback()"
          [attr.aria-label]="
            playing() ? 'Oprește animația pasagerilor' : 'Redă animația pasagerilor'
          "
        >
          {{ playing() ? 'Pauză' : 'Redă' }}
        </button>
        <div role="group" aria-label="Viteza animației">
          @for (rate of rates; track rate) {
            <button type="button" [attr.aria-pressed]="speed() === rate" (click)="setSpeed(rate)">
              {{ rate }}x
            </button>
          }
        </div>
        <label class="seek-control"
          ><span class="sr-only">Derulare</span>
          <input
            type="range"
            class="congestion-slider"
            aria-label="Poziția animației în minute"
            [min]="start()"
            [max]="end()"
            step="0.01"
            [value]="time()"
            [disabled]="!canPlay()"
            [style.--congestion-track]="track()"
            [style.--congestion-thumb]="states[band()].color"
            [attr.data-congestion-band]="band()"
            [attr.aria-valuetext]="'Minutul ' + time().toFixed(1) + ' din simulare · starea cea mai gravă: ' + states[band()].label"
            (input)="seek($event)"
          />
        </label>
        <span class="clock"
          >Minutul {{ time().toFixed(1) }}
          @if (!snapshotAvailable()) {
            · fără date pentru acest minut
          }
        </span>
      </div>
      <details class="motion-details">
        <summary>Detalii tehnice despre pasagerii afișați</summary>
        <label
          >Buget vizual
          <select
            aria-label="Număr maxim de simboluri pasageri"
            [value]="budget()"
            (change)="setBudget($event)"
          >
            <option value="600">600</option>
            <option value="250">250</option>
            <option value="100">100</option>
          </select>
        </label>
        <p class="provenance">
          @if (mode() === 'individual') {
            Traiectorii individuale din simulator · interpolare pe traseele furnizate.
          } @else if (mode() === 'aggregate') {
            Grupuri agregate din snapshoturi. Pozițiile individuale lipsesc; simbolurile reprezintă
            grupuri în coadă, nu trasee reale.
          } @else {
            Nu există traiectorii sau snapshoturi de pasageri disponibile.
          }
          Tranziția vizuală nu modifică KPI-urile.
        </p>
        @if (presentation() === '3d') {
          @if (stats(); as counts) {
            <p class="sampling" role="status">
              {{ counts.rendered }}
              {{ counts.mode === 'individual' ? 'avataruri' : 'simboluri de grup' }} ·
              {{ counts.represented }}
              {{ counts.mode === 'individual' ? 'traiectorii disponibile' : 'în cozi la snapshot' }}
              · {{ counts.lod === 'markers' ? 'LOD redus' : 'LOD detaliat' }}.
              @if (counts.unmapped) {
                {{ counts.unmapped }} fără zonă mapată.
              }
              Reducerea bugetului schimbă doar reprezentarea.
            </p>
          }
        }
        @if (presentation() === '2d') {
          <p class="sampling">
            Harta 2D afișează valorile agregate; controlul de timp rămâne disponibil.
          </p>
        }
      </details>
    </section>
  `,
  styles: [CONGESTION_SLIDER_STYLES, `
    :host {
      display: block;
      grid-column: 1/-1;
      grid-row: 3;
    }
    section {
      border-top: 1px solid var(--border);
      padding-top: 10px;
    }
    .transport,
    .transport div,
    label {
      display: flex;
      align-items: center;
      gap: 5px;
    }
    .transport {
      gap: 12px;
    }
    .seek-control {
      flex: 1;
      min-width: 50px;
    }
    input {
      width: 100%;
      accent-color: var(--accent);
    }
    label,
    .clock,
    p,
    summary {
      font-size: 11.5px;
      color: var(--text-faint);
    }
    .clock {
      white-space: nowrap;
      font-variant-numeric: tabular-nums;
    }
    button,
    select {
      padding: 6px 10px;
      color: var(--text-muted);
      background: var(--surface-sunken);
      border: 1px solid var(--border);
      border-radius: var(--radius);
      cursor: pointer;
      font-size: 12px;
    }
    button[aria-pressed='true'] {
      background: var(--accent-soft);
      color: var(--accent);
      border-color: var(--accent-line);
    }
    button:disabled {
      opacity: 0.4;
      cursor: not-allowed;
    }
    button:focus-visible,
    select:focus-visible,
    input:focus-visible,
    summary:focus-visible {
      outline: 2px solid var(--accent);
      outline-offset: 2px;
    }
    p {
      margin: 8px 0 0;
      line-height: 1.6;
    }
    .motion-details {
      margin-top: 8px;
    }
    summary {
      cursor: pointer;
    }
    .motion-details label {
      margin-top: 10px;
    }
    .provenance {
      color: var(--text-muted);
    }
    .sr-only {
      position: absolute;
      width: 1px;
      height: 1px;
      overflow: hidden;
      clip-path: inset(50%);
    }
    @container twin (max-width: 650px) {
      .transport {
        flex-wrap: wrap;
        gap: 8px;
      }
      .seek-control {
        flex-basis: 40%;
      }
      .clock {
        flex-basis: 100%;
      }
    }
  `],
})
export class PassengerPlayback {
  readonly run = input.required<HeatmapRun>();
  readonly minute = input.required<number>();
  readonly timelineMinutes = input.required<number[]>();
  readonly presentation = input<'3d' | '2d'>('3d');
  readonly stats = input<PassengerStats | null>(null);
  readonly packetChange = output<PassengerPacket>();
  readonly minuteChange = output<number>();
  protected readonly enabled = signal(true);
  protected readonly time = signal(0);
  protected readonly timeline = computed(() => congestionTimeline(this.run(), this.timelineMinutes()));
  protected readonly track = computed(() => congestionGradient(this.timeline(), this.start(), this.end()));
  protected readonly band = computed(() => congestionAt(this.timeline(), this.time()));
  protected readonly states = CONGESTION_BANDS;
  protected readonly playing = signal(false);
  protected readonly speed = signal<PlaybackSpeed>(1);
  protected readonly budget = signal(600);
  protected readonly mode = signal<PassengerReplay['mode']>('unavailable');
  readonly canPlay = signal(false);
  protected readonly rates = [1, 4, 16, 60] as const;
  private readonly clock = new PassengerClock();
  private readonly zone = inject(NgZone);
  private replay?: PassengerReplay;
  private previousRun?: HeatmapRun;
  private context = '';
  private lastMinute?: number;
  private request?: number;
  private publishedAt = -Infinity;
  protected readonly start = signal(0);
  protected readonly end = signal(0);
  constructor() {
    effect(() => {
      const run = this.run(),
        minute = this.minute(),
        minutes = this.timelineMinutes();
      untracked(() => {
        if (run !== this.previousRun) {
          this.replay = preparePassengerReplay(run);
          this.previousRun = run;
        }
        const motionRange = this.replay!.tracks.reduce(
          (range, track) => [
            Math.min(range[0], track.keyframes[0].elapsedMinutes),
            Math.max(range[1], track.keyframes.at(-1)!.elapsedMinutes),
          ],
          [Infinity, -Infinity],
        );
        const start = Math.min(minutes[0] ?? Infinity, motionRange[0]);
        this.start.set(Number.isFinite(start) ? start : 0);
        const end = Math.max(minutes.at(-1) ?? -Infinity, motionRange[1]);
        this.end.set(Number.isFinite(end) ? end : this.start());
        const context = `${run.runId}:${run.layoutId}`;
        if (context !== this.context) {
          this.context = context;
          this.stop();
          // Finished runs cover an hour or more: start at 16x so flows and queues visibly move
          // within seconds. Only the live preview follows real time.
          this.clock.speed = run.source === 'live-preview' ? 1 : 16;
          this.speed.set(this.clock.speed);
          this.clock.configure(this.start(), this.end());
          this.clock.seek(minute);
        } else {
          this.clock.configure(this.start(), this.end());
          if (minute !== this.lastMinute) this.clock.seek(minute);
        }
        this.lastMinute = minute;
        this.mode.set(this.replay!.mode);
        this.canPlay.set(this.end() > this.start() && this.replay!.mode !== 'unavailable');
        if (this.replay!.mode === 'unavailable') this.stop();
        this.publish(false);
      });
    });
    const visibilityChanged = () => {
      if (document.hidden) {
        this.stop();
        this.publish(false);
      }
    };
    document.addEventListener('visibilitychange', visibilityChanged);
    inject(DestroyRef).onDestroy(() => {
      this.stop();
      document.removeEventListener('visibilitychange', visibilityChanged);
    });
  }
  protected toggle(): void {
    this.enabled.set(!this.enabled());
    this.publish(false);
  }
  playFrom(minute: number): void {
    this.stop();
    this.clock.seek(minute);
    this.togglePlayback();
  }
  togglePlayback(): void {
    if (this.clock.playing) this.stop();
    else {
      this.clock.play();
      this.zone.runOutsideAngular(() => {
        this.request = requestAnimationFrame(this.tick);
      });
    }
    this.publish(true);
  }
  protected setSpeed(speed: PlaybackSpeed): void {
    this.clock.speed = speed;
    this.speed.set(speed);
    this.publish(false);
  }
  protected setBudget(event: Event): void {
    this.budget.set(Number((event.target as HTMLSelectElement).value));
    this.publish(false);
  }
  protected seek(event: Event): void {
    this.clock.seek(Number((event.target as HTMLInputElement).value));
    this.publish(true);
  }
  protected snapshotAvailable(): boolean {
    return this.replay?.snapshots.has(this.minute()) ?? false;
  }
  private readonly tick = (timestamp: number): void => {
    this.request = undefined;
    this.clock.step(timestamp);
    if (timestamp - this.publishedAt >= 100 || !this.clock.playing) {
      this.publishedAt = timestamp;
      this.zone.run(() => this.publish(true));
    }
    if (this.clock.playing) this.request = requestAnimationFrame(this.tick);
  };
  private stop(): void {
    this.clock.pause();
    if (this.request !== undefined) cancelAnimationFrame(this.request);
    this.request = undefined;
  }
  private publish(updateMinute: boolean): void {
    if (!this.replay) return;
    this.time.set(this.clock.time);
    this.playing.set(this.clock.playing);
    const anchor = updateMinute
      ? (snapshotAtOrBefore(this.timelineMinutes(), this.clock.time) ??
        Math.floor(this.clock.time * 100) / 100)
      : this.minute();
    if (anchor !== undefined && anchor !== this.lastMinute) {
      this.lastMinute = anchor;
      this.minuteChange.emit(anchor);
    }
    this.packetChange.emit({
      replay: this.replay,
      timeMinutes: this.clock.time,
      snapshotMinute: anchor ?? 0,
      playing: this.clock.playing,
      speed: this.clock.speed,
      budget: this.budget(),
      enabled: this.enabled(),
      endMinute: this.end(),
    });
  }
}

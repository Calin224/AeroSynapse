import {
  afterNextRender,
  Component,
  DestroyRef,
  effect,
  ElementRef,
  inject,
  InjectionToken,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { BaggageFrame, BaggageResult, heatColor, RESOURCE_LABELS } from './baggage.models';
import { BaggageRenderer } from './baggage-renderer';

export interface BaggageViewport {
  set(result: BaggageResult, frame: BaggageFrame): void;
  dispose(): void;
}
export const BAGGAGE_RENDERER = new InjectionToken<
  (host: HTMLElement, onError: () => void) => BaggageViewport
>('BAGGAGE_RENDERER', {
  providedIn: 'root',
  factory: () => (host, onError) => new BaggageRenderer(host, onError),
});

@Component({
  selector: 'app-baggage-view',
  template: `
    <div class="view-heading">
      <strong>Flux de bagaje · schemă sintetică</strong
      ><button type="button" (click)="show2d.set(!show2d())" [disabled]="failed()">
        {{ show2d() ? 'Deschide 3D' : 'Schemă 2D' }}
      </button>
    </div>
    @if (failed()) {
      <p role="status">
        Vizualizarea 3D nu este disponibilă. Heatmap-ul și KPI-urile rămân disponibile mai jos.
      </p>
    }
    <div #host class="viewport" [hidden]="show2d() || failed()"></div>
    @if (show2d() || failed()) {
      <svg
        viewBox="0 0 760 240"
        role="img"
        aria-label="Check-in, benzi paralele A și B, sortare, încărcare"
      >
        <path
          d="M100 120 280 55 460 120 650 120 M100 120 280 185 460 120"
          fill="none"
          stroke="#97aa99"
          stroke-width="5"
        />
        @for (row of frame().resources; track row.resourceId; let i = $index) {
          <g [attr.transform]="transform(i)">
            <rect x="-62" y="-28" width="124" height="56" rx="10" [attr.fill]="color(row)" />
            <text text-anchor="middle" y="-4" fill="white">{{ labels[row.resourceId] }}</text>
            <text text-anchor="middle" y="16" fill="white">
              {{ row.queued }} în coadă{{ row.stopped ? ' · STOP' : '' }}
            </text>
          </g>
        }
      </svg>
    }
    <p class="caption">
      3D: maximum 300 de bagaje vizibile. Heatmap: verde &lt; 1 bagaj în coadă/slot · ocru ≥ 1 ·
      roșu ≥ 3 · mov = bandă oprită. Toate bagajele sunt incluse în KPI.
    </p>
  `,
  styles: `
    :host {
      display: block;
    }
    .view-heading {
      display: flex;
      justify-content: space-between;
      gap: 12px;
      align-items: center;
      padding: 14px 16px;
    }
    .viewport {
      height: 340px;
      width: 100%;
    }
    .viewport[hidden] {
      display: none;
    }
    svg {
      display: block;
      width: 100%;
      min-height: 200px;
      background: #eef2ec;
    }
    text {
      font: 13px sans-serif;
    }
    .caption,
    p[role='status'] {
      padding: 10px 16px;
      color: var(--text-muted);
      font-size: 12px;
    }
    button {
      padding: 6px 10px;
      border: 1px solid var(--border-strong);
      border-radius: var(--radius);
      background: var(--panel);
      color: var(--text);
      cursor: pointer;
    }
    @media (max-width: 600px) {
      .viewport {
        height: 260px;
      }
    }
  `,
})
export class BaggageView {
  readonly result = input.required<BaggageResult>();
  readonly frame = input.required<BaggageFrame>();
  readonly show2d = signal(false);
  readonly failed = signal(false);
  readonly labels = RESOURCE_LABELS;
  readonly color = heatColor;
  private readonly host = viewChild.required<ElementRef<HTMLElement>>('host');
  private readonly factory = inject(BAGGAGE_RENDERER);
  private viewport?: BaggageViewport;
  transform(index: number): string {
    return [
      'translate(100 120)',
      'translate(280 55)',
      'translate(280 185)',
      'translate(460 120)',
      'translate(650 120)',
    ][index];
  }
  constructor() {
    afterNextRender(() => {
      try {
        this.viewport = this.factory(this.host().nativeElement, () => this.failed.set(true));
        this.viewport.set(this.result(), this.frame());
      } catch {
        this.failed.set(true);
      }
    });
    effect(() => {
      const result = this.result(),
        frame = this.frame();
      try {
        this.viewport?.set(result, frame);
      } catch {
        this.failed.set(true);
      }
    });
    inject(DestroyRef).onDestroy(() => this.viewport?.dispose());
  }
}

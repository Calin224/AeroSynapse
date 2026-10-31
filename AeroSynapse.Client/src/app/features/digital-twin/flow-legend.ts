import { Component, input } from '@angular/core';

/** Playback legend over the 3D scene: the clock and what standing and walking figures mean. */
@Component({
  selector: 'app-flow-legend',
  template: `
    <strong>Minutul {{ minute() }}</strong>
    <span class="queued">{{ queued() }} în coadă</span>
    @if (walking()) {
      <span class="walking">în mișcare · flux din simulare</span>
    }
  `,
  styles: `
    :host {
      position: absolute;
      top: 12px;
      left: 12px;
      display: grid;
      gap: 2px;
      padding: 8px 12px;
      border: 1px solid var(--border);
      border-radius: var(--radius);
      background: var(--panel);
      box-shadow: none;
      color: var(--text-muted);
      font-size: 12px;
      pointer-events: none;
      backdrop-filter: blur(8px);
    }
    strong {
      font: 600 20px var(--font-mono);
      font-variant-numeric: tabular-nums;
      color: var(--text);
    }
    span::before {
      content: '● ';
      color: #e0a12f;
    }
    .walking::before {
      color: #2aa9c4;
    }
  `,
  host: { 'aria-hidden': 'true', 'data-testid': 'flow-legend' },
})
export class FlowLegend {
  readonly minute = input.required<number>();
  readonly queued = input.required<number>();
  readonly walking = input(false);
}

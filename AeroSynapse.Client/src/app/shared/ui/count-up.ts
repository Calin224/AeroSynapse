import { Component, effect, input, signal } from '@angular/core';
import { prefersReducedMotion } from './motion';

/** Counts a numeric value up from zero; non-numeric values render as-is. Keeps zero-padding ("04"). */
@Component({
  selector: 'app-count-up',
  template: `{{ display() }}`,
})
export class CountUp {
  readonly value = input.required<string | number>();
  readonly duration = input(1100);
  readonly delay = input(0);
  protected readonly display = signal('');

  constructor() {
    effect((onCleanup) => {
      const raw = String(this.value());
      const match = /^(\d+)(?:\.(\d+))?$/.exec(raw.trim());
      if (!match || prefersReducedMotion()) {
        this.display.set(raw);
        return;
      }
      const target = Number(raw);
      const width = match[1].length;
      const decimals = match[2]?.length ?? 0;
      const format = (n: number) => {
        const [whole, fraction] = n.toFixed(decimals).split('.');
        return whole.padStart(width, '0') + (fraction ? `.${fraction}` : '');
      };
      this.display.set(format(0));
      let frame = 0;
      const start = performance.now() + this.delay();
      const step = (now: number) => {
        const t = Math.min(1, Math.max(0, (now - start) / this.duration()));
        this.display.set(format(target * (1 - Math.pow(1 - t, 3))));
        if (t < 1) frame = requestAnimationFrame(step);
        else this.display.set(raw);
      };
      frame = requestAnimationFrame(step);
      onCleanup(() => cancelAnimationFrame(frame));
    });
  }
}

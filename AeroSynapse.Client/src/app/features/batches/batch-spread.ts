import { Component, ElementRef, computed, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import {
  BatchMetric,
  SpreadLane,
  SpreadPoint,
  formatValue,
  howOften,
  pastThreshold,
} from './batch-insight';

/** Dot colours per variant position; the starting scenario matches the "before" colour elsewhere. */
const LANE_COLORS = ['#c8643f', '#258a72', '#3f6fb5', '#8a5cb8', '#93701c'];
/** Dots closer than this share of the axis stack vertically instead of overlapping. */
const DOT_GAP = 3.5;
const DOT_STEP = 15;
let nextId = 0;

function niceStep(raw: number) {
  const power = 10 ** Math.floor(Math.log10(raw));
  const fraction = raw / power;
  return (fraction < 1.5 ? 1 : fraction < 3 ? 2 : fraction < 7 ? 5 : 10) * power;
}

interface PlacedDot extends SpreadPoint {
  x: number;
  y: number;
  label: string;
  past: boolean;
}

/** Every simulated day as a dot on a shared axis, one lane per variant, with a "how often" threshold. */
@Component({
  selector: 'app-batch-spread',
  imports: [RouterLink],
  templateUrl: './batch-spread.html',
  styleUrl: './batch-spread.css',
})
export class BatchSpread {
  readonly lanes = input.required<SpreadLane[]>();
  readonly metric = input.required<BatchMetric>();
  readonly unit = input.required<string>();
  private readonly host: HTMLElement = inject(ElementRef).nativeElement;
  readonly id = `spread-${nextId++}`;
  readonly active = signal<string | null>(null);

  readonly domain = computed(() => {
    const values = this.lanes().flatMap((lane) => [
      ...lane.points.map((point) => point.value),
      ...[lane.stats?.p05, lane.stats?.p95].filter((value): value is number => value != null),
    ]);
    if (!values.length) return null;
    let lo = Math.min(...values),
      hi = Math.max(...values);
    const pad = hi > lo ? (hi - lo) * 0.08 : Math.max(1, Math.abs(lo) * 0.1);
    lo = lo >= 0 ? Math.max(0, lo - pad) : lo - pad;
    hi += pad;
    const tick = niceStep((hi - lo) / 4);
    const ticks: number[] = [];
    for (let value = Math.ceil(lo / tick) * tick; value <= hi; value += tick) ticks.push(value);
    return { lo, hi, ticks, step: niceStep((hi - lo) / 100) };
  });
  /** A threshold the viewer picked, for the metric it was picked on. */
  private readonly chosen = signal<{ name: string; value: number } | null>(null);
  /** The viewer's choice while it fits the axis, otherwise the starting scenario's typical value. */
  readonly threshold = computed(() => {
    const domain = this.domain() ?? { lo: 0, hi: 1, step: 1 };
    const chosen = this.chosen();
    if (
      chosen?.name === this.metric().name &&
      chosen.value >= domain.lo &&
      chosen.value <= domain.hi
    )
      return chosen.value;
    const median = this.lanes()[0]?.stats?.median ?? (domain.lo + domain.hi) / 2;
    return Math.round(median / domain.step) * domain.step;
  });

  readonly rows = computed(() => {
    const domain = this.domain();
    const scale = (value: number) =>
      domain ? ((value - domain.lo) / (domain.hi - domain.lo || 1)) * 100 : 50;
    const { lowerIsBetter } = this.metric();
    return this.lanes().map((lane) => {
      const levels: number[][] = [];
      const dots: PlacedDot[] = [...lane.points]
        .sort((a, b) => a.value - b.value || a.day - b.day)
        .map((point) => {
          const x = scale(point.value);
          let level = levels.findIndex((row) =>
            row.every((other) => Math.abs(other - x) >= DOT_GAP),
          );
          if (level < 0) level = levels.push([]) - 1;
          levels[level].push(x);
          return {
            ...point,
            x,
            y: (level % 2 ? -1 : 1) * Math.ceil(level / 2) * DOT_STEP,
            label: formatValue(this.unit(), point.value),
            past: pastThreshold([point], this.threshold(), lowerIsBetter) > 0,
          };
        });
      const stats = lane.stats;
      return {
        ...lane,
        color: LANE_COLORS[lane.position % LANE_COLORS.length],
        tag: lane.position === 0 ? 'Scenariul de pornire' : `Varianta ${lane.position}`,
        dots: dots.sort((a, b) => a.day - b.day),
        height: 44 + Math.ceil((levels.length - 1) / 2) * DOT_STEP * 2,
        typical: formatValue(this.unit(), stats?.median),
        band:
          stats?.p05 != null && stats.p95 != null
            ? { left: scale(stats.p05), width: Math.max(scale(stats.p95) - scale(stats.p05), 0.6) }
            : null,
        median: stats?.median != null ? scale(stats.median) : null,
        often: howOften(
          pastThreshold(lane.points, this.threshold(), lowerIsBetter),
          lane.points.length,
        ),
      };
    });
  });
  readonly ticks = computed(() => {
    const domain = this.domain();
    return (domain?.ticks ?? []).map((value) => ({
      value,
      x: ((value - domain!.lo) / (domain!.hi - domain!.lo || 1)) * 100,
      label: formatValue(this.unit(), value),
    }));
  });
  readonly thresholdX = computed(() => {
    const domain = this.domain();
    return domain ? ((this.threshold() - domain.lo) / (domain.hi - domain.lo || 1)) * 100 : 50;
  });
  readonly thresholdLabel = computed(() => formatValue(this.unit(), this.threshold()));
  /** The roving tab stop: the active dot, or the first one. */
  readonly focusId = computed(() => {
    const ids = this.rows().flatMap((row) => row.dots.map((dot) => dot.id));
    const active = this.active();
    return active && ids.includes(active) ? active : (ids[0] ?? null);
  });
  readonly detail = computed(() => {
    const id = this.active();
    for (const row of this.rows()) {
      const dot = row.dots.find((value) => value.id === id);
      if (!dot) continue;
      const better = (a: number, b: number) => (this.metric().lowerIsBetter ? a - b : b - a);
      const rank = [...row.dots].sort((a, b) => better(a.value, b.value)).indexOf(dot) + 1;
      const total = row.dots.length;
      return {
        dot,
        lane: row.tag,
        rank:
          total < 2
            ? 'Singura zi simulată reușită până acum.'
            : rank === 1
              ? `Cea mai bună zi din ${total}.`
              : rank === total
                ? `Cea mai grea zi din ${total}.`
                : `Locul ${rank} din ${total}, de la cea mai bună la cea mai grea zi.`,
      };
    }
    return null;
  });

  describe(dot: PlacedDot, tag: string) {
    return `${tag}, ziua ${dot.day}: ${dot.label}${dot.past ? ', dincolo de prag' : ''}`;
  }
  setThreshold(event: Event) {
    const value = Number((event.target as HTMLInputElement).value);
    this.chosen.set({ name: this.metric().name, value });
  }
  /** Arrow keys walk a lane by value; up and down jump to the nearest dot in the next lane. */
  move(event: KeyboardEvent) {
    const rows = this.rows().filter((row) => row.dots.length);
    const current = this.focusId();
    const laneIndex = rows.findIndex((row) => row.dots.some((dot) => dot.id === current));
    if (laneIndex < 0) return;
    const ordered = (index: number) => [...rows[index].dots].sort((a, b) => a.x - b.x);
    const lane = ordered(laneIndex);
    const position = lane.findIndex((dot) => dot.id === current);
    let target: PlacedDot | undefined;
    if (event.key === 'ArrowRight') target = lane[position + 1];
    else if (event.key === 'ArrowLeft') target = lane[position - 1];
    else if (event.key === 'Home') target = lane[0];
    else if (event.key === 'End') target = lane.at(-1);
    else if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      const next = rows[laneIndex + (event.key === 'ArrowDown' ? 1 : -1)];
      const x = lane[position].x;
      target = next
        ? [...next.dots].sort((a, b) => Math.abs(a.x - x) - Math.abs(b.x - x))[0]
        : undefined;
    } else return;
    event.preventDefault();
    if (!target) return;
    this.active.set(target.id);
    this.host.querySelector<HTMLElement>(`[data-point="${target.id}"]`)?.focus();
  }
}

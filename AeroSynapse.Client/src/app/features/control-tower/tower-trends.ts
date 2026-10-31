import { DecimalPipe } from '@angular/common';
import { Component, computed, input, linkedSignal } from '@angular/core';
import { TowerPoint } from './tower.models';

@Component({
  selector: 'app-tower-trends',
  imports: [DecimalPipe],
  templateUrl: './tower-trends.html',
  styleUrl: './tower-trends.css',
})
export class TowerTrends {
  readonly points = input.required<TowerPoint[]>();
  readonly source = input('Snapshot-uri persistate');
  readonly contextKey = input('');
  readonly selectedMinute = linkedSignal({
    source: this.contextKey,
    computation: () => null as number | null,
  });
  readonly metrics = [
    { key: 'queued', label: 'Pasageri în coadă', icon: 'users' },
    { key: 'processed', label: 'Procesați cumulat', icon: 'check-circle' },
  ] as const;
  readonly last = computed(() => this.points().at(-1));
  readonly selectedIndex = computed(() => {
    const index = this.points().findIndex((point) => point.minute === this.selectedMinute());
    return index < 0 ? Math.max(0, this.points().length - 1) : index;
  });
  readonly selected = computed(() => this.points()[this.selectedIndex()]);
  readonly plots = computed(() => {
    const rows = this.points();
    const start = rows[0]?.minute ?? 0;
    const span = (rows.at(-1)?.minute ?? 0) - start || 1;
    return Object.fromEntries(
      this.metrics.map(({ key }) => {
        const max = Math.max(1, ...rows.map((row) => row[key]));
        const coordinates = rows.map((row) => ({
          x: 8 + ((row.minute - start) / span) * 484,
          y: 82 - (row[key] / max) * 68,
        }));
        const marker = coordinates[this.selectedIndex()] ?? { x: 8, y: 82 };
        return [
          key,
          {
            path: coordinates.map(({ x, y }) => `${x},${y}`).join(' '),
            area: `${coordinates[0]?.x ?? 8},82 ${coordinates.map(({ x, y }) => `${x},${y}`).join(' ')} ${coordinates.at(-1)?.x ?? 8},82`,
            marker,
          },
        ];
      }),
    ) as Record<
      'queued' | 'processed',
      { path: string; area: string; marker: { x: number; y: number } }
    >;
  });

  scrub(event: Event) {
    const point = this.points()[Number((event.target as HTMLInputElement).value)];
    if (point) this.selectedMinute.set(point.minute);
  }

  inspect(event: PointerEvent) {
    const svg = event.currentTarget as SVGSVGElement;
    const bounds = svg.getBoundingClientRect();
    const rows = this.points();
    if (!rows.length || bounds.width <= 0) return;
    const ratio = Math.max(
      0,
      Math.min(1, (((event.clientX - bounds.left) / bounds.width) * 500 - 8) / 484),
    );
    const minute = rows[0].minute + ratio * (rows.at(-1)!.minute - rows[0].minute);
    const nearest = rows.reduce((best, row) =>
      Math.abs(row.minute - minute) < Math.abs(best.minute - minute) ? row : best,
    );
    this.selectedMinute.set(nearest.minute);
  }
}

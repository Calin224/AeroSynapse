import { Component, computed, input, output, signal } from '@angular/core';
import { TERMINAL_MAP_ZONES, TerminalMapZone } from './terminal-zones';
import { TwinScene } from './twin-model';
import { CONGESTION_BANDS, HeatmapFrame, TwinHover } from './heatmap-model';
import { displayNavigationRoutes, routePoints2d } from './navigation-path';

/** SVG remains usable without WebGL, a GLB download or a canvas graphics context. */
@Component({
  selector: 'app-terminal-map',
  template: `
    <svg
      [attr.viewBox]="viewBox()"
      aria-label="Hartă 2D a terminalului demonstrativ"
      tabindex="0"
      role="group"
      (keydown)="keyPan($event)"
      (pointerdown)="startPan($event)"
      (pointermove)="movePan($event)"
      (pointerup)="endPan()"
      (pointercancel)="endPan()"
      (wheel)="wheel($event)"
    >
      <path
        d="M -87.5 57 L -87.5 24 L -17.5 24 L -17.5 5 L 24 5 L 24 75 L -17.5 75 L -17.5 57 Z"
        fill="#f4f6f1"
        stroke="#ccd3c7"
        stroke-width=".4"
      />
      @for (zone of zones; track zone.entityId) {
        <g
          role="button"
          tabindex="0"
          [attr.aria-label]="zoneLabel(zone)"
          [attr.aria-pressed]="selectedEntityId() === zone.entityId"
          [attr.data-zone-id]="zone.zoneId"
          [class.selected]="selectedEntityId() === zone.entityId"
          [class.closed]="closed(zone.zoneId)"
          (click)="select(zone)"
          (keydown.enter)="select(zone, true)"
          (keydown.space)="$event.preventDefault(); select(zone, true)"
          (pointerenter)="
            zoneHovered.emit({ selection: zone, clientX: $event.clientX, clientY: $event.clientY })
          "
          (pointerleave)="zoneHovered.emit(null)"
        >
          <rect
            [attr.x]="zone.bounds[0]"
            [attr.y]="zone.bounds[1]"
            [attr.width]="zone.bounds[2]"
            [attr.height]="zone.bounds[3]"
            rx="1"
            [attr.fill]="color(zone)"
          />
          <text
            [attr.x]="zone.bounds[0] + zone.bounds[2] / 2"
            [attr.y]="zone.bounds[1] + zone.bounds[3] / 2"
            text-anchor="middle"
            dominant-baseline="middle"
          >
            {{ zone.code }}
          </text>
          @if (staff(zone); as people) {
            <text
              class="staff"
              [attr.x]="zone.bounds[0] + zone.bounds[2] / 2"
              [attr.y]="zone.bounds[1] + zone.bounds[3] / 2 + 2.8"
              text-anchor="middle"
              dominant-baseline="middle"
            >
              {{ people }}
            </text>
          }
        </g>
      }
      <text x="-82" y="12" class="map-note">G-1…G-5 · îmbarcare</text>
      @for (route of navigationRoutes(); track route.routeKey) {
        <polyline
          [attr.points]="points(route)"
          fill="none"
          stroke="#1f8f63"
          stroke-width=".7"
          pointer-events="none"
        >
          <title>Traseu {{ route.routeKey }} · {{ route.passengerCount }} pasageri</title>
        </polyline>
      }
    </svg>
  `,
  styles: `
    :host {
      display: block;
      position: absolute;
      inset: 0;
      background: var(--scene, #e4e9e2);
    }
    svg {
      width: 100%;
      height: 100%;
      touch-action: none;
    }
    g {
      cursor: pointer;
      outline: none;
    }
    rect {
      stroke: #8f9b8d;
      stroke-width: 0.35;
    }
    g:hover rect,
    g:focus-visible rect {
      stroke: #24352c;
      stroke-width: 0.8;
    }
    g.selected rect {
      stroke: var(--accent);
      stroke-width: 1;
    }
    g.closed rect {
      stroke-dasharray: 1 0.7;
    }
    text {
      fill: #24352c;
      font: 2.4px sans-serif;
      pointer-events: none;
    }
    .staff {
      font-size: 1.8px;
    }
    .map-note {
      fill: #5e6b5e;
      font-size: 2px;
    }
  `,
})
export class TerminalMap {
  readonly scene = input.required<TwinScene>();
  readonly navigationRoutes = computed(() =>
    displayNavigationRoutes(this.scene().navigationRoutes ?? []),
  );
  readonly points = routePoints2d;
  readonly selectedEntityId = input<string>('');
  readonly zoneSelected = output<TerminalMapZone>();
  readonly zoneHovered = output<TwinHover | null>();
  readonly heatmap = input<HeatmapFrame | null>(null);
  protected zoneLabel(zone: TerminalMapZone): string {
    const frame = this.heatmap();
    const label = zone.label.includes(zone.code) ? zone.label : `${zone.code} · ${zone.label}`;
    if (!frame) return label;
    const value = frame.zones.find((row) => row.zoneId === zone.zoneId);
    return value
      ? `${zone.code} · ${value.label}: ${value.queuedPassengers} pasageri în coadă; ${value.staffCount ?? '—'} operatori; ${value.capacityPerMinute ?? 'indisponibil'} pasageri/min; ${CONGESTION_BANDS[value.band].label}; minutul ${frame.minute}`
      : `${label}: fără snapshot la minutul ${frame.minute}`;
  }
  /** Operators on duty at the shown minute, so staffing changes during a run are visible on the map. */
  protected staff(zone: TerminalMapZone): string {
    const count = this.heatmap()?.zones.find((row) => row.zoneId === zone.zoneId)?.staffCount;
    return count == null ? '' : count === 1 ? '1 operator' : `${count} operatori`;
  }
  protected color(zone: TerminalMapZone): string {
    const data = this.heatmap()?.zones.find((snapshot) => snapshot.zoneId === zone.zoneId);
    return data
      ? CONGESTION_BANDS[data.band].color
      : this.heatmap()?.missingZones.some((snapshot) => snapshot.zoneId === zone.zoneId)
        ? CONGESTION_BANDS.unavailable.color
        : zone.zoneType === 'checkpoint'
          ? '#efe2bf'
          : zone.zoneType === 'checkIn'
            ? '#cfe6e4'
            : zone.zoneType === 'gate'
              ? '#e3e7e0'
              : '#dde3da';
  }
  protected readonly zones = TERMINAL_MAP_ZONES;
  private readonly camera = signal({ x: -99, y: -14, width: 136, height: 104 });
  protected readonly viewBox = computed(() => {
    const c = this.camera();
    return `${c.x} ${c.y} ${c.width} ${c.height}`;
  });
  private pointer?: { id: number; x: number; y: number };
  private dragged = false;
  private gestureOrigin?: [number, number];
  protected closed(zoneId: string): boolean {
    if (this.heatmap())
      return this.heatmap()!.zones.find((zone) => zone.zoneId === zoneId)?.capacityPerMinute === 0;
    return this.scene().checkpoints?.find((c) => c.zoneId === zoneId)?.status === 'closed';
  }
  protected select(zone: TerminalMapZone, keyboard = false): void {
    if (!this.dragged || keyboard) this.zoneSelected.emit(zone);
  }
  protected startPan(event: PointerEvent): void {
    if (this.pointer || event.button !== 0) {
      this.dragged = true;
      return;
    }
    this.dragged = false;
    this.pointer = { id: event.pointerId, x: event.clientX, y: event.clientY };
    this.gestureOrigin = [event.clientX, event.clientY];
  }
  protected movePan(event: PointerEvent): void {
    const p = this.pointer;
    if (!p || p.id !== event.pointerId) return;
    const [x, y] = this.gestureOrigin!;
    if (Math.hypot(event.clientX - x, event.clientY - y) <= 5 && !this.dragged) return;
    this.dragged = true;
    const svg = event.currentTarget as SVGSVGElement;
    svg.setPointerCapture(event.pointerId);
    const rect = svg.getBoundingClientRect();
    const c = this.camera();
    const scale = Math.max(c.width / rect.width, c.height / rect.height);
    this.camera.set({
      ...c,
      x: c.x - (event.clientX - p.x) * scale,
      y: c.y - (event.clientY - p.y) * scale,
    });
    this.pointer = { id: p.id, x: event.clientX, y: event.clientY };
  }
  protected endPan(): void {
    this.pointer = undefined;
  }
  protected wheel(event: WheelEvent): void {
    event.preventDefault();
    this.zoom(event.deltaY < 0 ? 'in' : 'out');
  }
  reset(): void {
    this.camera.set({ x: -99, y: -14, width: 136, height: 104 });
  }
  protected keyPan(event: KeyboardEvent): void {
    const keys: Record<string, 'left' | 'right' | 'up' | 'down'> = {
      ArrowLeft: 'left',
      ArrowRight: 'right',
      ArrowUp: 'up',
      ArrowDown: 'down',
    };
    const direction = keys[event.key];
    if (direction) {
      event.preventDefault();
      this.pan(direction);
    }
  }
  pan(direction: 'left' | 'right' | 'up' | 'down'): void {
    this.camera.update((c) => ({
      ...c,
      x: c.x + (direction === 'left' ? -1 : direction === 'right' ? 1 : 0) * c.width * 0.15,
      y: c.y + (direction === 'up' ? -1 : direction === 'down' ? 1 : 0) * c.height * 0.15,
    }));
  }
  zoom(direction: 'in' | 'out'): void {
    const c = this.camera();
    const factor = direction === 'in' ? 0.8 : 1.25;
    const width = Math.min(300, Math.max(30, c.width * factor));
    const height = (width * 104) / 136;
    this.camera.set({
      x: c.x + (c.width - width) / 2,
      y: c.y + (c.height - height) / 2,
      width,
      height,
    });
  }
}

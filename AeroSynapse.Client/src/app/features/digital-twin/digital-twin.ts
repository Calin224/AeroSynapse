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
import { ButtonModule } from 'primeng/button';
import { DataState } from '../../shared/ui/data-state';
import { ThreeTwinRenderer } from './three-twin-renderer';
import {
  TwinDisplayMode,
  TwinRenderer,
  TwinResource,
  TwinResources,
  TwinScene,
  TwinSurface,
  TwinSelection,
  TWIN_SURFACES,
} from './twin-model';

export const TWIN_RENDERER_FACTORY = new InjectionToken<
  (host: HTMLElement, onError: () => void) => TwinRenderer
>('TWIN_RENDERER_FACTORY', {
  providedIn: 'root',
  factory: () => (host, onError) => new ThreeTwinRenderer(host, onError),
});
@Component({
  selector: 'app-digital-twin',
  imports: [ButtonModule, DataState],
  template: ` <div class="viewport" [attr.aria-busy]="status() === 'loading' || assetLoading()">
    <div #host class="canvas-host" [class.hidden]="status() === 'error'"></div>
    @if (status() === 'loading') {
      <div class="overlay"><app-data-state status="loading" label="terminalul 3D" /></div>
    }
    @if (status() === 'error') {
      <div class="overlay">
        <app-data-state
          status="error"
          message="Scena 3D nu este disponibilă. Verifică suportul WebGL și accelerarea hardware a browserului."
          (retry)="initialize()"
        />
      </div>
    }
    @if (status() === 'ready') {
      <div class="scene-label">AEROPORTUL TĂU · DEMO SYNTHETIC</div>
      <div class="camera-action">
        <p-button
          label="Resetează camera"
          icon="pi pi-compass"
          [outlined]="true"
          size="small"
          (onClick)="resetCamera()"
        />
      </div>
      <div class="scene-tools">
        <div class="mode-controls" aria-label="Mod de afișare">
          <button
            type="button"
            [attr.aria-pressed]="mode() === 'interior'"
            (click)="setMode('interior')"
          >
            Interior
          </button>
          <button
            type="button"
            [attr.aria-pressed]="mode() === 'exterior'"
            (click)="setMode('exterior')"
          >
            Exterior
          </button>
          @if (mode() === 'exterior') {
            <label
              ><input
                type="checkbox"
                [checked]="resources().aircraft"
                (change)="toggleResource('aircraft', $event)"
              />
              Aeronave</label
            >
            <label
              ><input
                type="checkbox"
                [checked]="resources().baggage"
                (change)="toggleResource('baggage', $event)"
              />
              Bagaje</label
            >
            <label
              ><input
                type="checkbox"
                [checked]="resources().vehicles"
                (change)="toggleResource('vehicles', $event)"
              />
              Vehicule</label
            >
            <label
              ><input
                type="checkbox"
                [checked]="resources().runway"
                (change)="toggleResource('runway', $event)"
              />
              Pistă</label
            >
          }
        </div>
        <div class="zone-control">
          <label for="{{ zoneSelectId }}">Selectează zonă</label>
          <select
            [id]="zoneSelectId"
            [value]="selection()?.entityId ?? ''"
            (change)="selectZone($event)"
          >
            <option value="">Click pe model sau alege o zonă</option>
            @for (zone of zones(); track zone.entityId) {
              <option [value]="zone.entityId">{{ zone.label }}</option>
            }
          </select>
        </div>
      </div>
      @if (selection(); as selected) {
        <div class="selection" aria-live="polite">
          <strong>{{ selected.label }}</strong> <span>synthetic · {{ selected.zoneType }}</span
          ><code>zoneId: {{ selected.zoneId }}</code> <code>entityId: {{ selected.entityId }}</code>
        </div>
      }
      @if (assetLoading()) {
        <div class="asset-status" role="status">Se încarcă resursele exterioare…</div>
      }
      @if (assetError()) {
        <div class="asset-status" role="status">{{ assetError() }}</div>
      }
      <div class="legend">
        3 controale · 5 porți · Geometrie și mișcări demonstrative. Nu reproduce Craiova.
      </div>
    }
  </div>
  @if (status() === 'ready') {
    <section class="material-editor" aria-label="Editor de materiale">
      <div class="material-heading">
        <div>
          <strong>Materiale terminal</strong>
          <span>Alege o suprafață și aplic-o pe zona selectată.</span>
        </div>
        @if (selection(); as selected) {
          <span class="zone-chip">{{ selected.label }}</span>
        } @else {
          <span class="zone-hint">Selectează mai întâi o zonă din model</span>
        }
      </div>
      <div class="surface-grid" role="group" aria-label="Alege materialul">
        @for (surface of surfaceOptions; track surface.id) {
          <button
            type="button"
            class="surface-option"
            [class.selected]="selectedSurface() === surface.id"
            [attr.aria-pressed]="selectedSurface() === surface.id"
            (click)="chooseSurface(surface.id)"
          >
            <img [src]="surface.texture" [alt]="surface.label" />
            <span>{{ surface.label }}</span>
          </button>
        }
      </div>
      <div class="material-actions">
        <div class="material-status" aria-live="polite">
          @if (surfaceLoading()) {
            Se încarcă materialul…
          } @else if (surfaceError()) {
            <span class="material-error" role="alert">{{ surfaceError() }}</span>
          } @else if (appliedSurface()) {
            Material {{ appliedSurface() }} aplicat pe {{ selection()?.label }}.
          } @else {
            Materialul se aplică doar modelului local.
          }
        </div>
        <div class="material-buttons">
          <p-button
            label="Aplică material"
            icon="pi pi-palette"
            size="small"
            [disabled]="!selection() || mode() !== 'interior' || surfaceLoading()"
            (onClick)="applySurface()"
          />
          <p-button
            label="Revino la original"
            icon="pi pi-undo"
            [outlined]="true"
            size="small"
            [disabled]="!selection() || !appliedSurface() || surfaceLoading()"
            (onClick)="resetSurface()"
          />
        </div>
      </div>
    </section>
  }`,
  styles: `
    :host {
      display: block;
    }
    .viewport {
      position: relative;
      height: 540px;
      background: #0e1b27;
    }
    .canvas-host {
      width: 100%;
      height: 100%;
      overflow: hidden;
      touch-action: none;
    }
    .hidden {
      visibility: hidden;
    }
    .overlay {
      position: absolute;
      inset: 0;
      display: grid;
      align-items: center;
    }
    .scene-label {
      position: absolute;
      top: 20px;
      left: 20px;
      font-size: 10px;
      letter-spacing: 1.4px;
      color: #a8c4ce;
      pointer-events: none;
    }
    .camera-action {
      position: absolute;
      top: 16px;
      right: 16px;
    }
    .scene-tools {
      position: absolute;
      top: 64px;
      left: 16px;
      right: 16px;
      display: grid;
      gap: 10px;
    }
    .mode-controls {
      display: flex;
      gap: 8px;
      align-items: center;
      flex-wrap: wrap;
    }
    button,
    select {
      background: #132c3d;
      color: #dceef2;
      border: 1px solid #456371;
      border-radius: 5px;
      padding: 7px 10px;
      font: inherit;
      font-size: 12px;
    }
    button[aria-pressed='true'] {
      background: #125951;
      border-color: #5ee2d3;
    }
    button:focus-visible,
    select:focus-visible,
    input:focus-visible {
      outline: 2px solid #5ee2d3;
      outline-offset: 2px;
    }
    .mode-controls label {
      display: flex;
      align-items: center;
      gap: 4px;
      color: #dceef2;
      background: #132c3dcc;
      padding: 5px;
      font-size: 12px;
    }
    .zone-control {
      display: flex;
      gap: 7px;
      align-items: center;
      font-size: 11px;
      color: #a8c4ce;
    }
    .zone-control select {
      max-width: 250px;
    }
    .selection {
      position: absolute;
      bottom: 52px;
      left: 16px;
      display: grid;
      gap: 4px;
      padding: 10px;
      background: #102635ed;
      border: 1px solid #456371;
      color: #dceef2;
      font-size: 11px;
      max-width: calc(100% - 32px);
    }
    code {
      overflow-wrap: anywhere;
      font-size: 10px;
    }
    .legend {
      position: absolute;
      bottom: 16px;
      left: 16px;
      right: 16px;
      font-size: 10px;
      color: #a8c4ce;
      pointer-events: none;
    }
    .asset-status {
      position: absolute;
      bottom: 32px;
      right: 16px;
      padding: 7px;
      background: #102635ed;
      color: #e8c47c;
      font-size: 11px;
      max-width: 75%;
    }
    .material-editor {
      display: grid;
      gap: 12px;
      padding: 16px;
      background: #101a24;
      border-top: 1px solid #25313e;
    }
    .material-heading,
    .material-actions,
    .material-buttons {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 10px;
    }
    .material-heading > div {
      display: grid;
      gap: 4px;
    }
    .material-heading strong {
      color: #e8eef5;
      font-size: 12px;
      font-weight: 600;
    }
    .material-heading span,
    .material-status {
      color: #8e9caf;
      font-size: 10px;
    }
    .zone-chip {
      padding: 5px 8px;
      border: 1px solid #2e4c49;
      border-radius: 5px;
      color: #59d9ba !important;
      white-space: nowrap;
    }
    .surface-grid {
      display: grid;
      grid-template-columns: repeat(6, minmax(0, 1fr));
      gap: 8px;
    }
    .surface-option {
      display: grid;
      gap: 6px;
      padding: 5px;
      border: 1px solid #25313e;
      border-radius: 7px;
      background: #131e29;
      color: #c3cedb;
      cursor: pointer;
      text-align: left;
    }
    .surface-option.selected {
      border-color: #59d9ba;
      color: #e8eef5;
    }
    .surface-option img {
      width: 100%;
      aspect-ratio: 2 / 1;
      border-radius: 4px;
      object-fit: cover;
    }
    .surface-option span {
      font-size: 10px;
    }
    .material-buttons {
      justify-content: flex-end;
    }
    .material-error {
      color: #ff9a91;
    }
    @media (max-width: 650px) {
      .viewport {
        height: 520px;
      }
      .scene-label {
        top: 62px;
      }
      .scene-tools {
        top: 88px;
      }
      .zone-control {
        flex-wrap: wrap;
      }
      .selection {
        bottom: 64px;
      }
      .surface-grid {
        grid-template-columns: repeat(3, minmax(0, 1fr));
      }
      .material-heading,
      .material-actions {
        align-items: flex-start;
        flex-direction: column;
      }
      .material-buttons {
        justify-content: flex-start;
        flex-wrap: wrap;
      }
    }
  `,
})
export class DigitalTwin {
  private static instance = 0;
  protected readonly zoneSelectId = `twin-zone-${++DigitalTwin.instance}`;
  readonly scene = input.required<TwinScene>();
  protected readonly status = signal<'loading' | 'ready' | 'error'>('loading');
  protected readonly mode = signal<TwinDisplayMode>('interior');
  protected readonly resources = signal<TwinResources>({
    aircraft: true,
    baggage: true,
    vehicles: true,
    runway: false,
  });
  protected readonly selection = signal<TwinSelection | null>(null);
  protected readonly zones = signal<TwinSelection[]>([]);
  protected readonly assetLoading = signal(false);
  protected readonly assetError = signal('');
  protected readonly surfaceOptions = TWIN_SURFACES;
  protected readonly selectedSurface = signal<TwinSurface>('terrazzo');
  protected readonly appliedSurface = signal<TwinSurface | null>(null);
  protected readonly surfaceLoading = signal(false);
  protected readonly surfaceError = signal('');
  private readonly host = viewChild.required<ElementRef<HTMLElement>>('host');
  private readonly createRenderer = inject(TWIN_RENDERER_FACTORY);
  private renderer?: TwinRenderer;
  private modeVersion = 0;

  constructor() {
    afterNextRender(() => void this.initialize());
    effect(() => {
      const scene = this.scene();
      this.renderer?.setScene(scene);
    });
    inject(DestroyRef).onDestroy(() => this.releaseRenderer());
  }
  protected async initialize(): Promise<void> {
    this.releaseRenderer();
    this.status.set('loading');
    this.mode.set('interior');
    this.assetError.set('');
    this.assetLoading.set(false);
    this.selection.set(null);
    this.appliedSurface.set(null);
    this.surfaceError.set('');
    this.surfaceLoading.set(false);
    let renderer: TwinRenderer | undefined;
    try {
      renderer = this.createRenderer(this.host().nativeElement, () => this.fail());
      this.renderer = renderer;
      renderer.setSelectionHandler?.((value) => {
        this.selection.set(value);
        this.appliedSurface.set(
          value ? renderer?.getAppliedSurface(value.entityId) ?? null : null,
        );
      });
      renderer.setAssetStatusHandler?.((value) => {
        if (this.renderer !== renderer) return;
        this.assetLoading.set(value.loading);
        this.assetError.set(value.error ?? '');
        this.refreshZones();
      });
      renderer.setResources?.(this.resources());
      renderer.setScene(this.scene());
      if (renderer.ready) await renderer.ready;
      if (this.renderer === renderer) {
        this.refreshZones();
        this.status.set('ready');
      }
    } catch {
      if (!renderer || this.renderer === renderer) this.fail();
    }
  }
  protected async setMode(mode: TwinDisplayMode): Promise<void> {
    const version = ++this.modeVersion;
    this.mode.set(mode);
    this.assetError.set('');
    try {
      await this.renderer?.setDisplayMode?.(mode);
    } catch {
      if (version === this.modeVersion) {
        this.mode.set('interior');
        this.assetError.set(
          'Contextul exterior nu s-a încărcat. Poți reîncerca; terminalul rămâne disponibil.',
        );
      }
    }
    if (version === this.modeVersion) this.refreshZones();
  }
  protected toggleResource(resource: TwinResource, event: Event): void {
    this.resources.update((value) => ({
      ...value,
      [resource]: (event.target as HTMLInputElement).checked,
    }));
    this.renderer?.setResources?.(this.resources());
    this.refreshZones();
  }
  protected selectZone(event: Event): void {
    this.renderer?.selectEntity?.((event.target as HTMLSelectElement).value);
  }
  protected chooseSurface(surface: TwinSurface): void {
    this.selectedSurface.set(surface);
    this.surfaceError.set('');
  }
  protected async applySurface(): Promise<void> {
    const renderer = this.renderer;
    const selection = this.selection();
    if (!renderer || !selection || this.mode() !== 'interior') return;
    const surface = this.selectedSurface();
    this.surfaceLoading.set(true);
    this.surfaceError.set('');
    try {
      await renderer.applySurface(selection.entityId, surface);
      if (this.renderer === renderer && this.selection()?.entityId === selection.entityId)
        this.appliedSurface.set(renderer.getAppliedSurface(selection.entityId));
    } catch (error) {
      if (this.renderer === renderer)
        this.surfaceError.set(
          `Materialul nu a putut fi aplicat: ${error instanceof Error ? error.message : 'eroare necunoscută'}`,
        );
    } finally {
      if (this.renderer === renderer) this.surfaceLoading.set(false);
    }
  }
  protected resetSurface(): void {
    const selection = this.selection();
    if (!selection || !this.renderer) return;
    this.renderer.resetSurface(selection.entityId);
    this.appliedSurface.set(null);
    this.surfaceError.set('');
  }
  private refreshZones(): void {
    this.zones.set(this.renderer?.getSelectableZones?.() ?? []);
  }
  protected resetCamera(): void {
    this.renderer?.resetCamera();
  }
  private fail(): void {
    this.releaseRenderer();
    this.status.set('error');
  }
  private releaseRenderer(): void {
    const renderer = this.renderer;
    this.renderer = undefined;
    ++this.modeVersion;
    renderer?.dispose();
  }
}

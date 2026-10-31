import {
  afterNextRender,
  Component,
  computed,
  DestroyRef,
  effect,
  ElementRef,
  inject,
  Injector,
  InjectionToken,
  input,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { A11yModule } from '@angular/cdk/a11y';
import { DataState } from '../../shared/ui/data-state';
import { TerminalMap } from './terminal-map';
import { TERMINAL_MAP_ZONES } from './terminal-zones';
import { ZoneInspector } from './zone-inspector';
import { HeatmapControls } from './heatmap-controls';
import { PassengerPlayback } from './passenger-playback';
import { FlowLegend } from './flow-legend';
import { PassengerPacket, PassengerStats } from './passenger-model';
import {
  buildHeatmapFrame,
  busiestMoment,
  comparable,
  CONGESTION_BANDS,
  HeatmapDataset,
  HeatmapFrame,
  heatmapMinutes,
  TwinHover,
} from './heatmap-model';
import {
  DEFAULT_TWIN_RESOURCES,
  TwinDetailPolicy,
  TwinExteriorStatus,
  TwinDisplayMode,
  TwinCameraView,
  TwinRenderer,
  TwinResource,
  TwinResources,
  TwinScene,
  TwinSelection,
  TwinNavigationMode,
  TwinPerformance,
  TwinFailureReason,
} from './twin-model';

export type TwinRendererFactory = (
  host: HTMLElement,
  onError: (reason?: TwinFailureReason) => void,
  signal: AbortSignal,
) => TwinRenderer | Promise<TwinRenderer>;
export const TWIN_RENDERER_FACTORY = new InjectionToken<TwinRendererFactory>(
  'TWIN_RENDERER_FACTORY',
  {
    providedIn: 'root',
    factory: () => async (host, onError, signal) => {
      const { ThreeTwinRenderer } = await import('./three-twin-renderer');
      signal.throwIfAborted();
      return new ThreeTwinRenderer(host, onError);
    },
  },
);
export const TWIN_READY_TIMEOUT_MS = 10_000;
@Component({
  selector: 'app-digital-twin',
  host: { '[class.dashboard-view]': 'dashboardMode()' },
  imports: [
    A11yModule,
    DataState,
    TerminalMap,
    ZoneInspector,
    HeatmapControls,
    PassengerPlayback,
    FlowLegend,
  ],
  templateUrl: './digital-twin.html',
  styleUrl: './digital-twin.css',
})
export class DigitalTwin {
  private readonly injector = inject(Injector);
  private expandedTrigger: HTMLElement | null = null;
  private static instance = 0;
  protected readonly zoneSelectId = `twin-zone-${++DigitalTwin.instance}`;
  readonly scene = input.required<TwinScene>();
  readonly initialPresentation = input<'2d' | '3d'>('2d');
  readonly heatmap = input<HeatmapDataset | null>(null);
  readonly replayMode = input(false);
  /** The central dashboard owns analytics and follows the latest observation. */
  readonly dashboardMode = input(false);
  readonly replayFrame = input<{ snapshotMinute: number; packet: PassengerPacket } | null>(null);
  /** Finished runs can open where something happens (the longest queue) instead of the last minute. */
  readonly openAt = input<'latest' | 'busiest'>('latest');
  protected readonly dataset = computed(() => this.heatmap() ?? this.scene().heatmap ?? null);
  protected readonly heatmapEnabled = signal(true);
  protected readonly minute = signal(0);
  protected readonly followLatest = signal(true);
  protected readonly variant = signal<HeatmapFrame['variant']>('baseline');
  protected readonly heatmapFrame = computed(() => {
    const dataset = this.dataset();
    return dataset ? buildHeatmapFrame(dataset, this.variant(), this.minute()) : null;
  });
  protected readonly timelineMinutes = computed(() =>
    this.dataset() ? heatmapMinutes(this.dataset()!) : [],
  );
  protected readonly passengerRun = computed(() => {
    const dataset = this.dataset();
    return this.variant() === 'optimized' && dataset && comparable(dataset)
      ? dataset.optimized
      : dataset?.baseline;
  });
  protected readonly passengerPacket = signal<PassengerPacket | null>(null);
  protected playheadMinute(): number {
    return Math.floor(this.passengerPacket()?.timeMinutes ?? 0);
  }
  protected readonly passengerStats = signal<PassengerStats | null>(null);
  protected readonly hover = signal<(TwinHover & { left: number; top: number }) | null>(null);
  protected readonly hoverZone = computed(() =>
    this.heatmapFrame()?.zones.find((zone) => zone.zoneId === this.hover()?.selection.zoneId),
  );
  protected readonly congestionBands = CONGESTION_BANDS;
  private heatmapContext = '';
  protected readonly status = signal<'loading' | 'ready' | 'error'>('ready');
  protected readonly mode = signal<TwinDisplayMode>('interior');
  protected readonly cameraView = signal<TwinCameraView>('overview');
  protected readonly touring = signal(false);
  private tourTimer?: ReturnType<typeof setInterval>;
  protected readonly expanded = signal(false);
  protected readonly presentation = signal<'3d' | '2d'>('2d');
  protected readonly navigation = signal<TwinNavigationMode>('orbit');
  protected readonly performance = signal<TwinPerformance | null>(null);
  protected readonly errorMessage = signal('');
  protected readonly resources = signal<TwinResources>({ ...DEFAULT_TWIN_RESOURCES });
  protected readonly detailPolicy = signal<TwinDetailPolicy>('auto');
  protected readonly exteriorStatus = signal<TwinExteriorStatus>({
    level: 'unloaded',
    policy: 'auto',
    entities: 0,
  });
  protected readonly selection = signal<TwinSelection | null>(null);
  protected readonly zones = signal<TwinSelection[]>([]);
  protected readonly assetLoading = signal(false);
  protected readonly assetError = signal('');
  private readonly host = viewChild.required<ElementRef<HTMLElement>>('host');
  private readonly map = viewChild(TerminalMap);
  private readonly playback = viewChild(PassengerPlayback);
  protected readonly playbackAvailable = computed(() => this.playback()?.canPlay() ?? false);
  private readonly createRenderer = inject(TWIN_RENDERER_FACTORY);
  private renderer?: TwinRenderer;
  private rendererRequest?: AbortController;
  private modeVersion = 0;

  constructor() {
    const closeExpanded = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && this.expanded()) this.expandViewport();
    };
    const visibilityChanged = () => {
      if (document.hidden) this.stopTour();
    };
    document.addEventListener('keydown', closeExpanded);
    document.addEventListener('visibilitychange', visibilityChanged);
    afterNextRender(() => {
      if (this.initialPresentation() === '3d') void this.initialize();
      else this.refreshZones();
    });
    effect(() => {
      const scene = this.scene();
      try {
        this.renderer?.setScene(scene);
      } catch {
        this.fail();
      }
    });
    effect(() => {
      const dataset = this.dataset();
      const context = dataset?.baseline.runId ?? '';
      if (context !== this.heatmapContext) {
        this.selection.set(null);
        this.hover.set(null);
        this.passengerStats.set(null);
        this.passengerPacket.set(null);
        this.renderSafely((renderer) => renderer.selectEntity?.(''));
      }
      if (this.replayMode()) {
        this.heatmapContext = context;
        this.variant.set('baseline');
        this.followLatest.set(false);
        const frame = this.replayFrame();
        this.minute.set(frame?.snapshotMinute ?? 0);
        this.passengerPacket.set(frame?.packet ?? null);
        return;
      }
      if (context !== this.heatmapContext) {
        this.heatmapContext = context;
        this.variant.set('baseline');
        const busiest =
          dataset && untracked(this.openAt) === 'busiest' ? busiestMoment(dataset) : null;
        this.followLatest.set(!busiest);
        this.minute.set(
          busiest?.elapsedMinutes ?? (dataset ? (heatmapMinutes(dataset).at(-1) ?? 0) : 0),
        );
      }
      if (dataset && this.followLatest()) this.minute.set(heatmapMinutes(dataset).at(-1) ?? 0);
      if (dataset && this.variant() === 'optimized' && !comparable(dataset))
        this.variant.set('baseline');
    });
    effect(() => {
      // Subscribe before the asynchronous renderer exists so time changes keep
      // the 3D surfaces synchronized with the inspector and HTML metrics.
      const frame = this.heatmapFrame();
      this.renderSafely((renderer) => renderer.setHeatmap?.(this.heatmapEnabled() ? frame : null));
    });
    effect(() => {
      const packet = this.passengerPacket();
      const run = this.passengerRun();
      this.renderSafely((renderer) =>
        renderer.setPassengers?.(
          run?.runId === packet?.replay.runId && run?.layoutId === packet?.replay.layoutId
            ? packet
            : null,
        ),
      );
    });
    inject(DestroyRef).onDestroy(() => {
      this.stopTour();
      document.removeEventListener('keydown', closeExpanded);
      document.removeEventListener('visibilitychange', visibilityChanged);
      this.releaseRenderer();
    });
  }
  protected async initialize(): Promise<void> {
    this.releaseRenderer();
    const request = new AbortController();
    this.rendererRequest = request;
    this.status.set('loading');
    this.mode.set('interior');
    this.cameraView.set('overview');
    this.presentation.set('3d');
    this.performance.set(null);
    this.assetError.set('');
    this.assetLoading.set(false);
    this.selection.set(null);
    let renderer: TwinRenderer | undefined;
    let earlyFailure: TwinFailureReason | undefined;
    try {
      renderer = await this.createRenderer(
        this.host().nativeElement,
        (reason) => {
          if (!renderer) earlyFailure = reason ?? 'unavailable';
          else if (this.renderer === renderer) this.fail(reason);
        },
        request.signal,
      );
      if (request.signal.aborted) {
        renderer.dispose();
        return;
      }
      if (earlyFailure) {
        renderer.dispose();
        this.fail(earlyFailure);
        return;
      }
      this.renderer = renderer;
      renderer.setSelectionHandler?.((value) => {
        if (this.renderer !== renderer) return;
        this.selection.set(value);
        if (value) this.cameraView.set('overview');
      });
      renderer.setAssetStatusHandler?.((value) => {
        if (this.renderer !== renderer) return;
        this.assetLoading.set(value.loading);
        this.assetError.set(value.error ?? '');
        this.refreshZones();
      });
      renderer.setExteriorDetailPolicy?.(this.detailPolicy());
      renderer.setExteriorStatusHandler?.((value) => {
        if (this.renderer !== renderer) return;
        this.exteriorStatus.set(value);
        this.refreshZones();
      });
      renderer.setResources?.(this.resources());
      renderer.setNavigationMode?.(this.navigation());
      renderer.setHeatmap?.(this.heatmapEnabled() ? this.heatmapFrame() : null);
      renderer.setPassengerStatsHandler?.((stats) => {
        if (this.renderer === renderer) this.passengerStats.set(stats);
      });
      const packet = this.passengerPacket(),
        run = this.passengerRun();
      renderer.setPassengers?.(
        run?.runId === packet?.replay.runId && run?.layoutId === packet?.replay.layoutId
          ? packet
          : null,
      );
      renderer.setHoverHandler?.((value) => {
        if (this.renderer === renderer) this.showHover(value);
      });
      renderer.setPerformanceHandler?.((value) => {
        if (this.renderer === renderer) this.performance.set(value);
      });
      renderer.setScene(this.scene());
      if (request.signal.aborted) return;
      if (renderer.ready) {
        let timer: ReturnType<typeof setTimeout> | undefined;
        let onAbort: (() => void) | undefined;
        try {
          await Promise.race([
            renderer.ready,
            new Promise<never>((_, reject) => {
              onAbort = () => reject(request.signal.reason);
              request.signal.addEventListener('abort', onAbort, { once: true });
              timer = setTimeout(
                () => reject(new Error('Scene initialization timed out.')),
                TWIN_READY_TIMEOUT_MS,
              );
            }),
          ]);
        } finally {
          clearTimeout(timer);
          if (onAbort) request.signal.removeEventListener('abort', onAbort);
        }
      }
      if (this.renderer === renderer) {
        this.refreshZones();
        if (this.renderer === renderer) this.status.set('ready');
      }
    } catch {
      if (request.signal.aborted) return;
      if (!renderer || this.renderer === renderer) this.fail();
    }
  }
  protected async setMode(mode: TwinDisplayMode): Promise<void> {
    this.stopTour();
    const version = ++this.modeVersion;
    this.mode.set(mode);
    this.hover.set(null);
    this.cameraView.set('overview');
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
  protected setDetailPolicy(policy: TwinDetailPolicy): void {
    this.detailPolicy.set(policy);
    this.renderSafely((renderer) => renderer.setExteriorDetailPolicy?.(policy));
  }
  protected toggleResource(resource: TwinResource, event: Event): void {
    this.resources.update((value) => ({
      ...value,
      [resource]: (event.target as HTMLInputElement).checked,
    }));
    this.renderSafely((renderer) => renderer.setResources?.(this.resources()));
    this.refreshZones();
  }
  protected selectZone(event: Event): void {
    this.focusEntity((event.target as HTMLSelectElement).value);
  }
  /** Seeks passenger playback to `minute`, optionally focuses a zone, and plays from there. */
  playFrom(minute: number, zoneId?: string): void {
    this.setMinute(minute);
    this.playback()?.playFrom(minute);
    const zone = TERMINAL_MAP_ZONES.find((item) => item.zoneId === zoneId);
    if (zone) this.focusEntity(zone.entityId);
  }
  private focusEntity(entityId: string): void {
    this.stopTour();
    if (this.presentation() === '2d')
      this.selection.set(TERMINAL_MAP_ZONES.find((zone) => zone.entityId === entityId) ?? null);
    else this.renderSafely((renderer) => renderer.selectEntity?.(entityId));
  }
  private refreshZones(): void {
    try {
      this.zones.set(
        this.presentation() === '2d'
          ? [...TERMINAL_MAP_ZONES]
          : (this.renderer?.getSelectableZones?.() ?? []),
      );
    } catch {
      this.fail();
    }
  }
  protected resetCamera(): void {
    this.stopTour();
    this.cameraView.set('overview');
    if (this.presentation() === '2d') this.map()?.reset();
    else this.renderSafely((renderer) => renderer.resetCamera());
  }
  /** One picker for every vantage point: the terminal's camera views and the airside context. */
  protected async selectView(event: Event): Promise<void> {
    const value = (event.target as HTMLSelectElement).value;
    if (value === 'exterior') return this.setMode('exterior');
    const fromExterior = this.mode() === 'exterior';
    // Returning inside already lands on the overview.
    if (fromExterior) await this.setMode('interior');
    if (!fromExterior || value !== 'overview') this.setView(value as TwinCameraView);
  }
  protected setView(view: TwinCameraView): void {
    this.stopTour();
    this.cameraView.set(view);
    this.renderSafely((renderer) => renderer.setCameraView?.(view));
  }
  protected toggleTour(): void {
    if (this.touring()) {
      this.stopTour();
      return;
    }
    if (this.presentation() !== '3d' || this.mode() !== 'interior') return;
    const views: TwinCameraView[] = ['gates', 'security', 'immersive', 'overview'];
    let step = 0;
    this.touring.set(true);
    this.selection.set(null);
    this.renderSafely((renderer) => renderer.selectEntity?.(''));
    const advance = () => {
      const view = views[step++];
      this.cameraView.set(view);
      this.renderSafely((renderer) => renderer.setCameraView?.(view));
      if (step === views.length) this.stopTour();
    };
    advance();
    if (this.touring()) this.tourTimer = setInterval(advance, 6500);
  }
  protected stopTour(): void {
    if (this.tourTimer !== undefined) clearInterval(this.tourTimer);
    this.tourTimer = undefined;
    this.touring.set(false);
  }
  protected expandViewport(): void {
    const opening = !this.expanded();
    if (opening)
      this.expandedTrigger =
        document.activeElement instanceof HTMLElement ? document.activeElement : null;
    this.expanded.set(opening);
    afterNextRender(
      () => {
        if (this.expanded() !== opening) return;
        if (opening)
          this.host()
            .nativeElement.closest('.viewport')
            ?.querySelector<HTMLButtonElement>('.camera-action button:last-child')
            ?.focus();
        else if (this.expandedTrigger?.isConnected) this.expandedTrigger.focus();
      },
      { injector: this.injector },
    );
  }
  protected setPresentation(mode: '3d' | '2d'): void {
    this.stopTour();
    if (mode === '3d' && !this.renderer) {
      void this.initialize();
      return;
    }
    if (mode === '2d' && this.status() === 'loading') {
      this.releaseRenderer();
      this.status.set('ready');
      this.assetLoading.set(false);
    }
    this.presentation.set(mode);
    if (mode === '2d' && this.mode() === 'exterior') void this.setMode('interior');
    this.selection.set(null);
    this.renderSafely((renderer) => {
      renderer.selectEntity?.('');
      renderer.setActive?.(mode === '3d');
    });
    this.refreshZones();
  }
  protected setNavigation(mode: TwinNavigationMode): void {
    this.stopTour();
    this.navigation.set(mode);
    this.renderSafely((renderer) => renderer.setNavigationMode?.(mode));
  }
  protected zoom(direction: 'in' | 'out'): void {
    this.stopTour();
    if (this.presentation() === '2d') this.map()?.zoom(direction);
    else this.renderSafely((renderer) => renderer.zoomCamera?.(direction));
  }
  protected panMap(direction: 'left' | 'right' | 'up' | 'down'): void {
    this.map()?.pan(direction);
  }
  protected selectMapZone(zone: TwinSelection): void {
    this.selection.set(zone);
  }
  protected showHover(value: TwinHover | null): void {
    if (!value) {
      this.hover.set(null);
      return;
    }
    const rect = this.host().nativeElement.getBoundingClientRect();
    this.hover.set({
      ...value,
      left: Math.max(8, Math.min(value.clientX - rect.left + 14, rect.width - 280)),
      top: Math.max(
        8,
        Math.min(value.clientY - rect.top + 14, this.host().nativeElement.clientHeight - 125),
      ),
    });
  }
  protected setMinute(minute: number): void {
    this.followLatest.set(false);
    this.minute.set(minute);
  }
  protected togglePlayback(): void {
    this.playback()?.togglePlayback();
  }
  protected setVariant(variant: HeatmapFrame['variant']): void {
    const dataset = this.dataset();
    if (variant === 'baseline' || (dataset && comparable(dataset))) this.variant.set(variant);
  }
  protected clearSelection(): void {
    this.selection.set(null);
    this.renderSafely((renderer) => renderer.selectEntity?.(''));
  }
  private renderSafely(action: (renderer: TwinRenderer) => void): void {
    if (!this.renderer) return;
    try {
      action(this.renderer);
    } catch {
      this.fail();
    }
  }
  private fail(reason: TwinFailureReason = 'unavailable'): void {
    this.releaseRenderer();
    this.assetLoading.set(false);
    this.assetError.set('');
    const selected = this.selection();
    if (selected && !TERMINAL_MAP_ZONES.some((zone) => zone.entityId === selected.entityId))
      this.selection.set(null);
    this.presentation.set('2d');
    this.mode.set('interior');
    this.status.set('error');
    this.errorMessage.set(
      reason === 'performance'
        ? 'Scena 3D nu menține pragul de 30 FPS. Harta 2D rămâne disponibilă pentru investigare.'
        : 'Scena 3D nu este disponibilă. Harta 2D rămâne disponibilă pentru investigare.',
    );
    this.refreshZones();
  }
  private releaseRenderer(): void {
    this.stopTour();
    this.rendererRequest?.abort();
    this.rendererRequest = undefined;
    const renderer = this.renderer;
    this.renderer = undefined;
    this.hover.set(null);
    ++this.modeVersion;
    try {
      renderer?.dispose();
    } catch {
      /* A lost graphics context must not break the console. */
    }
  }
}

import { TestBed } from '@angular/core/testing';
import { DigitalTwin, TWIN_RENDERER_FACTORY, TWIN_READY_TIMEOUT_MS } from './digital-twin';
import { TwinRenderer } from './twin-model';
import { TERMINAL_MAP_ZONES } from './terminal-zones';
import { datasetFixture } from './heatmap.testing';
import { HeatmapFrame, TwinHover } from './heatmap-model';
import { motionFixture } from './passenger.testing';
import { TwinScene, TwinSelection, TwinFailureReason } from './twin-model';

function create3D() {
  const fixture = TestBed.createComponent(DigitalTwin);
  fixture.componentRef.setInput('initialPresentation', '3d');
  return fixture;
}
function mount(factory: unknown) {
  TestBed.configureTestingModule({
    imports: [DigitalTwin],
    providers: [{ provide: TWIN_RENDERER_FACTORY, useValue: factory }],
  });
  return create3D();
}
describe('DigitalTwin component boundary', () => {
  it('falls back after a stalled scene load and ignores a late readiness result', async () => {
    let ready!: () => void;
    const renderer: TwinRenderer = {
      ready: new Promise<void>((resolve) => (ready = resolve)),
      setScene: vi.fn(),
      resetCamera: vi.fn(),
      dispose: vi.fn(),
    };
    vi.useFakeTimers();
    try {
      const fixture = mount(() => renderer);
      fixture.componentRef.setInput('scene', { aircraft: [], heatmap: datasetFixture() });
      fixture.componentRef.setInput('dashboardMode', true);
      fixture.detectChanges();
      await vi.advanceTimersByTimeAsync(TWIN_READY_TIMEOUT_MS);
      fixture.detectChanges();
      expect(fixture.nativeElement.querySelector('app-terminal-map')).toBeTruthy();
      expect(fixture.nativeElement.querySelector('table')?.textContent).toContain('21');
      ready();
      await vi.advanceTimersByTimeAsync(0);
      fixture.detectChanges();
      expect(fixture.nativeElement.querySelector('app-terminal-map')).toBeTruthy();
      expect(renderer.dispose).toHaveBeenCalledOnce();
      fixture.destroy();
    } finally {
      vi.useRealTimers();
    }
  });
  it('keeps numeric dashboard heatmap values and legend available when a renderer update and disposal fail', async () => {
    const renderer: TwinRenderer = {
      setScene: vi.fn(),
      setHeatmap: vi.fn(),
      resetCamera: vi.fn(),
      dispose: vi.fn(() => {
        throw new Error('lost context');
      }),
    };
    const fixture = mount(() => renderer);
    fixture.componentRef.setInput('scene', { aircraft: [], heatmap: datasetFixture() });
    fixture.componentRef.setInput('dashboardMode', true);
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('app-heatmap-controls .legend')?.textContent).toContain('Critic');
    expect(host.querySelector('table')?.textContent).toContain('21');
    vi.mocked(renderer.setHeatmap!).mockImplementation(() => {
      throw new Error('graphics driver');
    });
    const data = datasetFixture();
    data.baseline.capacities[0].label = 'Control capturat din API';
    data.baseline.capacities[0].zoneId = 'zone-api';
    data.baseline.snapshots.forEach((row) => (row.zoneId = 'zone-api'));
    fixture.componentRef.setInput('scene', { aircraft: [], heatmap: data });
    await fixture.whenStable();
    expect(host.querySelector('app-terminal-map')).toBeTruthy();
    expect(host.querySelector('app-heatmap-controls details')?.hasAttribute('open')).toBe(true);
    expect(host.querySelector('table')?.textContent).toContain('Control capturat din API');
    expect(host.querySelector('table')?.textContent).toContain('21');
    expect(host.querySelector('input[type="range"]')).toBeNull();
    fixture.destroy();
  });
  it('returns focus to the viewport trigger on Escape and offers keyboard panning in the WebGL fallback', async () => {
    const fixture = mount(() => {
      throw new Error('no WebGL');
    });
    fixture.componentRef.setInput('scene', { aircraft: [], heatmap: datasetFixture() });
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    const trigger = host.querySelector<HTMLButtonElement>('.camera-action button:last-child')!;
    trigger.focus();
    trigger.click();
    await fixture.whenStable();
    expect(document.activeElement).toBe(trigger);
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    await fixture.whenStable();
    expect(host.querySelector('[role="dialog"]')).toBeNull();
    expect(document.activeElement).toBe(trigger);
    const svg = host.querySelector('app-terminal-map svg')!;
    const before = svg.getAttribute('viewBox');
    svg.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true, cancelable: true }),
    );
    await fixture.whenStable();
    expect(svg.getAttribute('viewBox')).not.toBe(before);
    const zone = svg.querySelector('g[role="button"]')!;
    expect(zone.getAttribute('aria-label')).toContain('pasageri în coadă');
    zone.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
    await fixture.whenStable();
    expect(host.querySelector('app-zone-inspector')).toBeTruthy();
    fixture.destroy();
  });
  it('switches exterior and detail policy without resetting simulation time, comparison, playback data or the renderer', async () => {
    let select!: (value: TwinSelection | null) => void;
    const renderer: TwinRenderer = {
      setScene: vi.fn(),
      setHeatmap: vi.fn(),
      setPassengers: vi.fn(),
      resetCamera: vi.fn(),
      dispose: vi.fn(),
      setDisplayMode: vi.fn(async () => {
        select(null);
      }),
      setExteriorDetailPolicy: vi.fn(),
      setSelectionHandler: (handler) => {
        select = handler;
      },
    };
    const factory = vi.fn(() => renderer);
    const fixture = mount(factory);
    const data = datasetFixture();
    const source = JSON.stringify(data);
    fixture.componentRef.setInput('scene', { aircraft: [], heatmap: data });
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    const button = (name: string) =>
      [...host.querySelectorAll<HTMLButtonElement>('button')].find(
        (b) => b.textContent?.trim() === name,
      )!;
    // Interior and exterior are vantage points of the single view picker.
    const view = (value: string) => {
      const picker = host.querySelector<HTMLSelectElement>('.view-select select')!;
      picker.value = value;
      picker.dispatchEvent(new Event('change'));
    };
    button('Optimizat').click();
    await fixture.whenStable();
    const before = host.querySelector('[data-testid="heatmap-queue-kpi"]')!.textContent;
    const playback = host.querySelector('app-passenger-playback');
    view('exterior');
    await fixture.whenStable();
    button('Economic').click();
    await fixture.whenStable();
    expect(renderer.setDisplayMode).toHaveBeenLastCalledWith('exterior');
    expect(renderer.setExteriorDetailPolicy).toHaveBeenLastCalledWith('low');
    expect(host.querySelector('[data-testid="exterior-status"]')!.textContent).toContain(
      'Exterior',
    );
    select({
      entityId: 'stand-id',
      zoneId: 'scene-id',
      zoneType: 'scene',
      label: 'STAND-1',
      resource: 'stands',
      synthetic: true,
      exterior: {
        code: 'STAND-1',
        status: 'synthetic',
        positionMeters: [1, 0, 2],
        dimensionsMeters: [32, 0, 50],
        gateId: 'gate-id',
        gateCode: 'G-1',
        level: 'low',
        positionSource: 'authored-layout',
      },
    });
    await fixture.whenStable();
    const inspector = host.querySelector('app-zone-inspector')!;
    expect(inspector.textContent).not.toContain('synthetic · demonstrativ');
    expect(inspector.textContent).toContain('G-1');
    expect(inspector.textContent).not.toContain('runId');
    expect(inspector.textContent).not.toContain('Fără snapshot');
    view('overview');
    await fixture.whenStable();
    expect(host.querySelector('[data-testid="heatmap-queue-kpi"]')!.textContent).toBe(before);
    expect(vi.mocked(renderer.setHeatmap!).mock.calls.at(-1)![0]).toMatchObject({
      runId: 'optimized-run',
      minute: 2,
    });
    expect(host.querySelector('app-passenger-playback')).toBe(playback);
    expect(JSON.stringify(data)).toBe(source);
    expect(factory).toHaveBeenCalledOnce();
    fixture.destroy();
  });
  it('guides the camera through architectural views without touching simulation time and stops on manual navigation', async () => {
    const renderer: TwinRenderer = {
      setScene: vi.fn(),
      setCameraView: vi.fn(),
      resetCamera: vi.fn(),
      dispose: vi.fn(),
      selectEntity: vi.fn(),
    };
    const fixture = mount(() => renderer);
    fixture.componentRef.setInput('scene', { aircraft: [], heatmap: datasetFixture() });
    await fixture.whenStable();
    let advance!: () => void;
    const interval = vi.spyOn(globalThis, 'setInterval').mockImplementation(((
      callback: () => void,
    ) => {
      advance = callback;
      return 71;
    }) as typeof setInterval);
    const clear = vi.spyOn(globalThis, 'clearInterval').mockImplementation(() => {});
    const host = fixture.nativeElement as HTMLElement;
    const kpi = host.querySelector('[data-testid="heatmap-queue-kpi"]')!.textContent;
    host.querySelector<HTMLButtonElement>('.tour-button')!.click();
    fixture.detectChanges();
    expect(renderer.setCameraView).toHaveBeenLastCalledWith('gates');
    advance();
    fixture.detectChanges();
    expect(renderer.setCameraView).toHaveBeenLastCalledWith('security');
    expect(host.querySelector('[data-testid="heatmap-queue-kpi"]')!.textContent).toBe(kpi);
    host.querySelector('.canvas-host')!.dispatchEvent(new Event('pointerdown', { bubbles: true }));
    fixture.detectChanges();
    expect(clear).toHaveBeenCalledWith(71);
    expect(host.querySelector('.tour-button')!.getAttribute('aria-pressed')).toBe('false');
    fixture.destroy();
    interval.mockRestore();
    clear.mockRestore();
  });
  it('can replay individual keyframes before a stored final snapshot without showing future KPI values', async () => {
    const renderer: TwinRenderer = {
      setScene: vi.fn(),
      setHeatmap: vi.fn(),
      setPassengers: vi.fn(),
      resetCamera: vi.fn(),
      dispose: vi.fn(),
    };
    const run = motionFixture();
    run.snapshots = [{ ...run.snapshots[0], elapsedMinutes: 60 }];
    const fixture = mount(() => renderer);
    fixture.componentRef.setInput('scene', { aircraft: [], heatmap: { baseline: run } });
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    const cursor = host.querySelector(
      'input[aria-label="Poziția animației în minute"]',
    ) as HTMLInputElement;
    expect(cursor.disabled).toBe(false);
    expect(cursor.min).toBe('1');
    expect(cursor.max).toBe('60');
    cursor.value = '1.75';
    cursor.dispatchEvent(new Event('input'));
    await fixture.whenStable();
    expect(vi.mocked(renderer.setPassengers!).mock.calls.at(-1)![0]).toMatchObject({
      timeMinutes: 1.75,
      snapshotMinute: 1.75,
    });
    expect(host.querySelector('[data-testid="heatmap-queue-kpi"]')?.textContent).toContain('—');
    expect(host.querySelector('app-passenger-playback')?.textContent).toContain(
      'fără date pentru acest minut',
    );
    fixture.destroy();
  });
  it('keeps replay data and KPI intact while changing sampling, speed, pause/resume and the shared time cursor', async () => {
    vi.stubGlobal(
      'requestAnimationFrame',
      vi.fn(() => 123),
    );
    vi.stubGlobal('cancelAnimationFrame', vi.fn());
    const renderer: TwinRenderer = {
      setScene: vi.fn(),
      setHeatmap: vi.fn(),
      setPassengers: vi.fn(),
      resetCamera: vi.fn(),
      dispose: vi.fn(),
    };
    const fixture = mount(() => renderer),
      data = datasetFixture(),
      before = JSON.stringify(data);
    fixture.componentRef.setInput('scene', { aircraft: [], heatmap: data });
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    const latest = () => vi.mocked(renderer.setPassengers!).mock.calls.at(-1)![0]!;
    const replay = latest().replay;
    const budget = host.querySelector(
      'select[aria-label="Număr maxim de simboluri pasageri"]',
    ) as HTMLSelectElement;
    budget.value = '100';
    budget.dispatchEvent(new Event('change'));
    await fixture.whenStable();
    expect(latest().budget).toBe(100);
    expect(latest().replay).toBe(replay);
    expect(host.querySelector('[data-testid="heatmap-queue-kpi"]')?.textContent).toContain('21');
    [...host.querySelectorAll('button')].find((b) => b.textContent?.trim() === '16x')!.click();
    host
      .querySelector('button[aria-label="Redă animația pasagerilor"]')!
      .dispatchEvent(new MouseEvent('click'));
    await fixture.whenStable();
    expect(latest()).toMatchObject({ speed: 16, playing: true, timeMinutes: 1 });
    host
      .querySelector('button[aria-label="Oprește animația pasagerilor"]')!
      .dispatchEvent(new MouseEvent('click'));
    await fixture.whenStable();
    expect(latest().playing).toBe(false);
    expect(latest().replay).toBe(replay);
    [...host.querySelectorAll('button')].find((b) => b.textContent?.trim() === 'Redă 3D')!.click();
    await fixture.whenStable();
    expect(latest()).toMatchObject({ playing: true, speed: 16, timeMinutes: 1 });
    expect(latest().replay).toBe(replay);
    expect(vi.mocked(renderer.setHeatmap!).mock.calls.at(-1)![0]).toMatchObject({
      runId: 'base-run',
      minute: 1,
      kpis: { queuedPassengers: 6 },
    });
    [...host.querySelectorAll('button')].find((b) => b.textContent?.trim() === 'Pauză 3D')!.click();
    await fixture.whenStable();
    expect(latest().playing).toBe(false);
    const cursor = host.querySelector('input[type="range"]') as HTMLInputElement;
    cursor.value = '1';
    cursor.dispatchEvent(new Event('input'));
    await fixture.whenStable();
    expect(latest()).toMatchObject({ timeMinutes: 2, snapshotMinute: 2, budget: 100 });
    expect(host.querySelector('[data-testid="heatmap-queue-kpi"]')?.textContent).toContain('21');
    [...host.querySelectorAll('button')]
      .find((b) => b.textContent?.trim() === 'Aglomerație')!
      .click();
    await fixture.whenStable();
    expect(latest().replay).toBe(replay);
    expect(host.querySelector('app-passenger-playback')).toBeTruthy();
    expect(JSON.stringify(data)).toBe(before);
    expect(renderer.resetCamera).not.toHaveBeenCalled();
    fixture.componentRef.setInput('scene', { aircraft: [] });
    await fixture.whenStable();
    expect(vi.mocked(renderer.setPassengers!).mock.calls.at(-1)![0]).toBeNull();
    fixture.destroy();
    vi.unstubAllGlobals();
  });
  it('changes mesh data, tooltip, inspector and KPI from the same minute/run frame and keeps comparison on the same renderer', async () => {
    let select!: (zone: TwinSelection | null) => void, hover!: (value: TwinHover | null) => void;
    const renderer: TwinRenderer = {
      setScene: vi.fn(),
      resetCamera: vi.fn(),
      dispose: vi.fn(),
      setHeatmap: vi.fn(),
      setSelectionHandler: (handler) => {
        select = handler;
      },
      setHoverHandler: (handler) => {
        hover = handler;
      },
      getSelectableZones: () => [...TERMINAL_MAP_ZONES],
    };
    const factory = vi.fn(() => renderer);
    const fixture = mount(factory);
    fixture.componentRef.setInput('scene', { aircraft: [], heatmap: datasetFixture() });
    await fixture.whenStable();
    select(TERMINAL_MAP_ZONES[0]);
    hover({ selection: TERMINAL_MAP_ZONES[0], clientX: 20, clientY: 20 });
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    const time = host.querySelector('input[type="range"]') as HTMLInputElement;
    time.value = '0';
    expect(time.value).toBe('0');
    time.dispatchEvent(new Event('input'));
    await fixture.whenStable();
    let frame = vi.mocked(renderer.setHeatmap!).mock.calls.at(-1)![0] as HeatmapFrame;
    expect(frame).toMatchObject({ runId: 'base-run', minute: 1, kpis: { queuedPassengers: 6 } });
    expect(host.querySelector('[role="tooltip"]')?.getAttribute('data-minute')).toBe('1');
    expect(host.querySelector('[data-testid="heatmap-queue-kpi"]')?.textContent).toContain('6');
    expect(host.querySelector('app-zone-inspector')?.textContent).toContain('Minutul 1');
    const optimized = [...host.querySelectorAll('button')].find(
      (b) => b.textContent?.trim() === 'Optimizat',
    )!;
    optimized.click();
    await fixture.whenStable();
    frame = vi.mocked(renderer.setHeatmap!).mock.calls.at(-1)![0] as HeatmapFrame;
    expect(frame).toMatchObject({
      runId: 'optimized-run',
      minute: 1,
      kpis: { queuedPassengers: 2 },
    });
    expect(host.querySelector('[role="tooltip"]')?.getAttribute('data-run-id')).toBe(
      'optimized-run',
    );
    expect(host.querySelector('app-zone-inspector')?.textContent).toContain('14 pasageri/min');
    expect(factory).toHaveBeenCalledOnce();
    [...host.querySelectorAll('button')].find((b) => b.textContent?.trim() === 'Hartă 2D')!.click();
    await fixture.whenStable();
    const mapZone = host.querySelector(`g[data-zone-id="${TERMINAL_MAP_ZONES[0].zoneId}"] rect`);
    expect(mapZone?.getAttribute('fill')).toBe('#41d3a2');
    expect(
      host.querySelector('ul[aria-label="Legendă accesibilă a aglomerației"]')?.textContent,
    ).toContain('Critic');
    host
      .querySelector('app-heatmap-controls summary')
      ?.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    expect(
      host.querySelector('app-heatmap-controls table [data-run-id]')?.getAttribute('data-run-id'),
    ).toBe('optimized-run');
  });
  it('shows a recoverable error when WebGL initialization fails', async () => {
    const renderer = { setScene: vi.fn(), resetCamera: vi.fn(), dispose: vi.fn() };
    const factory = vi
      .fn()
      .mockImplementationOnce(() => {
        throw Error('WebGL unavailable');
      })
      .mockReturnValue(renderer);
    const fixture = mount(factory);
    fixture.componentRef.setInput('scene', { aircraft: [] });
    await fixture.whenStable();
    expect(fixture.nativeElement.textContent).toContain('Scena 3D nu este disponibilă');
    [...(fixture.nativeElement as HTMLElement).querySelectorAll('button')]
      .find((button) => button.textContent?.trim() === 'Reîncearcă 3D')!
      .click();
    await fixture.whenStable();
    expect(factory).toHaveBeenCalledTimes(2);
    expect(fixture.nativeElement.textContent).toContain('Resetează camera');
  });
  function model(peak = false): TwinScene {
    return {
      aircraft: [],
      scenarioId: peak ? 'peak' : 'baseline',
      checkpoints: [
        {
          zoneId: TERMINAL_MAP_ZONES[0].zoneId,
          code: 'CP-1',
          capacityPerMinute: peak ? 10 : 7,
          staff: [{ id: 'person-1', name: 'Operator demo 1', role: 'Securitate' }],
          status: peak ? 'busy' : 'open',
          metrics: {
            queuedPassengers: peak ? 32 : 6,
            processedPassengers: 42,
            averageWaitMinutes: peak ? 4.2 : 0.9,
          },
        },
      ],
    };
  }
  it('keeps selection and refreshes checkpoint details when the scenario changes, without recreating the renderer', async () => {
    let selected!: (zone: TwinSelection | null) => void;
    const renderer: TwinRenderer = {
      setScene: vi.fn(),
      resetCamera: vi.fn(),
      dispose: vi.fn(),
      setSelectionHandler: (handler) => {
        selected = handler;
      },
      getSelectableZones: () => [...TERMINAL_MAP_ZONES],
    };
    const factory = vi.fn(() => renderer);
    const fixture = mount(factory);
    const first = model();
    fixture.componentRef.setInput('scene', first);
    await fixture.whenStable();
    selected(TERMINAL_MAP_ZONES[0]);
    await fixture.whenStable();
    expect(fixture.nativeElement.textContent).toContain('7 pasageri/min');
    expect(fixture.nativeElement.textContent).toContain('Operator demo 1');
    fixture.componentRef.setInput('scene', model(true));
    await fixture.whenStable();
    expect(factory).toHaveBeenCalledOnce();
    expect(fixture.nativeElement.textContent).toContain('10 pasageri/min');
    expect(fixture.nativeElement.textContent).toContain('Solicitat');
    expect(fixture.nativeElement.textContent).toContain('4.2 min');
    expect(
      (fixture.nativeElement as HTMLElement).querySelector<HTMLSelectElement>(
        'select[id^="twin-zone-"]',
      )?.value,
    ).toBe(TERMINAL_MAP_ZONES[0].entityId);
    expect(first.checkpoints![0].capacityPerMinute).toBe(7);
  });
  it('provides selectable SVG zones and the same inspector after GLB loading fails', async () => {
    let reject!: (reason: Error) => void;
    const renderer: TwinRenderer = {
      ready: new Promise<void>((_, fail) => {
        reject = fail;
      }),
      setScene: vi.fn(),
      resetCamera: vi.fn(),
      dispose: vi.fn(),
    };
    const fixture = mount(() => renderer);
    fixture.componentRef.setInput('scene', model());
    await fixture.whenStable();
    expect(fixture.nativeElement.textContent).toContain('Se încarcă');
    reject(new Error('invalid GLB'));
    await vi.waitFor(() => {
      fixture.detectChanges();
      expect((fixture.nativeElement as HTMLElement).querySelector('svg')).toBeTruthy();
    });
    const svg = (fixture.nativeElement as HTMLElement).querySelector('svg')!;
    expect(svg.querySelectorAll('g[role="button"]')).toHaveLength(11);
    svg.querySelector('g')!.dispatchEvent(new MouseEvent('click', { bubbles: true }));
    await fixture.whenStable();
    expect(fixture.nativeElement.textContent).toContain('7 pasageri/min');
    expect(renderer.dispose).toHaveBeenCalledOnce();
    fixture.componentRef.setInput('scene', model(true));
    await fixture.whenStable();
    expect(fixture.nativeElement.textContent).toContain('10 pasageri/min');
  });
  it('pauses and resumes the same renderer for the optional 2D map and falls back on performance failure', async () => {
    let fail!: (reason?: TwinFailureReason) => void;
    const renderer: TwinRenderer = {
      setScene: vi.fn(),
      resetCamera: vi.fn(),
      dispose: vi.fn(),
      setActive: vi.fn(),
    };
    const factory = vi.fn((_host: HTMLElement, onError: (reason?: TwinFailureReason) => void) => {
      fail = onError;
      return renderer;
    });
    const fixture = mount(factory);
    fixture.componentRef.setInput('scene', model());
    await fixture.whenStable();
    const button = (text: string) =>
      [...(fixture.nativeElement as HTMLElement).querySelectorAll('button')].find(
        (b) => b.textContent?.trim() === text,
      )!;
    button('Hartă 2D').click();
    await fixture.whenStable();
    expect(renderer.setActive).toHaveBeenLastCalledWith(false);
    button('3D').click();
    await fixture.whenStable();
    expect(renderer.setActive).toHaveBeenLastCalledWith(true);
    expect(factory).toHaveBeenCalledOnce();
    fail('performance');
    await fixture.whenStable();
    expect(fixture.nativeElement.textContent).toContain('pragul de 30 FPS');
    expect((fixture.nativeElement as HTMLElement).querySelector('svg')).toBeTruthy();
  });
});

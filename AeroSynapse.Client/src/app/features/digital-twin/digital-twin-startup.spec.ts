import { TestBed } from '@angular/core/testing';
import { DigitalTwin, TWIN_RENDERER_FACTORY } from './digital-twin';
import { TwinRenderer } from './twin-model';
import { datasetFixture } from './heatmap.testing';

const renderer = (): TwinRenderer => ({
  setScene: vi.fn(),
  setHeatmap: vi.fn(),
  setActive: vi.fn(),
  resetCamera: vi.fn(),
  dispose: vi.fn(),
});
const modeButton = (host: HTMLElement, label: string) =>
  [...host.querySelectorAll<HTMLButtonElement>('button')].find(
    (button) => button.textContent?.trim() === label,
  )!;

describe('Responsive map startup', () => {
  it('shows the map and current data without starting graphics, then loads 3D only on request', async () => {
    const graphics = renderer();
    const factory = vi.fn(() => graphics);
    TestBed.configureTestingModule({
      imports: [DigitalTwin],
      providers: [{ provide: TWIN_RENDERER_FACTORY, useValue: factory }],
    });
    const fixture = TestBed.createComponent(DigitalTwin);
    fixture.componentRef.setInput('scene', { aircraft: [], heatmap: datasetFixture() });
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('app-terminal-map')).not.toBeNull();
    expect(host.querySelector('.overlay')).toBeNull();
    expect(host.querySelector('[data-testid="heatmap-queue-kpi"]')).not.toBeNull();
    expect(factory).not.toHaveBeenCalled();
    modeButton(host, '3D').click();
    await fixture.whenStable();
    expect(factory).toHaveBeenCalledOnce();
    expect(graphics.setScene).toHaveBeenCalled();
    modeButton(host, 'Hartă 2D').click();
    await fixture.whenStable();
    expect(graphics.setActive).toHaveBeenLastCalledWith(false);
    fixture.destroy();
  });

  it('allows returning to the map while graphics are loading and disposes a late result', async () => {
    const graphics = renderer();
    let finish!: (value: TwinRenderer) => void;
    let signal!: AbortSignal;
    const pending = new Promise<TwinRenderer>((resolve) => {
      finish = resolve;
    });
    const factory = vi.fn((_host: HTMLElement, _error: unknown, request: AbortSignal) => {
      signal = request;
      return pending;
    });
    TestBed.configureTestingModule({
      imports: [DigitalTwin],
      providers: [{ provide: TWIN_RENDERER_FACTORY, useValue: factory }],
    });
    const fixture = TestBed.createComponent(DigitalTwin);
    fixture.componentRef.setInput('scene', { aircraft: [] });
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    modeButton(host, '3D').click();
    fixture.detectChanges();
    expect(host.querySelector('.overlay')).not.toBeNull();
    modeButton(host, 'Hartă 2D').click();
    fixture.detectChanges();
    expect(signal.aborted).toBe(true);
    expect(host.querySelector('.overlay')).toBeNull();
    finish(graphics);
    await fixture.whenStable();
    expect(graphics.dispose).toHaveBeenCalledOnce();
    expect(graphics.setScene).not.toHaveBeenCalled();
    expect(host.querySelector('.fallback-status')).toBeNull();
    fixture.destroy();
  });

  it('exposes scene controls only after the renderer and its scene are ready', async () => {
    let finishFactory!: (value: TwinRenderer) => void;
    let finishScene!: () => void;
    const graphics: TwinRenderer = {
      ...renderer(),
      ready: new Promise<void>((resolve) => {
        finishScene = resolve;
      }),
      setDisplayMode: vi.fn(),
      setCameraView: vi.fn(),
    };
    TestBed.configureTestingModule({
      imports: [DigitalTwin],
      providers: [
        {
          provide: TWIN_RENDERER_FACTORY,
          useValue: () =>
            new Promise<TwinRenderer>((resolve) => {
              finishFactory = resolve;
            }),
        },
      ],
    });
    const fixture = TestBed.createComponent(DigitalTwin);
    fixture.componentRef.setInput('scene', { aircraft: [] });
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    modeButton(host, '3D').click();
    fixture.detectChanges();

    const expectLoadingControls = () => {
      expect(host.querySelector('.view-select')).toBeNull();
      expect(host.querySelector('.zone-control')).toBeNull();
      expect(host.querySelector('.layer-settings')).toBeNull();
      expect(modeButton(host, 'Hartă 2D').disabled).toBe(false);
    };
    expectLoadingControls();
    finishFactory(graphics);
    await fixture.whenStable();
    expect(graphics.setScene).toHaveBeenCalled();
    expectLoadingControls();

    finishScene();
    await vi.waitFor(() => {
      fixture.detectChanges();
      expect(host.querySelector('.view-select')).not.toBeNull();
    });
    const view = host.querySelector<HTMLSelectElement>('.view-select select')!;
    expect(view).not.toBeNull();
    expect(host.querySelector('.layer-settings')).not.toBeNull();
    view.value = 'exterior';
    view.dispatchEvent(new Event('change'));
    await fixture.whenStable();
    expect(graphics.setDisplayMode).toHaveBeenLastCalledWith('exterior');
    expect(host.querySelector('.viewport.exterior-view')).not.toBeNull();
    fixture.destroy();
  });

  it('cancels pending graphics initialization when navigating away', async () => {
    const graphics = renderer();
    let finish!: (value: TwinRenderer) => void;
    let signal!: AbortSignal;
    const pending = new Promise<TwinRenderer>((resolve) => {
      finish = resolve;
    });
    TestBed.configureTestingModule({
      imports: [DigitalTwin],
      providers: [
        {
          provide: TWIN_RENDERER_FACTORY,
          useValue: (_host: HTMLElement, _error: unknown, request: AbortSignal) => {
            signal = request;
            return pending;
          },
        },
      ],
    });
    const fixture = TestBed.createComponent(DigitalTwin);
    fixture.componentRef.setInput('scene', { aircraft: [] });
    await fixture.whenStable();
    modeButton(fixture.nativeElement, '3D').click();
    fixture.detectChanges();
    fixture.destroy();
    expect(signal.aborted).toBe(true);
    finish(graphics);
    await Promise.resolve();
    expect(graphics.dispose).toHaveBeenCalledOnce();
    expect(graphics.setScene).not.toHaveBeenCalled();
  });
});

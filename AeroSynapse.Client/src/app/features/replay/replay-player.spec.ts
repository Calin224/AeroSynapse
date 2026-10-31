import { TestBed } from '@angular/core/testing';
import { ReplayPlayer } from './replay-player';
import { TWIN_RENDERER_FACTORY } from '../digital-twin/digital-twin';
import { TwinRenderer } from '../digital-twin/twin-model';
import { prepareReplay } from './replay.models';
import { ids, replayData, replayEnvelope } from './replay.testing';

describe('Single replay cursor for animation, KPI and events', () => {
  let renderer: TwinRenderer;
  let raf: FrameRequestCallback;
  beforeEach(async () => {
    vi.stubGlobal(
      'crypto',
      (await vi.importActual<{ webcrypto: Crypto }>('node:crypto')).webcrypto,
    );
    vi.stubGlobal(
      'requestAnimationFrame',
      vi.fn((callback: FrameRequestCallback) => {
        raf = callback;
        return 10;
      }),
    );
    vi.stubGlobal('cancelAnimationFrame', vi.fn());
    renderer = {
      setScene: vi.fn(),
      setHeatmap: vi.fn(),
      setPassengers: vi.fn(),
      resetCamera: vi.fn(),
      dispose: vi.fn(),
    };
    TestBed.configureTestingModule({
      imports: [ReplayPlayer],
      providers: [{ provide: TWIN_RENDERER_FACTORY, useValue: () => renderer }],
    });
  });
  afterEach(() => vi.unstubAllGlobals());
  async function component(data = replayData()) {
    const prepared = await prepareReplay(await replayEnvelope(data));
    const fixture = TestBed.createComponent(ReplayPlayer);
    fixture.componentRef.setInput('replay', prepared);
    await fixture.whenStable();
    [
      ...(fixture.nativeElement as HTMLElement).querySelectorAll<HTMLButtonElement>(
        'app-digital-twin button',
      ),
    ]
      .find((button) => button.textContent?.trim() === '3D')!
      .click();
    await fixture.whenStable();
    return fixture;
  }
  it('repeats the same values at T and sends the same snapshot anchor to both renderers without a second timeline', async () => {
    const fixture = await component(),
      view = fixture.componentInstance,
      host = fixture.nativeElement as HTMLElement;
    view.seek(2.75);
    await fixture.whenStable();
    const queued = host.querySelector('[data-kpi="queued"]')!.textContent;
    expect(queued).toBe('8');
    expect(vi.mocked(renderer.setHeatmap!).mock.calls.at(-1)![0]).toMatchObject({
      minute: 2,
      runId: ids.run,
    });
    expect(vi.mocked(renderer.setPassengers!).mock.calls.at(-1)![0]).toMatchObject({
      timeMinutes: 2.75,
      snapshotMinute: 2,
      interpolateAggregates: false,
    });
    view.seek(6);
    view.seek(2.75);
    await fixture.whenStable();
    expect(host.querySelector('[data-kpi="queued"]')!.textContent).toBe(queued);
    expect(host.querySelector('app-passenger-playback')).toBeNull();
    const values = host.querySelector('app-heatmap-controls')!;
    expect(values.querySelector('table')).toBeTruthy();
    expect(values.querySelector('input[type="range"]')).toBeNull();
    expect(values.querySelector('button')).toBeNull();
  });
  it('clicks a timeline event at its exact fractional minute, with KPI from that window and the captured zone', async () => {
    const fixture = await component(),
      host = fixture.nativeElement as HTMLElement;
    (host.querySelector('.event-marker') as HTMLButtonElement).click();
    await fixture.whenStable();
    expect(fixture.componentInstance.time()).toBe(2.5);
    expect(fixture.componentInstance.frame().anchor).toBe(2);
    expect(fixture.componentInstance.selectedEvent()).toBe(ids.event);
    expect(fixture.componentInstance.zoneId()).toBe(ids.cp);
    expect(host.querySelector('.event-row')!.textContent).toContain('1 min');
    expect(host.querySelector('[data-kpi="queued"]')!.textContent).toBe('8');
  });
  it('does not render old KPI or heatmap observations in a missing window', async () => {
    const data = replayData();
    data.samples = data.samples.filter((row) => row.minute !== 2);
    const fixture = await component(data);
    fixture.componentInstance.seek(3);
    await fixture.whenStable();
    expect(
      (fixture.nativeElement as HTMLElement).querySelector('[data-kpi="queued"]')!.textContent,
    ).toBe('—');
    expect(vi.mocked(renderer.setHeatmap!).mock.calls.at(-1)![0]).toMatchObject({
      minute: 2,
      zones: [],
    });
    expect(vi.mocked(renderer.setPassengers!).mock.calls.at(-1)![0]).toMatchObject({
      snapshotMinute: 2,
    });
  });
  it('uses one clock for speed and animation, then resets cursor, filters and playback on runId change', async () => {
    const fixture = await component(),
      view = fixture.componentInstance;
    view.seek(2);
    view.zoneId.set(ids.cp);
    view.setSpeed(16);
    view.toggle();
    raf(1000);
    raf(2000);
    await fixture.whenStable();
    expect(view.time()).toBeCloseTo(2 + 16 / 60);
    expect(vi.mocked(renderer.setPassengers!).mock.calls.at(-1)![0]?.timeMinutes).toBe(view.time());
    fixture.componentRef.setInput(
      'replay',
      await prepareReplay(await replayEnvelope(replayData(ids.otherRun))),
    );
    await fixture.whenStable();
    expect(view.time()).toBe(0);
    expect(view.speed()).toBe(1);
    expect(view.playing()).toBe(false);
    expect(view.zoneId()).toBe('');
    expect(view.selectedEvent()).toBe('');
    expect(cancelAnimationFrame).toHaveBeenCalledWith(10);
    expect(vi.mocked(renderer.setHeatmap!).mock.calls.at(-1)![0]).toMatchObject({
      runId: ids.otherRun,
      minute: 0,
    });
    expect(vi.mocked(renderer.setPassengers!).mock.calls.at(-1)![0]?.replay.runId).toBe(
      ids.otherRun,
    );
    fixture.destroy();
    expect(renderer.dispose).toHaveBeenCalled();
  });
  it('can explicitly preserve a presentation cursor when switching the compared run', async () => {
    const fixture = await component();
    fixture.componentInstance.seek(4.25);
    fixture.componentRef.setInput('initialMinute', 4.25);
    fixture.componentRef.setInput(
      'replay',
      await prepareReplay(await replayEnvelope(replayData(ids.otherRun))),
    );
    await fixture.whenStable();
    expect(fixture.componentInstance.time()).toBe(4.25);
    expect(vi.mocked(renderer.setPassengers!).mock.calls.at(-1)![0]).toMatchObject({
      timeMinutes: 4.25,
      snapshotMinute: 4,
    });
  });
  it('keeps incomplete overview totals unknown and distinguishes zero waits from missing waits', async () => {
    const data = replayData();
    data.samples = data.samples.filter(
      (sample) => !(sample.minute === 2 && sample.zoneId === ids.otherCp),
    );
    for (const sample of data.samples.filter((sample) => sample.minute === 4)) {
      sample.kpi!.meanWaitMinutes = 0;
    }
    const fixture = await component(data),
      view = fixture.componentInstance;
    expect(view.overview()).toMatchObject({ queued: 20, maxWait: null, available: 2, total: 2 });
    view.seek(2.5);
    expect(view.overview()).toEqual({ queued: null, maxWait: null, available: 1, total: 2 });
    view.seek(4);
    expect(view.overview()).toEqual({ queued: 12, maxWait: 0, available: 2, total: 2 });
    view.zoneId.set(ids.cp);
    expect(view.overview().queued).toBe(12); // The overview is explicitly for all captured zones.
  });
  it('groups coincident timeline markers without removing events or comparison-view access', async () => {
    const data = replayData();
    data.captured.scenario.inputs.events.push({
      ...data.captured.scenario.inputs.events[0],
      id: ids.mark,
    });
    const fixture = await component(data),
      view = fixture.componentInstance;
    expect(view.events()).toHaveLength(2);
    expect(view.eventGroups()).toHaveLength(1);
    expect((fixture.nativeElement as HTMLElement).querySelectorAll('.event-row')).toHaveLength(2);
    fixture.componentRef.setInput('compact', true);
    await fixture.whenStable();
    expect(view.eventGroups()).toHaveLength(2);
    expect((fixture.nativeElement as HTMLElement).querySelectorAll('.event-marker')).toHaveLength(
      2,
    );
  });
});

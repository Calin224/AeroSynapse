import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { API_BASE_URL } from '../../core/http/api-client';
import { RunHeatmap } from './run-heatmap';
import { TWIN_RENDERER_FACTORY } from './digital-twin';
import { datasetFixture, storedFixture } from './heatmap.testing';
import { HeatmapFrame } from './heatmap-model';
import { TwinRenderer } from './twin-model';
import { OPERATION_REQUEST_TIMEOUT_MS } from '../../core/http/operation-request';

describe('Run heatmap REST and live integration', () => {
  let http: HttpTestingController;
  let renderer: TwinRenderer;
  beforeEach(() => {
    renderer = { setScene: vi.fn(), setHeatmap: vi.fn(), resetCamera: vi.fn(), dispose: vi.fn() };
    TestBed.configureTestingModule({
      imports: [RunHeatmap],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: API_BASE_URL, useValue: '/api' },
        { provide: TWIN_RENDERER_FACTORY, useValue: () => renderer },
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());
  const latest = () => vi.mocked(renderer.setHeatmap!).mock.calls.at(-1)![0] as HeatmapFrame;
  const start3D = async (fixture: ComponentFixture<RunHeatmap>) => {
    [
      ...(fixture.nativeElement as HTMLElement).querySelectorAll<HTMLButtonElement>(
        'app-digital-twin button',
      ),
    ]
      .find((button) => button.textContent?.trim() === '3D')!
      .click();
    await fixture.whenStable();
  };
  it('keeps baseline values readable after comparison timeout and allows an explicit retry', async () => {
    const fixture = TestBed.createComponent(RunHeatmap);
    fixture.componentRef.setInput('run', storedFixture());
    await fixture.whenStable();
    await start3D(fixture);
    vi.useFakeTimers();
    try {
      const host = fixture.nativeElement as HTMLElement;
      host.querySelector<HTMLButtonElement>('header button')!.click();
      const pending = http.expectOne('/api/runs/base-run/recommendations');
      await vi.advanceTimersByTimeAsync(OPERATION_REQUEST_TIMEOUT_MS);
      fixture.detectChanges();
      expect(pending.cancelled).toBe(true);
      expect(host.textContent).toContain('Timeout');
      expect(latest().runId).toBe('base-run');
      const retry = host.querySelector<HTMLButtonElement>('header button')!;
      expect(retry.disabled).toBe(false);
      retry.click();
      http.expectOne('/api/runs/base-run/recommendations').flush([]);
      fixture.destroy();
    } finally {
      vi.useRealTimers();
    }
  });
  it('loads the recommendation pair and switches variant without reloading the scene', async () => {
    const fixture = TestBed.createComponent(RunHeatmap),
      data = datasetFixture();
    fixture.componentRef.setInput('run', storedFixture());
    await fixture.whenStable();
    await start3D(fixture);
    const host = fixture.nativeElement as HTMLElement;
    host.querySelector('header button')!.dispatchEvent(new MouseEvent('click'));
    const recommendation = http.expectOne('/api/runs/base-run/recommendations');
    expect(recommendation.request.method).toBe('GET');
    recommendation.flush([
      { recommendation: { baselineRunId: 'base-run' }, recommendedRunId: 'optimized-run' },
    ]);
    http.expectOne('/api/runs/base-run').flush(storedFixture());
    http.expectOne('/api/runs/optimized-run').flush(storedFixture(data.optimized!));
    await fixture.whenStable();
    [...host.querySelectorAll('button')]
      .find((b) => b.textContent?.trim() === 'Optimizat')!
      .click();
    await fixture.whenStable();
    expect(latest()).toMatchObject({
      runId: 'optimized-run',
      minute: 2,
      kpis: { queuedPassengers: 3, capacityPerMinute: 14 },
    });
    expect(renderer.dispose).not.toHaveBeenCalled();
  });
  it('rejects an incompatible layout and cancels stale requests when the selected run changes', async () => {
    const fixture = TestBed.createComponent(RunHeatmap),
      data = datasetFixture();
    fixture.componentRef.setInput('run', storedFixture());
    await fixture.whenStable();
    await start3D(fixture);
    const compare = () =>
      (fixture.nativeElement as HTMLElement)
        .querySelector('header button')!
        .dispatchEvent(new MouseEvent('click'));
    compare();
    http
      .expectOne('/api/runs/base-run/recommendations')
      .flush([
        { recommendation: { baselineRunId: 'base-run' }, recommendedRunId: 'optimized-run' },
      ]);
    http.expectOne('/api/runs/base-run').flush(storedFixture());
    http
      .expectOne('/api/runs/optimized-run')
      .flush(storedFixture({ ...data.optimized!, layoutId: 'other-layout' }));
    await fixture.whenStable();
    expect(fixture.nativeElement.textContent).toContain('aceeași versiune de layout');
    expect(latest().runId).toBe('base-run');
    compare();
    const pending = http.expectOne('/api/runs/base-run/recommendations');
    fixture.componentRef.setInput('run', storedFixture(data.optimized!));
    await fixture.whenStable();
    expect(pending.cancelled).toBe(true);
    expect(latest().runId).toBe('optimized-run');
  });
  it('follows observed live frames, preserves history on completion and keeps an explicitly selected minute', async () => {
    const fixture = TestBed.createComponent(RunHeatmap),
      data = datasetFixture();
    fixture.componentRef.setInput('run', {
      ...storedFixture(),
      status: 'running',
      resultsValid: false,
      snapshots: [],
    });
    await fixture.whenStable();
    await start3D(fixture);
    const progress = (minute: number, queued: number) => ({
      runId: 'base-run',
      status: 'running',
      revision: minute,
      resultsValid: false,
      simulationMinute: minute,
      snapshot: {
        totalZones: 1,
        zones: [
          {
            zoneId: data.baseline.capacities[0].zoneId,
            queuedPassengers: queued,
            processedPassengers: minute * 3,
          },
        ],
      },
    });
    fixture.componentRef.setInput('progress', progress(1, 6));
    await fixture.whenStable();
    expect(latest()).toMatchObject({ minute: 1, kpis: { queuedPassengers: 6 } });
    fixture.componentRef.setInput('progress', progress(2, 21));
    await fixture.whenStable();
    const range = (fixture.nativeElement as HTMLElement).querySelector(
      'input[type="range"]',
    ) as HTMLInputElement;
    range.value = '0';
    range.dispatchEvent(new Event('input'));
    await fixture.whenStable();
    fixture.componentRef.setInput('progress', progress(3, 30));
    await fixture.whenStable();
    expect(latest()).toMatchObject({ minute: 1, kpis: { queuedPassengers: 6 } });
    fixture.componentRef.setInput(
      'run',
      storedFixture({
        ...data.baseline,
        snapshots: [
          {
            zoneId: data.baseline.capacities[0].zoneId,
            elapsedMinutes: 3,
            queuedPassengers: 30,
            processedPassengers: 9,
          },
        ],
      }),
    );
    await fixture.whenStable();
    expect(latest().minute).toBe(1);
    expect(range.max).toBe('2');
  });
});

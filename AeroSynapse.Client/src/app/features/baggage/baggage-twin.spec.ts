import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { routes } from '../../app.routes';
import { apiErrorInterceptor } from '../../core/http/api-error.interceptor';
import { NotFound } from '../../shared/not-found';
import { BaggageTwin } from './baggage-twin';
import {
  bagPosition,
  BaggageRequest,
  BaggageResult,
  demoRequest,
  heatColor,
  RESOURCE_IDS,
} from './baggage.models';
import { BAGGAGE_RENDERER } from './baggage-view';

function result(request = demoRequest()): BaggageResult {
  const resources = RESOURCE_IDS.map((resourceId) => ({
    resourceId,
    ...request.resources[resourceId],
    queued: 0,
    inService: 0,
    completed: 0,
    stopped: false,
    peakQueue: 0,
    peakBusy: 0,
    totalWaitingMinutes: 0,
    meanWaitingMinutes: 0,
    utilization: 0,
  }));
  return {
    contractVersion: '1.0',
    engine: 'simpy-baggage-1.0',
    synthetic: true,
    populationFingerprint: 'same-synthetic-population',
    request,
    kpis: {
      unitCount: request.unitCount,
      loadedCount: 0,
      backlogCount: request.unitCount,
      throughputPerMinute: 0,
      totalWaitingMinutes: 0,
      meanWaitingMinutes: 0,
      totalDelayMinutes: 0,
      meanDelayMinutes: 0,
      lateCount: 0,
      divertedCount: 0,
    },
    resources,
    units: [],
    snapshots: Array.from({ length: request.durationMinutes + 1 }, (_, minute) => ({
      minute,
      loadedCount: 0,
      resources,
    })),
  };
}

describe('Synthetic baggage twin', () => {
  let http: HttpTestingController;
  beforeEach(() => {
    window.__AEROSYNAPSE_CONFIG__ = {
      apiBaseUrl: '/api',
      intelligenceBaseUrl: '/intelligence',
      baggageTwinEnabled: true,
    };
    TestBed.configureTestingModule({
      providers: [
        provideRouter(routes),
        provideHttpClient(withInterceptors([apiErrorInterceptor])),
        provideHttpClientTesting(),
        {
          provide: BAGGAGE_RENDERER,
          useValue: () => {
            throw new Error('No WebGL');
          },
        },
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => {
    http.verify();
    delete window.__AEROSYNAPSE_CONFIG__;
  });

  it('uses an opt-in lazy route and makes no baggage requests when disabled', async () => {
    window.__AEROSYNAPSE_CONFIG__!.baggageTwinEnabled = false;
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/baggage', NotFound);
    expect(harness.routeNativeElement?.textContent).toContain('Pagina nu există');
    http.expectNone((request) => request.url.includes('baggage'));
  });

  it('compares the same population and changes only candidate incident and added resources', () => {
    const fixture = TestBed.createComponent(BaggageTwin);
    fixture.detectChanges();
    const component = fixture.componentInstance;
    component.extraSorting = 2;
    component.extraLoading = 1;
    component.run();
    const requests = http.match('/intelligence/baggage/v1/run');
    expect(requests).toHaveLength(2);
    const baseline = requests[0].request.body as BaggageRequest;
    const candidate = requests[1].request.body as BaggageRequest;
    expect(baseline.incident).toBeNull();
    expect(candidate.incident).toEqual(component.incident);
    expect(candidate.seed).toBe(baseline.seed);
    expect(candidate.resources.sorting.capacity).toBe(baseline.resources.sorting.capacity + 2);
    expect(candidate.resources.loading.capacity).toBe(baseline.resources.loading.capacity + 1);
    expect(component.draft.resources.sorting.capacity).toBe(3);
    requests[0].flush(result(baseline));
    requests[1].flush(result(candidate));
    fixture.detectChanges();
    const root: HTMLElement = fixture.nativeElement;
    expect(root.querySelector('[aria-label="KPI bagaje separate de securitate"]')).not.toBeNull();
    expect(root.querySelectorAll('.heat-cell')).toHaveLength(5);
    expect(root.querySelectorAll('.resource-report tbody tr')).toHaveLength(5);
    expect(component.minute()).toBe(60);
    const slider = root.querySelector<HTMLInputElement>('#baggage-minute')!;
    slider.value = '12';
    slider.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    expect(component.frame()?.minute).toBe(12);
    expect(root.textContent).toContain('Minut 12 / 60');
  });

  it('rejects comparison when fingerprints differ and does not display invented KPI', () => {
    const fixture = TestBed.createComponent(BaggageTwin);
    fixture.componentInstance.run();
    const requests = http.match('/intelligence/baggage/v1/run');
    requests[0].flush(result());
    requests[1].flush({ ...result(), populationFingerprint: 'different' });
    fixture.detectChanges();
    expect(fixture.componentInstance.results()).toBeNull();
    expect(fixture.nativeElement.textContent).toContain('Populațiile diferă');
  });

  it('reports a disabled backend and allows retry; leaving cancels pending requests', () => {
    const fixture = TestBed.createComponent(BaggageTwin);
    fixture.componentInstance.run();
    const requests = http.match('/intelligence/baggage/v1/run');
    requests[0].flush({}, { status: 404, statusText: 'Disabled' });
    expect(requests[1].cancelled).toBe(true);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('BAGGAGE_TWIN_ENABLED=true');
    expect(fixture.componentInstance.busy()).toBe(false);
    fixture.componentInstance.run();
    const retry = http.match('/intelligence/baggage/v1/run');
    fixture.destroy();
    expect(retry.every((request) => request.cancelled)).toBe(true);
  });

  it('freezes 3D bag progress throughout an outage and uses the same heat scale for all scenarios', () => {
    const unit = {
      id: 'synthetic-bag-1',
      arrivalMinute: 0,
      nominalBelt: 'beltA' as const,
      belt: 'beltA' as const,
      diverted: false,
      loadedMinute: null,
      stages: [
        {
          resourceId: 'beltA' as const,
          enteredMinute: 0,
          startedMinute: 0,
          completedMinute: 7,
          serviceMinutes: 2,
          workSegments: [
            { startMinute: 0, endMinute: 1 },
            { startMinute: 6, endMinute: 7 },
          ],
        },
      ],
    };
    expect(bagPosition(unit, 2)).toEqual(bagPosition(unit, 5));
    expect(bagPosition(unit, 6.5)![0]).toBeGreaterThan(bagPosition(unit, 5)![0]);
    const frame = result().snapshots[0].resources[0];
    expect(heatColor({ ...frame, queued: 1 })).not.toBe(heatColor({ ...frame, queued: 6 }));
    expect(heatColor({ ...frame, stopped: true })).toBe('#9b4b62');
  });
});

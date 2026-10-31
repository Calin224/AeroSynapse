import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { apiErrorInterceptor } from '../../core/http/api-error.interceptor';
import { AirsideTwin } from './airside-twin';
import {
  AirsideOptimization,
  AirsideRequest,
  AirsideResult,
  airsideDemoRequest,
  validateAirside,
} from './airside.models';

function result(request = airsideDemoRequest()): AirsideResult {
  return {
    contractVersion: '1.0',
    engine: 'simpy-airside-1.0',
    synthetic: true,
    populationFingerprint: 'synthetic-same-population',
    request: structuredClone(request),
    kpis: {
      turnaroundCount: request.turnarounds.length,
      departedCount: 1,
      backlogCount: 3,
      readyCount: 1,
      meanTurnaroundMinutes: 25,
      meanWaitingMinutes: 7.5,
      meanReadinessDelayMinutes: 2,
      lateCount: 1,
      conflictCount: 2,
    },
    turnarounds: request.turnarounds.map((turn, index) => ({
      ...turn,
      standEnteredMinute: index === 0 ? 0 : null,
      handlingStartedMinute: index === 0 ? 0 : null,
      handlingCompletedMinute: index === 0 ? 20 : null,
      pushbackReadyMinute: index === 0 ? 20 : null,
      departedMinute: index === 0 ? 25 : null,
      waitingMinutes: 0,
      readinessDelayMinutes: 0,
      status: index === 0 ? 'departed' : 'pending',
      handlingTeamId: index === 0 ? 'team-1' : null,
      handlingVehicleId: index === 0 ? 'vehicle-1' : null,
      pushbackTeamId: index === 0 ? 'team-3' : null,
      tugId: index === 0 ? 'vehicle-3' : null,
    })),
    allocations: [
      {
        resourceType: 'stand',
        resourceId: 'stand-1',
        turnaroundId: 'turn-1',
        stage: 'stand',
        startMinute: 0,
        endMinute: 25,
      },
      {
        resourceType: 'team',
        resourceId: 'team-1',
        turnaroundId: 'turn-1',
        stage: 'handling',
        startMinute: 0,
        endMinute: 20,
      },
      {
        resourceType: 'member',
        resourceId: 'member-1',
        turnaroundId: 'turn-1',
        stage: 'handling',
        startMinute: 0,
        endMinute: 20,
      },
    ],
    conflicts: [
      {
        turnaroundId: 'turn-2',
        stage: 'handling',
        minute: 0,
        reason: 'synthetic_resource_wait',
        resourceIds: ['team-1'],
      },
    ],
    snapshots: Array.from({ length: request.durationMinutes + 1 }, (_, minute) => ({
      minute,
      departedCount: minute >= 25 ? 1 : 0,
      readyCount: 0,
      backlogCount: minute >= 25 ? 3 : 4,
    })),
  };
}
function optimized(request = airsideDemoRequest()): AirsideOptimization {
  return {
    status: 'optimal',
    method: 'cp-sat-airside-1.0',
    objective: 12,
    result: { ...result(request), engine: 'cp-sat-airside-1.0' },
    diagnostics: ['Synthetic allocation only.'],
  };
}

describe('Synthetic airside planner', () => {
  let http: HttpTestingController;
  beforeEach(() => {
    window.__AEROSYNAPSE_CONFIG__ = { apiBaseUrl: '/api', intelligenceBaseUrl: '/intelligence/' };
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([apiErrorInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => {
    http.verify();
    delete window.__AEROSYNAPSE_CONFIG__;
  });

  it('posts identical independent synthetic snapshots to run and optimize and displays server KPI', () => {
    const fixture = TestBed.createComponent(AirsideTwin);
    fixture.detectChanges();
    fixture.componentInstance.run();
    const baseline = http.expectOne('/intelligence/airside/v1/run');
    const optimizer = http.expectOne('/intelligence/airside/v1/optimize');
    const body = baseline.request.body as AirsideRequest;
    expect(baseline.request.method).toBe('POST');
    expect(body.synthetic).toBe(true);
    expect(optimizer.request.body).toEqual(body);
    expect(optimizer.request.body).not.toBe(body);
    expect(body).not.toBe(fixture.componentInstance.draft);
    expect(body.teams[1].available[0].startMinute).toBe(15);
    baseline.flush(result(body));
    optimizer.flush(optimized(body));
    fixture.detectChanges();
    const root: HTMLElement = fixture.nativeElement;
    expect(root.textContent).toContain('KPI AIRSIDE');
    expect(root.querySelector('.kpis')?.textContent).toContain('7.5 min');
    expect(root.querySelectorAll('.turnaround-results tbody tr')).toHaveLength(4);
    expect(root.querySelectorAll('.conflict-results tbody tr')).toHaveLength(1);
    expect(fixture.componentInstance.baseline()?.request).not.toBe(body);
    expect(fixture.componentInstance.busy()).toBe(false);
  });

  it('retains baseline and disables the optimized view when CP-SAT has no solution', () => {
    const fixture = TestBed.createComponent(AirsideTwin);
    fixture.componentInstance.run();
    http.expectOne('/intelligence/airside/v1/run').flush(result());
    http.expectOne('/intelligence/airside/v1/optimize').flush({
      status: 'infeasible',
      method: 'cp-sat-airside-1.0',
      objective: null,
      result: null,
      diagnostics: ['No compatible team in the requested window.'],
    });
    fixture.detectChanges();
    expect(fixture.componentInstance.baseline()).not.toBeNull();
    expect(fixture.componentInstance.selected()?.engine).toBe('simpy-airside-1.0');
    expect(fixture.nativeElement.textContent).toContain('No compatible team');
    expect(
      fixture.nativeElement.querySelector('[data-testid="airside-variant-optimized"]').disabled,
    ).toBe(true);
  });

  it('retains baseline when optimizer HTTP fails and allows retry', () => {
    const fixture = TestBed.createComponent(AirsideTwin);
    fixture.componentInstance.run();
    http.expectOne('/intelligence/airside/v1/run').flush(result());
    http
      .expectOne('/intelligence/airside/v1/optimize')
      .flush({}, { status: 503, statusText: 'Unavailable' });
    expect(fixture.componentInstance.baseline()).not.toBeNull();
    expect(fixture.componentInstance.optimizerError()).not.toBe('');
    fixture.componentInstance.run();
    http.expectOne('/intelligence/airside/v1/run').flush(result());
    http.expectOne('/intelligence/airside/v1/optimize').flush(optimized());
    expect(fixture.componentInstance.optimizerError()).toBe('');
    expect(fixture.componentInstance.optimization()?.status).toBe('optimal');
  });

  it('keeps a successful optimized plan when baseline HTTP fails and preserves the actual error', () => {
    const fixture = TestBed.createComponent(AirsideTwin);
    fixture.componentInstance.run();
    http
      .expectOne('/intelligence/airside/v1/run')
      .flush({}, { status: 404, statusText: 'Disabled' });
    http.expectOne('/intelligence/airside/v1/optimize').flush(optimized());
    fixture.detectChanges();
    expect(fixture.componentInstance.baseline()).toBeNull();
    expect(fixture.componentInstance.variant()).toBe('optimized');
    expect(fixture.componentInstance.selected()?.engine).toBe('cp-sat-airside-1.0');
    expect(fixture.componentInstance.error()).toContain('AIRSIDE_TWIN_ENABLED=true');
    expect(fixture.nativeElement.querySelector('[data-testid="airside-results"]')).not.toBeNull();
  });

  it('clears comparison after editing crew availability and sends the updated window', async () => {
    const fixture = TestBed.createComponent(AirsideTwin);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.componentInstance.run();
    http.expectOne('/intelligence/airside/v1/run').flush(result());
    http.expectOne('/intelligence/airside/v1/optimize').flush(optimized());
    fixture.detectChanges();
    const input: HTMLInputElement = fixture.nativeElement.querySelector(
      '[data-testid="team-2-start"]',
    );
    input.value = '0';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    await fixture.whenStable();
    expect(fixture.componentInstance.draft.teams[1].available[0].startMinute).toBe(0);
    expect(fixture.componentInstance.baseline()).toBeNull();
    expect(fixture.componentInstance.optimization()).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="airside-results"]')).toBeNull();
    expect(fixture.componentInstance.notice()).toContain('Configurația s-a schimbat');
    fixture.componentInstance.run();
    const baseline = http.expectOne('/intelligence/airside/v1/run');
    const optimizer = http.expectOne('/intelligence/airside/v1/optimize');
    expect(baseline.request.body.teams[1].available[0].startMinute).toBe(0);
    baseline.flush(result(baseline.request.body));
    optimizer.flush(optimized(optimizer.request.body));
  });

  it('cancels both pending requests on edits, explicit cancel, and leaving the page', () => {
    const fixture = TestBed.createComponent(AirsideTwin);
    fixture.componentInstance.run();
    const first = http.match((request) => request.url.includes('/airside/v1/'));
    fixture.componentInstance.draft.teams[1].available[0].startMinute = 0;
    fixture.componentInstance.changed();
    expect(first.every((request) => request.cancelled)).toBe(true);
    expect(fixture.componentInstance.busy()).toBe(false);
    fixture.componentInstance.run();
    const second = http.match((request) => request.url.includes('/airside/v1/'));
    fixture.componentInstance.cancel();
    expect(second.every((request) => request.cancelled)).toBe(true);
    expect(fixture.componentInstance.notice()).toContain('Cereri anulate');
    fixture.componentInstance.run();
    const third = http.match((request) => request.url.includes('/airside/v1/'));
    fixture.destroy();
    expect(third.every((request) => request.cancelled)).toBe(true);
  });

  it('rejects mismatched populations while leaving the baseline visible', () => {
    const fixture = TestBed.createComponent(AirsideTwin);
    fixture.componentInstance.run();
    http.expectOne('/intelligence/airside/v1/run').flush(result());
    const alternative = optimized();
    alternative.result!.populationFingerprint = 'another-population';
    http.expectOne('/intelligence/airside/v1/optimize').flush(alternative);
    expect(fixture.componentInstance.baseline()).not.toBeNull();
    expect(fixture.componentInstance.optimization()).toBeNull();
    expect(fixture.componentInstance.optimizerError()).toContain('Populațiile diferă');
  });

  it('uses returned allocations for the schematic and resource Gantt with explicit synthetic labels', () => {
    const fixture = TestBed.createComponent(AirsideTwin);
    fixture.componentInstance.run();
    http.expectOne('/intelligence/airside/v1/run').flush(result());
    http.expectOne('/intelligence/airside/v1/optimize').flush(optimized());
    fixture.detectChanges();
    const slider: HTMLInputElement = fixture.nativeElement.querySelector(
      '[data-testid="airside-minute"]',
    );
    slider.value = '12';
    slider.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    expect(fixture.componentInstance.frame()?.minute).toBe(12);
    expect(fixture.componentInstance.occupying('stand-1')).toBe('turn-1');
    expect(fixture.componentInstance.occupying('stand-2')).toBe('Liber');
    expect(fixture.nativeElement.querySelectorAll('.gantt .lane')).toHaveLength(3);
    const map: SVGElement = fixture.nativeElement.querySelector('[data-testid="airside-map"]');
    expect(map.textContent).toContain('turn-1');
    expect(map.textContent).toContain('fără coordonate reale');


  });

  it('rejects non-synthetic output instead of presenting KPI', () => {
    const fixture = TestBed.createComponent(AirsideTwin);
    fixture.componentInstance.run();
    http.expectOne('/intelligence/airside/v1/run').flush({ ...result(), synthetic: false });
    http.expectOne('/intelligence/airside/v1/optimize').flush(optimized());
    fixture.detectChanges();
    expect(fixture.componentInstance.error()).toContain('exclusiv sintetice');
    expect(fixture.nativeElement.querySelector('[data-testid="airside-results"]')).toBeNull();
  });

  it('offers typed editors for shared members, compatible stands, and resource additions/removal', () => {
    const fixture = TestBed.createComponent(AirsideTwin);
    const component = fixture.componentInstance;
    component.toggle(component.draft.teams[1].memberIds, 'member-1', true);
    expect(component.draft.teams[1].memberIds).toContain('member-1');
    component.addMember();
    expect(component.members()).toContain('member-7');
    component.addStand();
    component.addTeam();
    component.addVehicle();
    component.addTurnaround();
    expect(component.draft.stands).toHaveLength(3);
    expect(component.draft.teams[3].standIds).toContain('stand-3');
    component.removeStand('stand-3');
    expect(component.draft.teams[3].standIds).not.toContain('stand-3');
    component.removeStand('stand-1');
    expect(component.draft.stands).toHaveLength(2);
    component.remove('teams', 'team-4');
    component.remove('vehicles', 'vehicle-4');
    component.remove('turnarounds', 'turn-5');
    expect(validateAirside(component.draft)).toEqual([]);
  });

  it('validates backend minute bounds and refuses invalid windows before making requests', () => {
    const fixture = TestBed.createComponent(AirsideTwin);
    const request = fixture.componentInstance.draft;
    expect(validateAirside(request)).toEqual([]);
    request.turnarounds[0].handlingMinutes = 121;
    expect(validateAirside(request).join(' ')).toContain('handling 1–120');
    request.turnarounds[0].handlingMinutes = 120;
    request.turnarounds[0].pushbackMinutes = 61;
    expect(validateAirside(request)).not.toEqual([]);
    request.turnarounds[0].pushbackMinutes = 60;
    request.turnarounds[0].arrivalMinute = request.durationMinutes;
    expect(validateAirside(request)).not.toEqual([]);
    request.turnarounds[0].arrivalMinute = request.durationMinutes - 1;
    request.turnarounds[0].scheduledPushbackMinute = 240;
    expect(validateAirside(request)).toEqual([]);
    request.teams[0].available[0] = { startMinute: 20, endMinute: 10 };
    fixture.componentInstance.run();
    expect(fixture.componentInstance.error()).toContain('fereastra');
    http.expectNone((request) => request.url.includes('/airside/v1/'));
  });
});

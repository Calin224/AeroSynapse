import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ScenarioDetails } from './operations.models';
import { RunSettingsPanel } from './run-settings-panel';

const scenario: ScenarioDetails = {
  scenario: {
    id: 'scenario-1',
    code: 'demo',
    name: 'Demo',
    description: '',
    sceneId: 'scene-1',
    layoutVersionId: 'layout-1',
    revision: 7,
  },
  inputs: {
    startsAtUtc: '2026-10-14T10:00:00Z',
    durationMinutes: 10,
    seed: 42,
    timeZone: 'UTC',
    flights: [],
    populations: [],
    flows: [],
    staffAllocations: [],
    events: [],
    snapshotIntervalMinutes: 3,
  },
};

describe('Run snapshot settings', () => {
  let http: HttpTestingController;
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());

  function component(value = scenario) {
    const fixture = TestBed.createComponent(RunSettingsPanel);
    fixture.componentRef.setInput('scenario', value);
    fixture.detectChanges();
    return fixture;
  }

  it('loads the saved interval and defaults to one minute when switching to an unconfigured scenario', () => {
    const fixture = component();
    expect(fixture.componentInstance.interval).toBe(3);
    expect(fixture.nativeElement.textContent).toContain('Interval salvat: 3 minute virtuale');
    fixture.componentRef.setInput('scenario', {
      ...scenario,
      scenario: { ...scenario.scenario, id: 'scenario-2' },
      inputs: { ...scenario.inputs, snapshotIntervalMinutes: null },
    });
    fixture.detectChanges();
    expect(fixture.componentInstance.interval).toBe(1);
    expect(fixture.nativeElement.textContent).toContain('Interval salvat: 1 minut virtual');
    http.expectNone(() => true);
  });

  it('saves the selected interval with the current revision and publishes the updated scenario', () => {
    const fixture = component();
    const view = fixture.componentInstance;
    const changes: ScenarioDetails[] = [];
    const working: boolean[] = [];
    view.changed.subscribe((value) => changes.push(value));
    view.working.subscribe((value) => working.push(value));
    const slider = fixture.nativeElement.querySelector('input[type="range"]') as HTMLInputElement;
    slider.value = '5';
    slider.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('output').textContent).toContain(
      '5 minute virtuale',
    );
    fixture.nativeElement.querySelector('button').click();
    const request = http.expectOne('/api/scenarios/scenario-1/kpis/settings');
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({ revision: 7, snapshotIntervalMinutes: 5 });
    view.save();
    http.expectNone('/api/scenarios/scenario-1/kpis/settings');
    const saved = {
      ...scenario,
      scenario: { ...scenario.scenario, revision: 8 },
      inputs: { ...scenario.inputs, snapshotIntervalMinutes: 5 },
    };
    request.flush(saved);
    fixture.componentRef.setInput('scenario', saved);
    fixture.detectChanges();
    expect(changes).toEqual([saved]);
    expect(working).toEqual([true, false]);
    expect(view.saving()).toBe(false);
    expect(fixture.nativeElement.textContent).toContain('Interval salvat: 5 minute virtuale');
    expect(fixture.nativeElement.textContent).toContain('salvat pentru următoarea rulare');
    view.interval = 2;
    view.save();
    const next = http.expectOne('/api/scenarios/scenario-1/kpis/settings');
    expect(next.request.body).toEqual({ revision: 8, snapshotIntervalMinutes: 2 });
    next.flush({
      ...saved,
      scenario: { ...saved.scenario, revision: 9 },
      inputs: { ...saved.inputs, snapshotIntervalMinutes: 2 },
    });
  });

  it('keeps the saved value and the draft separate after a rejected save and releases the run controls', () => {
    const fixture = component();
    const view = fixture.componentInstance;
    const changes = vi.fn();
    const working: boolean[] = [];
    view.changed.subscribe(changes);
    view.working.subscribe((value) => working.push(value));
    view.interval = 5;
    view.save();
    http
      .expectOne('/api/scenarios/scenario-1/kpis/settings')
      .flush({}, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();
    expect(view.interval).toBe(5);
    expect(fixture.nativeElement.textContent).toContain('Interval salvat: 3 minute virtuale');
    expect(changes).not.toHaveBeenCalled();
    expect(working).toEqual([true, false]);
    expect(view.saving()).toBe(false);
    expect(view.message()).not.toBe('');
  });

  it('prevents writes when editing is disabled', async () => {
    const fixture = component();
    fixture.componentRef.setInput('disabled', true);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector('input[type="range"]').disabled).toBe(true);
    expect(fixture.nativeElement.querySelector('button').disabled).toBe(true);
    fixture.componentInstance.save();
    http.expectNone(() => true);
  });
});

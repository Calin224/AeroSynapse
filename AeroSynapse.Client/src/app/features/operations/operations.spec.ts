import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { ActivatedRoute, Router, provideRouter, convertToParamMap } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { SessionState } from '../../core/auth/session-state';
import { Scenario } from '../scenario/scenario';
import { noWebGl, SimulationPanelStub, stubScenarioPanels } from '../scenario/scenario.testing';
import { ids, towerFixture } from '../control-tower/tower.testing';

const data = towerFixture();
data.scenario.scenario.name = 'Baseline';
data.scenario.scenario.code = 'BASE';
const alternative = structuredClone(data.scenario);
alternative.scenario.id = ids.otherRun;
alternative.scenario.name = 'Trafic redus';
alternative.scenario.code = 'ALT';
const catalog = [data.scenario.scenario, alternative.scenario];

describe('Scenario and run controls inside Control Tower', () => {
  let http: HttpTestingController;
  let params: BehaviorSubject<ReturnType<typeof convertToParamMap>>;
  beforeEach(() => {
    params = new BehaviorSubject(convertToParamMap({}));
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        {
          provide: ActivatedRoute,
          useValue: {
            queryParamMap: params,
            get snapshot() {
              return { queryParamMap: params.value };
            },
          },
        },
        noWebGl,
      ],
    });
    stubScenarioPanels();
    http = TestBed.inject(HttpTestingController);
    TestBed.inject(SessionState).session.set({
      username: 'operator.demo',
      roles: ['Operator'],
      expiresAtUtc: '2099-01-01T00:00:00Z',
    });
    vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
  });
  afterEach(() => http.verify());
  function component(selected = data.scenario) {
    const fixture = TestBed.createComponent(Scenario);
    fixture.detectChanges();
    http.expectOne('/api/scenarios').flush(catalog);
    http.expectOne(`/api/scenarios/${selected.scenario.id}`).flush(selected);
    http.expectOne(`/api/scenes/${ids.scene}/layout-versions/${ids.layout}`).flush(data.layout);
    http.expectOne(`/api/scenarios/${selected.scenario.id}/runs?limit=100`).flush([]);
    fixture.detectChanges();
    return fixture;
  }
  function launcher(fixture: ReturnType<typeof component>): SimulationPanelStub {
    return fixture.debugElement.query(By.directive(SimulationPanelStub)).componentInstance;
  }
  it('opens the exact saved scenario supplied in the link beside its map', () => {
    params.next(convertToParamMap({ scenarioId: ids.otherRun }));
    const fixture = component(alternative);
    expect(fixture.nativeElement.querySelector('#summary-title')?.textContent).toContain(
      alternative.scenario.name,
    );
    expect(launcher(fixture).scenarioId()).toBe(ids.otherRun);
    expect(fixture.componentInstance.store.context()?.scenario.scenario.id).toBe(ids.otherRun);
    expect(
      fixture.nativeElement.querySelector('.simulation-workspace app-digital-twin'),
    ).toBeTruthy();
    http.expectNone(`/api/scenarios/${ids.scenario}`);
  });
  it('filters the scenario gallery by name or code without opening another data owner', () => {
    const fixture = component(),
      view = fixture.componentInstance;
    view.browse();
    view.query.set('base');
    fixture.detectChanges();
    expect(view.visibleScenarios().map((item) => item.id)).toEqual([ids.scenario]);
    expect(fixture.nativeElement.querySelectorAll('.gallery .card')).toHaveLength(1);
    view.query.set('nimic');
    fixture.detectChanges();
    expect(view.visibleScenarios()).toEqual([]);
    expect(fixture.nativeElement.textContent).toContain('Niciun scenariu nu se potrivește');
    http.expectNone(() => true);
  });
  it.each(['resourcesBusy', 'staffingBusy', 'runSettingsBusy'] as const)(
    'blocks gallery, scenario selection and launches while %s is active',
    (activity) => {
      const fixture = component(),
        view = fixture.componentInstance;
      const navigate = vi.mocked(TestBed.inject(Router).navigate);
      navigate.mockClear();
      view[activity].set(true);
      view.browse();
      view.selectScenario(ids.otherRun);
      view.openRun(ids.otherRun);
      view.run();
      fixture.detectChanges();
      expect(view.galleryOpen()).toBe(false);
      expect(view.store.selected()?.scenario.id).toBe(ids.scenario);
      expect(navigate).not.toHaveBeenCalled();
      expect(launcher(fixture).start).not.toHaveBeenCalled();
      expect((fixture.nativeElement.querySelector('.run') as HTMLButtonElement).disabled).toBe(
        true,
      );
      expect(view.lockedExcept(view[activity])).toBe(false);
      expect(view.canLeave()).toBe(false);
      expect(fixture.nativeElement.querySelector('.create-link')?.getAttribute('href')).toBeNull();
      http.expectNone(() => true);
    },
  );
  it('keeps one guided creation destination and read-only panels for viewers', () => {
    TestBed.inject(SessionState).session.set({
      username: 'viewer.demo',
      roles: ['Viewer'],
      expiresAtUtc: '2099-01-01T00:00:00Z',
    });
    const fixture = component(),
      view = fixture.componentInstance;
    expect(fixture.nativeElement.querySelector('.create-link')).toBeNull();
    expect(fixture.nativeElement.querySelector('app-simulation-panel')).toBeNull();
    expect((fixture.nativeElement.querySelector('.run') as HTMLButtonElement).hidden).toBe(true);
    expect(view.lockedExcept(view.scheduleBusy)).toBe(true);
    TestBed.inject(SessionState).session.set({
      username: 'operator.demo',
      roles: ['Operator'],
      expiresAtUtc: '2099-01-01T00:00:00Z',
    });
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.create-link')?.getAttribute('href')).toBe(
      '/control-tower/configure',
    );
    expect(launcher(fixture)).toBeTruthy();
    http.expectNone((request) => request.method === 'POST');
  });
  it('opens recent runs through the shared route and keeps the six latest entries', () => {
    const fixture = component(),
      view = fixture.componentInstance;
    const runs = Array.from({ length: 8 }, (_, index) => ({
      ...data.run,
      id: `10000000-0000-0000-0000-00000000000${index + 1}`,
    }));
    view.store.runs.set(runs);
    view.store.run.set(runs[0]);
    fixture.detectChanges();
    const buttons = fixture.nativeElement.querySelectorAll(
      '.history-runs button',
    ) as NodeListOf<HTMLButtonElement>;
    expect(buttons).toHaveLength(6);
    expect(buttons[0].getAttribute('aria-pressed')).toBe('true');
    view.showPhase('prepare');
    buttons[1].click();
    expect(view.tab()).toBe('runs');
    expect(TestBed.inject(Router).navigate).toHaveBeenLastCalledWith(
      [],
      expect.objectContaining({
        queryParams: { runId: runs[1].id, zoneId: null, minute: null },
        queryParamsHandling: 'merge',
      }),
    );
  });
  it('delegates launch and recovery to the existing idempotent simulation command owner', () => {
    const fixture = component(),
      view = fixture.componentInstance;
    const panel = launcher(fixture);
    view.run();
    expect(panel.start).toHaveBeenCalledTimes(1);
    panel.error.set('Confirmarea nu a ajuns');
    panel.command.set({ key: 'existing-command' });
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.deck-error')?.textContent).toContain(
      'Confirmarea nu a ajuns',
    );
    expect(view.canResume()).toBe(true);
    view.resume();
    expect(panel.resume).toHaveBeenCalledTimes(1);
    http.expectNone((request) => request.method === 'POST');
  });
  it('updates the next-launch revision without replacing a historical run capture', () => {
    const fixture = component(),
      view = fixture.componentInstance;
    view.store.run.set(data.run);
    const captured = view.store.context();
    const updated = structuredClone(data.scenario);
    updated.scenario.revision++;
    view.scheduleChanged(updated);
    expect(view.store.selected()?.scenario.revision).toBe(updated.scenario.revision);
    expect(view.store.context()).toBe(captured);
    expect(view.store.scenarios()[0].revision).toBe(updated.scenario.revision);
    fixture.detectChanges();
    expect(launcher(fixture).scenario()?.revision).toBe(updated.scenario.revision);
    http.expectNone(() => true);
  });
});

import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Component, input } from '@angular/core';
import { By } from '@angular/platform-browser';
import { ScenarioDetails } from './operations.models';
import { StaffingPanel, StaffingSettings, StaffingProposal } from './staffing-panel';
import { StaffingEvaluationPanel } from './staffing-evaluation-panel';
import { apiErrorInterceptor } from '../../core/http/api-error.interceptor';

const scenario: ScenarioDetails = {
  scenario: {
    id: 'scenario-1',
    name: 'Demo',
    code: 'demo',
    description: '',
    sceneId: 'scene-1',
    layoutVersionId: 'layout-1',
    revision: 3,
  },
  inputs: {
    startsAtUtc: '2026-10-16T10:00:00Z',
    durationMinutes: 60,
    seed: 42,
    timeZone: 'UTC',
    flights: [],
    populations: [],
    flows: [],
    staffAllocations: [],
    events: [],
  },
};
const settings: StaffingSettings = {
  scenarioId: 'scenario-1',
  revision: 3,
  passengerProfilesActive: true,
  availableStaff: 4,
  staffPools: [{ terminalId: 'terminal-1', availableStaff: 4 }],
  checkpoints: [{ id: 'cp-1', terminalId: 'terminal-1', name: 'Control A', currentStaff: 2 }],
};

@Component({ selector: 'app-staffing-evaluation-panel', template: '' })
class EvaluationStub {
  readonly readOnly = input(false);
  readonly workflowStage = input<string | null>(null);
  readonly scenario = input.required<ScenarioDetails>();
  readonly active = input(false);
  readonly disabled = input(false);
  readonly proposal = input<StaffingProposal | null>(null);
}

describe('Staff redistribution proposals', () => {
  let http: HttpTestingController;
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([apiErrorInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    TestBed.overrideComponent(StaffingPanel, {
      remove: { imports: [StaffingEvaluationPanel] },
      add: { imports: [EvaluationStub] },
    });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());
  /** A solver reply without a configuration; tests override the outcome. */
  const unsolved = (status = 'timeout', solverStatus = 'UNKNOWN') => ({
    status,
    solverStatus,
    timedOut: status === 'timeout',
    score: null,
    assignments: [],
    constraints: {
      scenarioId: 'scenario-1',
      scenarioRevision: 3,
      availableStaff: 4,
      timeLimitSeconds: 5,
      staffPools: settings.staffPools,
      checkpoints: [],
    },
    wallTimeSeconds: 5,
  });
  function component() {
    const fixture = TestBed.createComponent(StaffingPanel);
    fixture.componentRef.setInput('scenario', structuredClone(scenario));
    fixture.componentRef.setInput('active', true);
    fixture.detectChanges();
    http.expectOne('/api/scenarios/scenario-1/staffing/settings').flush(settings);
    fixture.detectChanges();
    return fixture;
  }
  it('in the guided workflow starts from the staff budget as limit and folds the constraints away', () => {
    const fixture = TestBed.createComponent(StaffingPanel);
    fixture.componentRef.setInput('scenario', structuredClone(scenario));
    fixture.componentRef.setInput('active', true);
    fixture.componentRef.setInput('workflowStage', 'optimize');
    fixture.detectChanges();
    http.expectOne('/api/scenarios/scenario-1/staffing/settings').flush(settings);
    fixture.detectChanges();
    const view = fixture.componentInstance,
      element = fixture.nativeElement as HTMLElement;
    expect(view.rows[0].physicalLimit).toBe(4);
    expect(view.proposal()).not.toBeNull();
    expect(element.textContent).toContain('Control A · 2');
    expect(
      element.querySelector('details.advanced [aria-label="Limită fizică Control A"]'),
    ).not.toBeNull();
  });
  it('leads with current staff, folds the limits away and sends the current revision with all constraints', () => {
    const fixture = component();
    const view = fixture.componentInstance,
      element = fixture.nativeElement as HTMLElement;
    expect(element.textContent).toContain('Control A · 2 operatori');
    expect(element.textContent).not.toContain('Timeout solver');
    expect(
      element.querySelector('details.disclosure:not([open]) [aria-label="Limită fizică Control A"]'),
    ).not.toBeNull();
    expect(view.rows[0].physicalLimit).toBe(4);
    view.rows[0].physicalLimit = null;
    view.propose();
    expect(view.message()).toContain('Setări avansate');
    http.expectNone('/api/scenarios/scenario-1/staffing/proposals');
    view.rows[0].physicalLimit = 2;
    view.propose();
    const request = http.expectOne('/api/scenarios/scenario-1/staffing/proposals');
    expect(request.request.body).toEqual({
      revision: 3,
      availableStaff: 4,
      timeLimitSeconds: 5,
      checkpoints: [
        {
          checkpointId: 'cp-1',
          minimumStaff: 1,
          maximumStaff: 4,
          physicalLimit: 2,
          requiredOpen: true,
          closed: false,
          changeCost: 10,
        },
      ],
    });
    request.flush(unsolved());
    expect(view.result()?.score).toBeNull();
    expect(view.status(view.result()!)).toContain('fără o soluție');
    expect(fixture.nativeElement.textContent).not.toContain('SimPy');
  });
  it('cancels an obsolete request and clears the proposal when the scenario revision changes', () => {
    const fixture = component();
    const view = fixture.componentInstance;
    view.rows[0].physicalLimit = 2;
    view.propose();
    const previous = http.expectOne('/api/scenarios/scenario-1/staffing/proposals');
    fixture.componentRef.setInput('scenario', {
      ...scenario,
      scenario: { ...scenario.scenario, revision: 4 },
    });
    fixture.detectChanges();
    expect(previous.cancelled).toBe(true);
    expect(view.result()).toBeNull();
    http
      .expectOne('/api/scenarios/scenario-1/staffing/settings')
      .flush({ ...settings, revision: 4 });
    fixture.detectChanges();
    expect(view.rows[0].physicalLimit).toBe(4);
    expect(view.busy()).toBe(false);
  });
  it('keeps pending proposals and operator limits when the same scenario revision is refreshed', () => {
    const fixture = component();
    const view = fixture.componentInstance;
    view.rows[0].physicalLimit = 2;
    view.propose();
    const request = http.expectOne('/api/scenarios/scenario-1/staffing/proposals');
    fixture.componentRef.setInput('scenario', structuredClone(scenario));
    fixture.detectChanges();
    expect(request.cancelled).toBe(false);
    expect(view.busy()).toBe(true);
    expect(view.rows[0].physicalLimit).toBe(2);
    http.expectNone('/api/scenarios/scenario-1/staffing/settings');
    request.error(new ProgressEvent('error'));
    expect(view.busy()).toBe(false);
    fixture.destroy();
  });
  it('preserves the review component and refreshes staffing counts after its own approval', () => {
    const fixture = component();
    const view = fixture.componentInstance;
    const evaluation = fixture.debugElement.query(By.directive(EvaluationStub)).componentInstance;
    const approved = {
      ...scenario,
      scenario: { ...scenario.scenario, revision: 4 },
      inputs: {
        ...scenario.inputs,
        staffAllocations: [{ staffMemberId: 'staff-1', checkpointId: 'cp-1' }],
      },
    };
    view.approved(approved);
    fixture.componentRef.setInput('scenario', approved);
    fixture.detectChanges();
    expect(view.settings()?.revision).toBe(4);
    expect(view.rows[0].currentStaff).toBe(1);
    expect(fixture.debugElement.query(By.directive(EvaluationStub)).componentInstance).toBe(
      evaluation,
    );
    http.expectNone('/api/scenarios/scenario-1/staffing/settings');
    fixture.destroy();
  });
  it.each([
    { ...settings, scenarioId: 'another-scenario' },
    { ...settings, revision: 4 },
  ])(
    'rejects settings that do not match the displayed scenario revision: $scenarioId / $revision',
    (response) => {
      const fixture = TestBed.createComponent(StaffingPanel);
      fixture.componentRef.setInput('scenario', structuredClone(scenario));
      fixture.componentRef.setInput('active', true);
      fixture.detectChanges();
      http.expectOne('/api/scenarios/scenario-1/staffing/settings').flush(response);
      fixture.detectChanges();
      const view = fixture.componentInstance;
      expect(view.settings()).toBeNull();
      expect(view.rows).toEqual([]);
      expect(fixture.nativeElement.textContent).toContain('Scenariul s-a modificat');
      view.propose();
      http.expectNone('/api/scenarios/scenario-1/staffing/proposals');
    },
  );
  it('allows retrying a failed settings request without leaving the tab or sending duplicate requests', () => {
    const fixture = TestBed.createComponent(StaffingPanel);
    fixture.componentRef.setInput('scenario', structuredClone(scenario));
    fixture.componentRef.setInput('active', true);
    fixture.detectChanges();
    http
      .expectOne('/api/scenarios/scenario-1/staffing/settings')
      .flush({}, { status: 503, statusText: 'Unavailable' });
    fixture.detectChanges();
    const retry: HTMLButtonElement = fixture.nativeElement.querySelector('button');
    expect(retry.textContent).toContain('Reîncearcă');
    retry.click();
    fixture.componentInstance.loadSettings();
    http.expectOne('/api/scenarios/scenario-1/staffing/settings').flush(settings);
    fixture.detectChanges();
    expect(fixture.componentInstance.settings()).toEqual(settings);
    expect(fixture.componentInstance.message()).toBe('');
    expect(fixture.componentInstance.loading()).toBe(false);
  });
  it('cancels settings loading when leaving the tab and reloads on return', () => {
    const fixture = TestBed.createComponent(StaffingPanel);
    fixture.componentRef.setInput('scenario', structuredClone(scenario));
    fixture.componentRef.setInput('active', true);
    fixture.detectChanges();
    const abandoned = http.expectOne('/api/scenarios/scenario-1/staffing/settings');
    fixture.componentRef.setInput('active', false);
    fixture.detectChanges();
    expect(abandoned.cancelled).toBe(true);
    expect(fixture.componentInstance.loading()).toBe(false);
    fixture.componentRef.setInput('active', true);
    fixture.detectChanges();
    http.expectOne('/api/scenarios/scenario-1/staffing/settings').flush(settings);
    expect(fixture.componentInstance.settings()).toEqual(settings);
  });
  it('reports solver unavailability and allows retrying without displaying an invented proposal', () => {
    const fixture = component();
    const view = fixture.componentInstance;
    view.rows[0].physicalLimit = 2;
    view.propose();
    http
      .expectOne('/api/scenarios/scenario-1/staffing/proposals')
      .flush({ code: 'staffing_planner_unavailable' }, { status: 502, statusText: 'Bad Gateway' });
    fixture.detectChanges();
    expect(view.busy()).toBe(false);
    expect(view.result()).toBeNull();
    expect(fixture.nativeElement.textContent).toContain(
      'optimizare a personalului nu este disponibil',
    );
    expect(fixture.nativeElement.textContent).not.toContain('Personal propus');
    view.propose();
    http.expectOne('/api/scenarios/scenario-1/staffing/proposals').flush(unsolved());
    expect(view.message()).toBe('');
    expect(view.result()?.score).toBeNull();
  });
  it('displays infeasible status without presenting a configuration or score', () => {
    const fixture = component();
    const view = fixture.componentInstance;
    view.rows[0].physicalLimit = 0;
    view.propose();
    http
      .expectOne('/api/scenarios/scenario-1/staffing/proposals')
      .flush(unsolved('infeasible', 'INFEASIBLE'));
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Nu există o împărțire');
    expect(fixture.nativeElement.textContent).not.toContain('Personal propus');
  });
  it.each(['optimal', 'feasible'])(
    'shows the configuration and score with the correct %s status and captured terminal budgets',
    (status) => {
      const fixture = component();
      const view = fixture.componentInstance;
      view.rows[0].physicalLimit = 2;
      view.propose();
      http.expectOne('/api/scenarios/scenario-1/staffing/proposals').flush({
        status,
        solverStatus: status === 'optimal' ? 'OPTIMAL' : 'FEASIBLE',
        timedOut: status === 'feasible',
        score: 0,
        bestBound: 0,
        usedStaff: 2,
        unassignedStaff: 2,
        assignments: [
          {
            checkpointId: 'cp-1',
            staffCount: 2,
            estimatedCapacityMilli: 4000,
            unmetDemandMilli: 0,
            changePenalty: 0,
          },
        ],
        constraints: {
          scenarioId: 'scenario-1',
          scenarioRevision: 3,
          availableStaff: 4,
          timeLimitSeconds: 5,
          staffPools: settings.staffPools,
          checkpoints: [
            {
              checkpointId: 'cp-1',
              currentStaff: 2,
              minimumStaff: 1,
              maximumStaff: 4,
              physicalLimit: 2,
              closed: false,
              requiredOpen: true,
              changeCost: 10,
              demandMilli: 3000,
              capacityMilli: 2000,
            },
          ],
        },
        wallTimeSeconds: 0.01,
      });
      fixture.detectChanges();
      const text = fixture.nativeElement.textContent;
      expect(text).toContain(
        status === 'optimal' ? 'cea mai bună împărțire' : 'înainte să confirmăm',
      );
      expect(text).toContain('Scor de minimizat: 0');
      expect(text).toContain('Personal propus');
      expect(text).toContain('limită fizică 2');
      expect(text).toContain('Terminal terminal-1: 4 operatori');
    },
  );
});

import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { apiErrorInterceptor } from '../../core/http/api-error.interceptor';
import { SessionState } from '../../core/auth/session-state';
import type { ScenarioDetails } from '../operations/operations.models';
import { AgentDecision, AgentPanel, defaultAgent, reserveOf } from './agent-panel';
import { towerFixture } from './tower.testing';

describe('Live AI agent panel', () => {
  let http: HttpTestingController;
  beforeEach(() => {
    vi.useFakeTimers();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([apiErrorInterceptor])),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: SessionState, useValue: { canWrite: signal(true) } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => {
    http.verify();
    vi.useRealTimers();
  });

  const scenario = () => towerFixture().scenario as unknown as ScenarioDetails;
  const decision = (sequence: number, overrides: Partial<AgentDecision> = {}): AgentDecision => ({
    sequence,
    minute: 15 * sequence,
    point: 'periodic',
    kind: 'staff_move',
    optionId: 'move-1-1',
    text: 'Mută un operator de la CP-2 la CP-1',
    reason: 'Coada de la CP-1 are 40 de pasageri.',
    modelStatus: 'chosen',
    model: 'gemini-2.5-flash',
    options: [],
    ...overrides,
  });
  function render(runId: string | null, running: boolean, value = scenario()) {
    const fixture = TestBed.createComponent(AgentPanel);
    fixture.componentRef.setInput('scenario', value);
    fixture.componentRef.setInput('controls', 3);
    fixture.componentRef.setInput('runId', runId);
    fixture.componentRef.setInput('tracked', !!value.inputs.agent);
    fixture.componentRef.setInput('running', running);
    fixture.detectChanges();
    if (value.inputs.agent)
      http
        .expectOne('/api/agent/status')
        .flush({ modelConfigured: true, model: 'gemini-2.5-flash' });
    else http.expectNone('/api/agent/status');
    return fixture;
  }

  it('turns the agent on with everything the scenario allows and asks the tower to reload', () => {
    const fixture = render(null, false);
    const reloaded = vi.fn();
    fixture.componentInstance.changed.subscribe(reloaded);
    const box = fixture.nativeElement.querySelector('input[type=checkbox]') as HTMLInputElement;
    box.checked = true;
    box.dispatchEvent(new Event('change'));
    const save = http.expectOne((request) => request.method === 'PUT');
    expect(save.request.body.inputs.agent).toEqual(defaultAgent(scenario(), 3));
    expect(save.request.body.revision).toBe(scenario().scenario.revision);
    save.flush(scenario());
    expect(reloaded).toHaveBeenCalled();
  });

  it('follows the decisions while the run advances and explains a decision without a model answer', async () => {
    const value = {
      ...scenario(),
      inputs: { ...scenario().inputs, agent: defaultAgent(scenario(), 3) },
    };
    const fixture = render('run-1', true, value as ScenarioDetails);
    await vi.advanceTimersByTimeAsync(0);
    http.expectOne('/api/runs/run-1/agent-decisions').flush([decision(1)]);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Mută un operator de la CP-2 la CP-1');
    expect(fixture.nativeElement.textContent).toContain('Coada de la CP-1 are 40 de pasageri.');
    // A staffing decision links to its control on the map, once the operators serve there.
    expect(
      fixture.componentInstance.onMap(
        decision(1, {
          options: [{ id: 'move-1-1', kind: 'staff_move', text: '', toCheckpointId: 'cp-1' }],
        }),
      ),
    ).toEqual({
      scenarioId: value.scenario.id,
      runId: 'run-1',
      zoneId: 'cp-1',
      minute: 15 + 1 + 5,
    });
    await vi.advanceTimersByTimeAsync(2000);
    http.expectOne('/api/runs/run-1/agent-decisions').flush([
      decision(1),
      decision(2, {
        kind: 'none',
        text: 'Nicio schimbare acum',
        reason: '',
        modelStatus: 'timeout',
      }),
    ]);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain(
      'Modelul nu a răspuns la timp. Regulă de rezervă activă.',
    );
    fixture.componentRef.setInput('running', false);
    fixture.detectChanges();
    await vi.advanceTimersByTimeAsync(0);
    http.expectOne('/api/runs/run-1/agent-decisions').flush([decision(1)]);
    // The finished run is compared once with the same day without the agent.
    fixture.detectChanges();
    http.expectOne('/api/runs/run-1/agent-impact').flush({
      changes: ['2 operatori din rezervă la CP-1'],
      withoutAgent: {
        meanWaitMinutes: 18.2,
        maxP95WaitMinutes: 30,
        maxQueue: 472,
        queueClearedMinute: null,
      },
      withAgent: {
        meanWaitMinutes: 11.6,
        maxP95WaitMinutes: 17.8,
        maxQueue: 466,
        queueClearedMinute: 41,
      },
    });
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain(
      'Agentul: 2 operatori din rezervă la CP-1. Așteptarea medie a scăzut de la 18.2 la 11.6 min (−36%).',
    );
    expect(fixture.nativeElement.textContent).toContain('nu se golește');
    await vi.advanceTimersByTimeAsync(10000);
    http.expectNone('/api/runs/run-1/agent-decisions');
    http.expectNone('/api/runs/run-1/agent-impact');
  });

  it('changes how often the agent decides without turning it off', () => {
    const value = {
      ...scenario(),
      inputs: {
        ...scenario().inputs,
        agent: { ...defaultAgent(scenario(), 3)!, decisionIntervalMinutes: 15 },
      },
    };
    const fixture = render(null, false, value as ScenarioDetails);
    const select = fixture.nativeElement.querySelector(
      '.agent-interval select',
    ) as HTMLSelectElement;
    expect(select.value).toBe('15');
    select.value = '5';
    select.dispatchEvent(new Event('change'));
    const save = http.expectOne((request) => request.method === 'PUT');
    expect(save.request.body.inputs.agent).toEqual({
      ...value.inputs.agent,
      decisionIntervalMinutes: 5,
    });
    save.flush(value);
  });

  it('gives the agent a reserve to call in, and refuses a scenario with nothing to control', () => {
    const value = scenario();
    const bare = {
      ...value,
      inputs: { ...value.inputs, checkIn: null, journey: null },
    } as ScenarioDetails;
    // One control and nobody to move: the reserve is still something the agent can act with.
    expect(defaultAgent(bare, 1)).toMatchObject({ staffMoves: false, reserveOperators: 10 });
    expect(defaultAgent(bare, 3)?.staffMoves).toBe(true);
    // A scenario saved before the reserve existed shows the default it now gets.
    expect(
      reserveOf({
        staffMoves: true,
        checkInDesks: false,
        gateChoices: false,
        decisionIntervalMinutes: 15,
        transferMinutes: 5,
      }),
    ).toBe(10);
    const shifts = {
      ...bare,
      inputs: { ...bare.inputs, staffShifts: { slotMinutes: 15, transferMinutes: 5, shifts: [] } },
    } as ScenarioDetails;
    expect(defaultAgent(shifts, 3)).toBeNull();
  });
});

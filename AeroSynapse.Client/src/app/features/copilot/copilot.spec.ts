import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component, input, output, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap, provideRouter, Router } from '@angular/router';
import { BehaviorSubject } from 'rxjs';
import { PickedRun, RunPicker } from '../../shared/ui/run-picker';
import { Copilot } from './copilot';
import { ToolName, ToolResult, validToolResult } from './copilot-tools.models';
import { CopilotAnswer, CopilotContext, CopilotScope, validAnswer } from './copilot.models';

const scenarioId = '10000000-0000-0000-0000-000000000001';
const runId = '20000000-0000-0000-0000-000000000001';
const otherRun = '20000000-0000-0000-0000-000000000002';
const zoneId = '30000000-0000-0000-0000-000000000001';
const otherZone = '30000000-0000-0000-0000-000000000002';
const scope: CopilotScope = { scenarioId, runId, zoneId, minute: 60 };
const context = (id = runId): CopilotContext => ({
  contractVersion: '1.0',
  scenarioId,
  runId: id,
  status: 'completed',
  resultsValid: true,
  isSynthetic: true,
  scenarioRevision: 1,
  layoutVersionId: '40000000-0000-0000-0000-000000000001',
  durationMinutes: 60,
  snapshotIntervalMinutes: 1,
  zones: [
    { id: zoneId, code: 'CP1', name: 'Control' },
    { id: otherZone, code: 'CP2', name: 'Control doi' },
  ],
  unavailableReason: null,
});
const answer = (at: CopilotScope = scope): CopilotAnswer => ({
  contractVersion: '1.0',
  scope: at,
  status: 'answered',
  reasonCode: 'observed_metrics',
  answer: 'Pasageri în coadă: 17 pasageri [queuedPassengers].',
  modelStatus: 'disabled',
  sourceHash: 'a'.repeat(64),
  citations: [
    {
      ...at,
      id: 'queuedPassengers',
      label: 'Pasageri în coadă',
      value: 17,
      unit: 'pasageri',
      recordedAtUtc: '2026-10-17T09:00:00Z',
    },
  ],
});
const toolResult = (tool: ToolName, at: CopilotScope = scope): ToolResult => ({
  contractVersion: '1.0',
  scope: at,
  intent: tool === 'forecast_queues' ? 'forecast' : 'diagnose',
  tool,
  effect: 'read_only',
  message: 'Calcul confirmat',
});
const forecast = (minute: number): ToolResult => ({
  ...toolResult('forecast_queues', { ...scope, minute }),
  forecast: {
    kind: 'synthetic',
    formula: 'coadă = coadă anterioară + sosiri − capacitate',
    runId,
    fromMinute: minute,
    toMinute: minute + 15,
    queueUnit: 'pasageri',
    flowUnit: 'pasageri/minut',
    timeUnit: 'minute',
    zone: {
      checkpointId: zoneId,
      initialQueuePassengers: 4,
      points: Array.from({ length: 15 }, (_, index) => ({
        minute: minute + index + 1,
        arrivals: 5,
        capacity: 3,
        queuePassengers: index * 2,
        availableStaff: 2,
      })),
    },
  },
});
const path = (id = runId) => `/api/scenarios/${scenarioId}/runs/${id}/copilot`;

/** The real picker loads every scenario; the page only needs its contract. */
@Component({ selector: 'app-run-picker', template: '' })
class PickerStub {
  readonly selected = input<string | null>(null);
  readonly preferredScenarioId = input<string | null>(null);
  readonly autoSelect = input(false);
  readonly disabled = input(false);
  readonly picked = output<PickedRun>();
  readonly scenarioId = signal(scenarioId);
  readonly scenarioName = signal('Bottleneck');
}

describe('Copilot conversation', () => {
  let http: HttpTestingController;
  let query: BehaviorSubject<ReturnType<typeof convertToParamMap>>;
  beforeEach(() => {
    query = new BehaviorSubject(convertToParamMap({}));
    TestBed.configureTestingModule({
      imports: [Copilot],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { queryParamMap: query } },
      ],
    }).overrideComponent(Copilot, {
      remove: { imports: [RunPicker] },
      add: { imports: [PickerStub] },
    });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());
  async function open(params: Record<string, string> = { scenarioId, runId }) {
    const fixture = TestBed.createComponent(Copilot);
    query.next(convertToParamMap(params));
    http.expectOne(path(params['runId']) + '/context').flush(context(params['runId']));
    await fixture.whenStable();
    return fixture;
  }

  it('asks with only the explicit scope and shows the cited figures', async () => {
    const fixture = await open(),
      view = fixture.componentInstance;
    view.question = 'Câți pasageri sunt în coadă?';
    view.ask();
    const request = http.expectOne(path());
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({
      zoneId,
      minute: 60,
      question: 'Câți pasageri sunt în coadă?',
    });
    expect(request.request.headers.has('Authorization')).toBe(false);
    expect(view.question).toBe('');
    request.flush(answer());
    await fixture.whenStable();
    const host: HTMLElement = fixture.nativeElement;
    expect(host.querySelector('[data-citation="queuedPassengers"]')!.textContent).toContain('17');
    expect(host.querySelector('[data-answer]')!.textContent!.trim()).toBe(
      'Pasageri în coadă: 17 pasageri.',
    );
  });

  it('shows calculated checkpoint and queue options with their source figures', async () => {
    const fixture = await open(); fixture.componentInstance.ask('Cum distribui coada?');
    http.expectOne(path()).flush({ ...answer(), queueAdvice: {
      options: [{ id: 'open', kind: 'checkpoint_open', text: 'Deschide controlul alternativ și distribuie până la 8 pasageri',
        fromCheckpointId: zoneId, toCheckpointId: otherZone, passengerCount: 8 }],
      facts: [{ id: 'queue', label: 'Coada observată', value: 17, unit: 'pasageri', runId, zoneId, sourcePath: '/kpis' }],
      limitation: 'Variante de evaluat în simulator.' } });
    await fixture.whenStable();
    expect(fixture.nativeElement.textContent).toContain('Deschide controlul alternativ');
    expect(fixture.nativeElement.textContent).toContain('Variante de evaluat în simulator.');
  });
  it('keeps each turn with the zone and minute it was asked about', async () => {
    const fixture = await open(),
      view = fixture.componentInstance;
    view.ask('Rezumă zona.');
    http.expectOne(path()).flush(answer());
    view.zoneId = otherZone;
    view.minute = 20;
    view.ask('Rezumă zona.');
    const second = http.expectOne(path());
    expect(second.request.body).toEqual({
      zoneId: otherZone,
      minute: 20,
      question: 'Rezumă zona.',
    });
    second.flush(answer({ ...scope, zoneId: otherZone, minute: 20 }));
    expect(view.turns().map((turn) => [turn.scope.zoneId, turn.scope.minute, turn.state])).toEqual([
      [zoneId, 60, 'done'],
      [otherZone, 20, 'done'],
    ]);
  });

  it('sends one request at a time and nothing invalid', async () => {
    const fixture = await open(),
      view = fixture.componentInstance;
    for (const minute of [-1, 61, 1.5]) {
      view.minute = minute;
      view.ask('Rezumă zona.');
    }
    view.minute = 60;
    view.zoneId = 'unknown';
    view.ask('Rezumă zona.');
    view.zoneId = zoneId;
    view.question = '';
    view.ask();
    view.question = 'x'.repeat(1001);
    view.ask();
    http.expectNone(path());
    expect(view.turns()).toEqual([]);
    view.ask('Prima');
    view.ask('A doua');
    http.expectOne(path()).flush(answer());
    expect(view.turns().length).toBe(1);
  });

  it('cancels the pending reply and clears the conversation when the run changes', async () => {
    const fixture = await open(),
      view = fixture.componentInstance;
    view.ask('Rezumă zona.');
    const pending = http.expectOne(path());
    query.next(convertToParamMap({ scenarioId, runId: otherRun }));
    expect(pending.cancelled).toBe(true);
    expect(view.turns()).toEqual([]);
    expect(view.context()).toBeNull();
    expect(view.busy()).toBe(false);
    http.expectOne(path(otherRun) + '/context').flush(context(otherRun));
    expect(view.minute).toBe(60);
  });

  it('rejects a reply for another scope or with foreign citations', async () => {
    const fixture = await open(),
      view = fixture.componentInstance;
    for (const bad of [
      { ...answer(), scope: { ...scope, runId: otherRun } },
      { ...answer(), citations: [{ ...answer().citations[0], zoneId: otherZone }] },
    ]) {
      view.ask('Rezumă zona.');
      http.expectOne(path()).flush(bad);
      const last = view.turns().at(-1)!;
      expect(last.state).toBe('failed');
      expect(last.answer).toBeUndefined();
      expect(last.error).toContain('surse valide');
    }
  });

  it('shows insufficient evidence as plain text without figures', async () => {
    const fixture = await open(),
      view = fixture.componentInstance;
    view.ask('Care este așteptarea?');
    http.expectOne(path()).flush({
      ...answer(),
      status: 'insufficient_data',
      answer: 'Nu pot concluziona: lipsesc date. <img src=x onerror=alert(1)>',
      citations: [],
      sourceHash: null,
      modelStatus: 'not_requested',
    });
    await fixture.whenStable();
    expect(fixture.nativeElement.textContent).toContain('Date insuficiente');
    expect(fixture.nativeElement.textContent).toContain('Nu pot concluziona');
    expect(fixture.nativeElement.querySelector('img')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-citation]')).toBeNull();
  });

  it('recovers from an HTTP failure and allows a retry', async () => {
    const fixture = await open(),
      view = fixture.componentInstance;
    view.ask('Rezumă zona.');
    http.expectOne(path()).flush({}, { status: 503, statusText: 'Unavailable' });
    expect(view.busy()).toBe(false);
    expect(view.turns()[0].state).toBe('failed');
    expect(view.context()).not.toBeNull();
    view.ask('Rezumă zona.');
    http.expectOne(path()).flush({ ...answer(), modelStatus: 'timeout' });
    await fixture.whenStable();
    expect(fixture.nativeElement.textContent).toContain('timpul alocat');
  });

  it('forecasts from the selected minute only when the horizon fits in the run', async () => {
    const fixture = await open({ scenarioId, runId, minute: '15' }),
      view = fixture.componentInstance;
    expect(view.minute).toBe(15);
    view.forecast();
    const rejected = http.expectOne(path() + '/tools');
    expect(rejected.request.body).toEqual({
      intent: 'forecast',
      zoneId,
      minute: 15,
      call: { name: 'forecast_queues', arguments: { horizonMinutes: 15 } },
    });
    rejected.flush({ ...forecast(15), forecast: { ...forecast(15).forecast!, runId: otherRun } });
    expect(view.turns()[0].state).toBe('failed');
    view.forecast();
    http.expectOne(path() + '/tools').flush(forecast(15));
    await fixture.whenStable();
    const text = fixture.nativeElement.textContent;
    expect(text).toContain('Estimare, nu observație');
    expect(text.replace(/\s+/g, ' ')).toContain(
      'Acum sunt 4 pasageri în coadă. Până la minutul 30, coada estimată crește la 28 pasageri.',
    );
    expect(text).not.toContain('Vârful estimat');
    const shrinking = view.describe({
      ...forecast(15).forecast!,
      zone: {
        ...forecast(15).forecast!.zone,
        initialQueuePassengers: 30,
        points: forecast(15).forecast!.zone.points.map((p, i) => ({
          ...p,
          queuePassengers: i === 3 ? 40 : 10,
        })),
      },
    });
    expect([shrinking.verb, shrinking.end, shrinking.peak.minute, shrinking.showPeak]).toEqual([
      'scade la',
      10,
      19,
      true,
    ]);
    view.minute = 50;
    expect(view.canForecast()).toBe(false);
    view.forecast();
    http.expectNone(path() + '/tools');
  });

  it('lists the zone events through the read-only inspection tool', async () => {
    const fixture = await open(),
      view = fixture.componentInstance;
    view.events();
    const request = http.expectOne(path() + '/tools');
    expect(request.request.body.intent).toBe('diagnose');
    expect(request.request.body.call).toEqual({
      name: 'inspect_zone',
      arguments: { question: 'Rezumă situația zonei.' },
    });
    request.flush({
      ...toolResult('inspect_zone'),
      investigation: {
        observation: answer(),
        events: [{ id: 'e1', type: 'passenger_surge', minute: 12, checkpointId: zoneId }],
      },
    });
    await fixture.whenStable();
    expect(fixture.nativeElement.textContent).toContain('Un eveniment a afectat');
    expect(fixture.nativeElement.textContent).toContain('Val de pasageri');
    http.expectNone((r) => r.url.includes('approve') || r.url.includes('recommendation'));
  });

  it('opens a picked run through the URL and resolves a pasted runId to its scenario', async () => {
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const view = TestBed.createComponent(Copilot).componentInstance;
    http.expectNone((r) => r.url.includes('/copilot'));
    view.pick({ scenarioId, runId, scenarioName: 'Bottleneck' });
    expect(navigate).toHaveBeenLastCalledWith(
      [],
      expect.objectContaining({ queryParams: { scenarioId, runId } }),
    );
    view.pick({ scenarioId: '', runId: otherRun, scenarioName: '' });
    http
      .expectOne(`/api/runs/${otherRun}`)
      .flush({ id: otherRun, scenarioId, status: 'completed' });
    expect(navigate).toHaveBeenLastCalledWith(
      [],
      expect.objectContaining({ queryParams: { scenarioId, runId: otherRun } }),
    );
  });

  it('opens deep links with zone and minute and ignores a late context from the previous run', async () => {
    const view = TestBed.createComponent(Copilot).componentInstance;
    query.next(convertToParamMap({ scenarioId, runId, zoneId: otherZone, minute: '20' }));
    const previous = http.expectOne(path() + '/context');
    query.next(convertToParamMap({ scenarioId, runId: otherRun }));
    expect(previous.cancelled).toBe(true);
    http.expectOne(path(otherRun) + '/context').flush(context(otherRun));
    expect(view.context()?.runId).toBe(otherRun);
    expect(view.minute).toBe(60);
    query.next(convertToParamMap({ scenarioId, runId, zoneId: otherZone, minute: '20' }));
    http.expectOne(path() + '/context').flush(context());
    expect(view.zoneId).toBe(otherZone);
    expect(view.minute).toBe(20);
    query.next(convertToParamMap({ scenarioId, runId, zoneId: 'unknown', minute: '99' }));
    http.expectOne(path() + '/context').flush(context());
    expect(view.zoneId).toBe(zoneId);
    expect(view.minute).toBe(60);
    expect(view.message()).toContain('prima zonă');
  });

  it('rejects malformed links, a foreign context and explains a failed run', async () => {
    const fixture = TestBed.createComponent(Copilot),
      view = fixture.componentInstance;
    query.next(convertToParamMap({ scenarioId: 'x', runId: 'y' }));
    expect(view.message()).toContain('UUID');
    query.next(convertToParamMap({ scenarioId, runId: otherRun }));
    http.expectOne(path(otherRun) + '/context').flush(context());
    expect(view.context()).toBeNull();
    expect(view.message()).toContain('nu corespund');
    query.next(convertToParamMap({ scenarioId, runId: otherRun }));
    http.expectOne(path(otherRun) + '/context').flush({
      ...context(otherRun),
      status: 'failed',
      resultsValid: false,
      unavailableReason: 'run_not_completed',
    });
    await fixture.whenStable();
    expect(fixture.nativeElement.textContent).toContain('nu are rezultate valide');
  });
});

describe('Copilot evidence envelope', () => {
  it('rejects queue advice that moves passengers from another zone', () => {
    const value = { ...answer(), queueAdvice: { options: [{ id: 'queue', kind: 'queue_redistribution',
      text: 'Distribuie coada', fromCheckpointId: otherZone, toCheckpointId: scope.zoneId, passengerCount: 10 }],
      facts: [], limitation: 'Variantă de evaluat.' } } as unknown as CopilotAnswer;
    expect(validAnswer(value, scope)).toBe(false);
  });
  it('rejects nonfinite values, wrong time, unknown versions and answers without evidence', () => {
    expect(validAnswer(answer(), scope)).toBe(true);
    expect(validAnswer({ ...answer(), contractVersion: '2.0' }, scope)).toBe(false);
    expect(validAnswer({ ...answer(), citations: [] }, scope)).toBe(false);
    expect(validAnswer({ ...answer(), scope: { ...scope, minute: 59 } }, scope)).toBe(false);
    expect(
      validAnswer({ ...answer(), citations: [{ ...answer().citations[0], value: NaN }] }, scope),
    ).toBe(false);
    expect(validAnswer({ ...answer(), sourceHash: null }, scope)).toBe(false);
    expect(validAnswer({ ...answer(), citations: [null] } as unknown as CopilotAnswer, scope)).toBe(
      false,
    );
  });
  it('accepts tool results only for the selected run, zone, minute and tool', () => {
    const at = { ...scope, minute: 15 },
      good = forecast(15),
      body = good.forecast!;
    expect(validToolResult(good, at, 'forecast_queues')).toBe(true);
    expect(validToolResult(good, at, 'inspect_zone')).toBe(false);
    expect(validToolResult(good, scope, 'forecast_queues')).toBe(false);
    expect(
      validToolResult({ ...good, forecast: { ...body, runId: otherRun } }, at, 'forecast_queues'),
    ).toBe(false);
    expect(
      validToolResult(
        {
          ...good,
          forecast: { ...body, zone: { ...body.zone, points: body.zone.points.slice(1) } },
        },
        at,
        'forecast_queues',
      ),
    ).toBe(false);
    expect(
      validToolResult(
        { ...toolResult('inspect_zone'), investigation: { observation: answer(), events: [] } },
        scope,
        'inspect_zone',
      ),
    ).toBe(true);
  });
});

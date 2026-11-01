import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { apiErrorInterceptor } from '../../core/http/api-error.interceptor';
import { OPERATION_REQUEST_TIMEOUT_MS } from '../../core/http/operation-request';
import { ids, towerFixture } from '../control-tower/tower.testing';
import { NotificationStore } from './notification.store';
import {
  activityFixture,
  alertFixture,
  detailFixture,
  notificationIds,
} from './notification.testing';

describe('Persistent operational notification coordination', () => {
  let http: HttpTestingController, store: NotificationStore;
  beforeEach(() => {
    vi.useFakeTimers();
    const storage = new Map<string, string>();
    vi.stubGlobal('localStorage', {
      getItem: (key: string) => storage.get(key) ?? null,
      setItem: (key: string, value: string) => storage.set(key, value),
      removeItem: (key: string) => storage.delete(key),
    });
    configure();
  });
  function configure() {
    TestBed.configureTestingModule({
      providers: [
        NotificationStore,
        provideHttpClient(withInterceptors([apiErrorInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    store = TestBed.inject(NotificationStore);
    http
      .expectOne('/api/scenarios')
      .flush([
        towerFixture().scenario.scenario,
        { ...towerFixture().scenario.scenario, id: ids.otherRun, isSynthetic: false },
      ]);
  }
  afterEach(() => {
    TestBed.resetTestingModule();
    http.verify({ ignoreCancelled: true });
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });
  async function open() {
    store.open({ scenarioId: ids.scenario, runId: ids.run, alertId: notificationIds.alert });
    http.expectOne(`/api/scenarios/${ids.scenario}/runs?limit=100`).flush([towerFixture().run]);
    http.expectOne(`/api/scenarios/${ids.scenario}/runs/${ids.run}`).flush(towerFixture().run);
    await vi.advanceTimersByTimeAsync(0);
    answer();
  }
  function answer() {
    const list = http.expectOne((r) => r.url.startsWith('/api/operational-notifications?'));
    list.flush({ items: [alertFixture()], total: 1, offset: 0, limit: 25 });
    http
      .expectOne((r) => r.url.startsWith('/api/operational-notifications/activity?'))
      .flush({ items: [activityFixture()], total: 1, offset: 0, limit: 25 });
    http
      .expectOne(`/api/operational-notifications/${notificationIds.alert}`)
      .flush(detailFixture());
    return new URL(list.request.url, 'http://localhost');
  }
  it('reads synthetic alerts, audit and captured run data from API with the same identities', async () => {
    await open();
    expect(store.scenarios()).toHaveLength(1);
    expect(store.alerts().items[0].evidence.peakQueuePassengers).toBe(30);
    expect(store.detail()?.alert.runId).toBe(ids.run);
    expect(store.activity().items[0].kind).toBe('created');
    expect(store.fresh()).toBe(true);
    expect(store.capture()).not.toBeNull();
  });
  it('polls silently with confirmed content retained and shows feedback for a manual refresh', async () => {
    await open();
    const detail = store.detail();
    expect(store.loading()).toBe(false);
    await vi.advanceTimersByTimeAsync(5000);
    expect(store.loading()).toBe(false);
    expect(store.detail()).toBe(detail);
    expect(store.alerts().total).toBe(1);
    answer();
    expect(store.fresh()).toBe(true);
    store.refresh();
    await vi.advanceTimersByTimeAsync(0);
    expect(store.loading()).toBe(true);
    answer();
    expect(store.loading()).toBe(false);
  });
  it('cancels pending reads on a filter change and sends scenario/time/level/ack filters to both endpoints', async () => {
    await open();
    store.refresh();
    await vi.advanceTimersByTimeAsync(0);
    const old = http.expectOne((r) => r.url.startsWith('/api/operational-notifications?'));
    http.expectOne((r) => r.url.startsWith('/api/operational-notifications/activity?'));
    http.expectOne(`/api/operational-notifications/${notificationIds.alert}`);
    store.applyFilters({
      level: 'critical',
      acknowledged: 'false',
      fromUtc: '2026-10-20T09:00:00Z',
      toUtc: '2026-10-20T11:00:00Z',
      offset: 0,
    });
    expect(old.cancelled).toBe(true);
    await vi.advanceTimersByTimeAsync(0);
    const query = answer().searchParams;
    expect(query.get('scenarioId')).toBe(ids.scenario);
    expect(query.get('runId')).toBe(ids.run);
    expect(query.get('level')).toBe('critical');
    expect(query.get('fromUtc')).toBe('2026-10-20T09:00:00Z');
    expect(query.get('acknowledged')).toBe('false');
  });
  it('rejects foreign detail provenance and immediately disables coordination', async () => {
    await open();
    store.refresh();
    await vi.advanceTimersByTimeAsync(0);
    http
      .expectOne((r) => r.url.startsWith('/api/operational-notifications?'))
      .flush({ items: [], total: 0, offset: 0, limit: 25 });
    http
      .expectOne((r) => r.url.startsWith('/api/operational-notifications/activity?'))
      .flush({ items: [], total: 0, offset: 0, limit: 25 });
    http
      .expectOne(`/api/operational-notifications/${notificationIds.alert}`)
      .flush({ ...detailFixture(), alert: { ...alertFixture(), runId: ids.otherRun } });
    expect(store.fresh()).toBe(false);
    expect(store.detail()).toBeNull();
    store.act('acknowledge', 'Supervisor');
    http.expectNone((r) => r.method === 'POST');
  });
  it('invalidating the route cancels polling and prevents an old response from overwriting the context error', async () => {
    await open();
    store.refresh();
    await vi.advanceTimersByTimeAsync(0);
    const pending = http.expectOne((r) => r.url.startsWith('/api/operational-notifications?'));
    http.expectOne((r) => r.url.startsWith('/api/operational-notifications/activity?'));
    http.expectOne(`/api/operational-notifications/${notificationIds.alert}`);
    store.open({ scenarioId: 'invalid', runId: null, alertId: null });
    expect(pending.cancelled).toBe(true);
    expect(store.detail()).toBeNull();
    expect(store.fresh()).toBe(false);
    await vi.advanceTimersByTimeAsync(15000);
    http.expectNone((r) => r.url.startsWith('/api/operational-notifications?'));
  });
  it('retains an ambiguous action and retries exactly its original actor/text/revision/command', async () => {
    await open();
    store.act('comment', ' Supervisor ', ' Verificare ');
    const first = http.expectOne(`/api/operational-notifications/${notificationIds.alert}/actions`),
      body = first.request.body;
    expect(body).toMatchObject({
      revision: 1,
      actor: 'Supervisor',
      text: 'Verificare',
      action: 'comment',
    });
    first.error(new ProgressEvent('error'));
    store.act('assign', 'Other', 'Other team');
    http.expectNone((r) => r.method === 'POST');
    store.retry();
    const retry = http.expectOne(`/api/operational-notifications/${notificationIds.alert}/actions`);
    expect(retry.request.body).toEqual(body);
    retry.flush({
      commandId: body.commandId,
      alertIds: [notificationIds.alert],
      duplicates: 0,
      staleObservations: 0,
      recordedAtUtc: '2026-10-20T10:00:01Z',
    });
    expect(store.pending()).toBeNull();
    await vi.advanceTimersByTimeAsync(0);
    answer();
  });
  it('restores a timed-out action after navigation and never submits it automatically', async () => {
    await open();
    store.act('assign', 'Supervisor', 'Echipa A');
    const first = http.expectOne(`/api/operational-notifications/${notificationIds.alert}/actions`),
      body = first.request.body;
    store.open({ scenarioId: 'invalid', runId: null, alertId: null });
    await vi.advanceTimersByTimeAsync(OPERATION_REQUEST_TIMEOUT_MS);
    expect(first.cancelled).toBe(true);
    expect(store.commandError()).toContain('Timp de așteptare depășit');
    TestBed.resetTestingModule();
    configure();
    expect(store.pending()?.body).toEqual(body);
    http.expectNone((r) => r.method === 'POST');
    store.retry();
    const retry = http.expectOne(`/api/operational-notifications/${notificationIds.alert}/actions`);
    expect(retry.request.body).toEqual(body);
    retry.flush({
      commandId: body.commandId,
      alertIds: [notificationIds.alert],
      duplicates: 0,
      staleObservations: 0,
      recordedAtUtc: '2026-10-20T10:00:01Z',
    });
  });
  it('restores an unconfirmed whole-run generation for all scenarios and retries the same command', async () => {
    store.publishRuns(5, 10);
    const first = http.expectOne('/api/operational-notifications/run-queues'),
      body = first.request.body;
    expect(body).toMatchObject({ scenarioId: null, runId: null, warningThreshold: 5, criticalThreshold: 10 });
    first.error(new ProgressEvent('error'));
    TestBed.resetTestingModule();
    configure();
    expect(store.pending()?.body).toEqual(body);
    store.retry();
    const retry = http.expectOne('/api/operational-notifications/run-queues');
    expect(retry.request.body).toEqual(body);
    retry.flush({
      commandId: body.commandId,
      alertIds: [notificationIds.alert],
      duplicates: 1,
      staleObservations: 0,
      recordedAtUtc: '2026-10-20T10:00:01Z',
    });
    expect(store.message()).toBe('Gata: 0 alerte noi, 1 existau deja.');
    expect(store.pending()).toBeNull();
  });
  it('retains a command after a foreign receipt and preserves a revision conflict message after refreshing the feed', async () => {
    await open();
    store.act('acknowledge', 'Supervisor');
    const first = http.expectOne(`/api/operational-notifications/${notificationIds.alert}/actions`);
    first.flush({ commandId: crypto.randomUUID(), alertIds: [notificationIds.alert] });
    expect(store.pending()).not.toBeNull();
    expect(store.commandError()).toContain('nu corespunde');
    store.retry();
    http
      .expectOne(`/api/operational-notifications/${notificationIds.alert}/actions`)
      .flush(
        { title: 'Alert changed in another session.' },
        { status: 409, statusText: 'Conflict' },
      );
    expect(store.pending()).toBeNull();
    const error = store.commandError();
    expect(error).toBeTruthy();
    await vi.advanceTimersByTimeAsync(0);
    answer();
    expect(store.commandError()).toBe(error);
  });
  it('publishes using the captured run and reports duplicate/stale observations without inventing another alert', async () => {
    await open();
    store.publish({
      scenarioId: ids.scenario,
      runId: ids.run,
      fromMinute: 0,
      horizonMinutes: 15,
      warningThreshold: 10,
      criticalThreshold: 20,
      useLatest: false,
    });
    const request = http.expectOne('/api/operational-notifications/queue-forecast');
    expect(request.request.body.scenarioId).toBe(ids.scenario);
    expect(request.request.body.runId).toBe(ids.run);
    request.flush({
      commandId: request.request.body.commandId,
      alertIds: [],
      duplicates: 2,
      staleObservations: 1,
      recordedAtUtc: '2026-10-20T10:00:01Z',
    });
    expect(store.message()).toContain('Duplicate ignorate: 2');
    expect(store.message()).toContain('vechi ignorate: 1');
    await vi.advanceTimersByTimeAsync(0);
    answer();
  });
  it('keeps archived scenario names available from retained alert evidence and preserves selection outside the filtered page', async () => {
    await open();
    store.scenarios.set([]);
    expect(store.scenarioOptions().some((s) => s.id === ids.scenario)).toBe(true);
    store.refresh();
    await vi.advanceTimersByTimeAsync(0);
    http
      .expectOne((r) => r.url.startsWith('/api/operational-notifications?'))
      .flush({ items: [], total: 0, offset: 0, limit: 25 });
    http
      .expectOne((r) => r.url.startsWith('/api/operational-notifications/activity?'))
      .flush({ items: [], total: 0, offset: 0, limit: 25 });
    http
      .expectOne(`/api/operational-notifications/${notificationIds.alert}`)
      .flush(detailFixture());
    expect(store.selectedOutsidePage()).toBe(true);
    expect(store.scenarioOptions().some((s) => s.id === ids.scenario)).toBe(true);
  });
});

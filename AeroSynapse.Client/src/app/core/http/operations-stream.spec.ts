import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { OPERATIONS_RECOVERY_TIMEOUT_MS } from './operation-request';
import { HubConnectionState } from '@microsoft/signalr';
import { OPERATIONS_CONNECTION_FACTORY, OperationsConnection, OperationsMessage, OperationsState, OperationsStream } from './operations-stream';

class Connection implements OperationsConnection {
  state = HubConnectionState.Disconnected;
  fail = false;
  handlers = new Map<string, (message: OperationsMessage) => void>();
  reconnecting = () => {};
  reconnected = () => {};
  closed = () => {};
  start = vi.fn(async () => { if (this.fail) throw new Error('offline'); this.state = HubConnectionState.Connected; });
  stop = vi.fn(async () => { this.state = HubConnectionState.Disconnected; });
  invoke = vi.fn(async () => ({}));
  on(name: string, handler: (message: OperationsMessage) => void) { this.handlers.set(name, handler); }
  onreconnecting(handler: () => void) { this.reconnecting = handler; }
  onreconnected(handler: () => void) { this.reconnected = handler; }
  onclose(handler: () => void) { this.closed = handler; }
  emit(message: OperationsMessage) { this.handlers.get(message.type)?.(message); }
}
function state(sequence = 0, minute = 0, scenarioId = 'scenario-a'): OperationsState {
  const run = { runId: 'run-a', status: 'running', revision: 2, resultsValid: false, simulationMinute: minute,
    snapshot: { totalZones: 1, zones: [{ zoneId: 'zone-a', queuedPassengers: 4, processedPassengers: 2 }] } };
  return { scenarioId, sequence, resetRequired: false, run, runs: [run], optimization: null };
}
function frame(sequence: number, minute: number, scenarioId = 'scenario-a'): OperationsMessage {
  return { scenarioId, sequence, type: 'run.snapshot', runId: 'run-a', status: 'running', resultsValid: false,
    simulationMinute: minute, snapshot: { totalZones: 1, zones: [{ zoneId: 'zone-a', queuedPassengers: 3, processedPassengers: minute }] }, optimization: null };
}

describe('Operations connection, ordering and REST recovery', () => {
  it('releases a stalled reconciliation and recovers the same scenario/run without regressing its last observation', async () => {
    await connect();
    connections[0].state = HubConnectionState.Reconnecting;
    connections[0].reconnecting();
    const stalled = http.expectOne((request) => request.url.includes('/operations'));
    await vi.advanceTimersByTimeAsync(OPERATIONS_RECOVERY_TIMEOUT_MS);
    expect(stalled.cancelled).toBe(true);
    expect(stream.state()?.run?.simulationMinute).toBe(0);
    expect(stream.error()).toContain('reconectare');
    await vi.advanceTimersByTimeAsync(1000);
    const retry = http.expectOne((request) => request.url.includes('/operations'));
    expect(retry.request.url).toContain('scenario-a/operations');
    expect(retry.request.url).toContain('runId=run-a');
    retry.flush(state(1, 8));
    expect(stream.error()).toBe('');
    expect(stream.state()?.run?.simulationMinute).toBe(8);
  });
  let http: HttpTestingController;
  let stream: OperationsStream;
  let connections: Connection[];
  beforeEach(() => {
    vi.useFakeTimers(); connections = [];
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), OperationsStream,
      { provide: OPERATIONS_CONNECTION_FACTORY, useValue: () => { const connection = new Connection(); connections.push(connection); return connection; } }] });
    http = TestBed.inject(HttpTestingController); stream = TestBed.inject(OperationsStream);
  });
  afterEach(() => { stream.ngOnDestroy(); http.verify({ ignoreCancelled: true }); vi.useRealTimers(); });
  function recover(value: OperationsState) {
    http.expectOne(request => request.url.startsWith(`/api/scenarios/${value.scenarioId}/operations`)).flush(value);
  }
  async function connect() {
    stream.connect('scenario-a', 'run-a');
    await vi.advanceTimersByTimeAsync(0);
    recover(state());
    await vi.advanceTimersByTimeAsync(1000);
    const requests = http.match(request => request.url.includes('/operations'));
    for (const request of requests) request.flush(state());
    expect(stream.status()).toBe('live');
  }

  it('uses ordered sequences, ignores duplicate/foreign messages and refuses time regression', async () => {
    await connect();
    connections[0].emit(frame(1, 3));
    connections[0].emit(frame(1, 2));
    connections[0].emit(frame(2, 99, 'scenario-b'));
    expect(stream.state()?.sequence).toBe(1);
    expect(stream.state()?.run?.simulationMinute).toBe(3);
    connections[0].emit(frame(2, 1));
    expect(stream.state()?.sequence).toBe(2);
    expect(stream.state()?.run?.simulationMinute).toBe(3);
  });

  it('buffers gaps and applies the authoritative GET before later buffered events', async () => {
    await connect();
    connections[0].emit(frame(3, 6));
    expect(stream.state()?.sequence).toBe(0);
    recover(state(2, 5));
    expect(stream.state()?.sequence).toBe(3);
    expect(stream.state()?.run?.simulationMinute).toBe(6);
    connections[0].emit(frame(2, 5));
    expect(stream.state()?.sequence).toBe(3);
  });

  it('rejoins its scenario and reconciles GET on reconnection', async () => {
    await connect();
    connections[0].state = HubConnectionState.Reconnecting;
    connections[0].reconnecting();
    recover(state(4, 10));
    connections[0].state = HubConnectionState.Connected;
    connections[0].reconnected();
    await vi.advanceTimersByTimeAsync(0);
    recover(state(5, 12));
    expect(connections[0].invoke).toHaveBeenCalledWith('SubscribeScenario', 'scenario-a');
    expect(stream.state()?.run?.simulationMinute).toBe(12);
    expect(stream.status()).toBe('live');
  });

  it('continues REST polling after a failed initial SignalR connection and a REST error', async () => {
    stream.connect('scenario-a', 'run-a'); connections[0].fail = true;
    await vi.advanceTimersByTimeAsync(0);
    recover(state());
    expect(stream.status()).toBe('polling');
    await vi.advanceTimersByTimeAsync(2000);
    http.expectOne(request => request.url.includes('/operations')).flush({}, { status: 503, statusText: 'offline' });
    await vi.advanceTimersByTimeAsync(2000);
    recover(state(7, 20));
    expect(stream.state()?.sequence).toBe(7);
    expect(stream.state()?.run?.simulationMinute).toBe(20);
  });

  it('cancels stale scenario requests, stops the old connection and ignores its late messages', async () => {
    stream.connect('scenario-a', 'run-a');
    await vi.advanceTimersByTimeAsync(0);
    const old = http.expectOne(request => request.url.includes('scenario-a'));
    stream.connect('scenario-b', 'run-b');
    expect(old.cancelled).toBe(true);
    await vi.advanceTimersByTimeAsync(0);
    recover({ ...state(0, 0, 'scenario-b'), run: null, runs: [] });
    connections[0].emit(frame(1, 50));
    expect(connections[0].stop).toHaveBeenCalled();
    expect(stream.state()?.scenarioId).toBe('scenario-b');
    expect(stream.state()?.sequence).toBe(0);
  });

  it('recovers on buffer overflow and invalidates its cursor when history was reset', async () => {
    await connect();
    for (let sequence = 2; sequence <= 135; sequence++) connections[0].emit(frame(sequence, 10));
    recover({ ...state(200, 30), resetRequired: true });
    expect(stream.state()?.sequence).toBe(200);
    expect(stream.state()?.run?.simulationMinute).toBe(30);
  });

  it('keeps completed results terminal despite a late running frame', async () => {
    await connect();
    const completed = { ...frame(1, 60), type: 'run.completed', status: 'completed', resultsValid: true };
    connections[0].emit(completed);
    recover({ ...state(1, 60), run: { ...state().run!, status: 'completed', simulationMinute: 60, resultsValid: true } });
    connections[0].emit(frame(2, 61));
    expect(stream.state()?.run?.status).toBe('completed');
  });
});

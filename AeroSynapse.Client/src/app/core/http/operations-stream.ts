import { effect, inject, Injectable, InjectionToken, OnDestroy, signal } from '@angular/core';
import { SessionState } from '../auth/session-state';
import { HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import { catchError, EMPTY, exhaustMap, merge, Subject, Subscription, timer, timeout } from 'rxjs';
import { ApiClient, API_BASE_URL } from './api-client';
import { OPERATIONS_RECOVERY_TIMEOUT_MS } from './operation-request';

export interface ProgressZone { zoneId: string; queuedPassengers: number | null; processedPassengers: number | null; notArrivedPassengers?: number | null }
export interface ProgressSnapshot { zones: ProgressZone[]; totalZones: number }
export interface OperationsRun {
  runId: string; status: string; revision: number; resultsValid: boolean;
  simulationMinute: number | null; snapshot: ProgressSnapshot | null;
}
export interface OptimizationSummary { optimizationId: string; baselineRunId: string | null; recommendedRunId: string | null; comparedRunCount: number }
export interface OperationsState {
  scenarioId: string; sequence: number; resetRequired: boolean; runs: OperationsRun[];
  run: OperationsRun | null; optimization: OptimizationSummary | null;
}
export interface OperationsMessage {
  scenarioId: string; sequence: number; type: string; runId: string | null; simulationMinute: number | null;
  status: string | null; resultsValid: boolean | null; snapshot: ProgressSnapshot | null; optimization: OptimizationSummary | null;
}
export interface OperationsConnection {
  state: HubConnectionState;
  start(): Promise<void>; stop(): Promise<void>; invoke(method: string, ...args: unknown[]): Promise<unknown>;
  on(method: string, handler: (message: OperationsMessage) => void): void;
  onreconnecting(handler: () => void): void; onreconnected(handler: () => void): void; onclose(handler: () => void): void;
}
export const OPERATIONS_CONNECTION_FACTORY = new InjectionToken<(url: string) => OperationsConnection>('OPERATIONS_CONNECTION_FACTORY', {
  providedIn: 'root', factory: () => url => new HubConnectionBuilder().withUrl(url, { withCredentials: true })
    .withAutomaticReconnect([0, 2000, 5000, 10000]).configureLogging(LogLevel.None).build(),
});

@Injectable()
export class OperationsStream implements OnDestroy {
  private readonly session = inject(SessionState);
  private hadSession = false;
  constructor() {
    effect(() => {
      if (this.session.session()) this.hadSession = true;
      else if (this.hadSession) { this.hadSession = false; this.connect(null); }
    });
  }
  private readonly api = inject(ApiClient);
  private readonly factory = inject(OPERATIONS_CONNECTION_FACTORY);
  private readonly hubUrl = `${inject(API_BASE_URL).replace(/\/$/, '')}/hubs/operations`;
  private readonly refresh = new Subject<void>();
  private connection?: OperationsConnection;
  private polling?: Subscription;
  private generation = 0;
  private scenarioId: string | null = null;
  private runId: string | null = null;
  private sequence = 0;
  private synchronizing = true;
  private nextRecovery = 0;
  private nextConnect = 0;
  private starting = false;
  private readonly pending = new Map<number, OperationsMessage>();
  readonly status = signal<'idle' | 'connecting' | 'live' | 'reconnecting' | 'polling'>('idle');
  readonly state = signal<OperationsState | null>(null);
  readonly error = signal('');
  readonly updates = new Subject<OperationsState>();

  connect(scenarioId: string | null, runId: string | null = null) {
    if (scenarioId === this.scenarioId) { this.trackRun(runId); return; }
    const generation = ++this.generation;
    const previous = this.connection;
    this.connection = undefined;
    this.polling?.unsubscribe();
    this.scenarioId = scenarioId; this.runId = runId; this.sequence = 0;
    this.pending.clear(); this.state.set(null); this.error.set('');
    this.synchronizing = true; this.nextRecovery = 0; this.nextConnect = 0; this.starting = false;
    if (!scenarioId) { this.status.set('idle'); void previous?.stop().catch(() => {}); return; }
    this.status.set('connecting');
    const connection = this.factory(this.hubUrl);
    this.connection = connection;
    for (const type of ['run.started', 'run.snapshot', 'run.completed', 'optimization.completed'])
      connection.on(type, message => { if (generation === this.generation && message.type === type) this.receive(message); });
    connection.onreconnecting(() => {
      if (generation !== this.generation) return;
      this.status.set('reconnecting'); this.synchronizing = true; this.recover();
    });
    connection.onreconnected(() => { if (generation === this.generation) void this.join(connection, generation); });
    connection.onclose(() => {
      if (generation !== this.generation) return;
      this.status.set('polling'); this.nextConnect = Date.now() + 5000; this.recover();
    });
    this.polling = merge(timer(0, 1000), this.refresh).pipe(exhaustMap(() => {
      if (generation !== this.generation) return EMPTY;
      if (connection.state === HubConnectionState.Disconnected && !this.starting && Date.now() >= this.nextConnect)
        void this.start(connection, generation, previous);
      else if (connection.state === HubConnectionState.Connected && this.status() === 'polling' && !this.starting && Date.now() >= this.nextConnect) {
        this.starting = true;
        void this.join(connection, generation).finally(() => { if (generation === this.generation) this.starting = false; });
      }
      if (Date.now() < this.nextRecovery) return EMPTY;
      // Connected clients also reconcile periodically to cover a missed message or server restart.
      this.nextRecovery = Date.now() + (this.status() === 'live' ? 15000 : 2000);
      const path = `scenarios/${encodeURIComponent(scenarioId)}/operations?afterSequence=${this.sequence}`
        + (this.runId ? `&runId=${encodeURIComponent(this.runId)}` : '');
      const requestedRun = this.runId;
      return this.api.get<OperationsState>(path).pipe(timeout(OPERATIONS_RECOVERY_TIMEOUT_MS), catchError(() => {
        if (generation === this.generation) this.error.set('Conexiunea nu este disponibilă. Urmărirea va continua la reconectare.');
        return EMPTY;
      }), exhaustMap(state => {
        if (generation === this.generation && requestedRun === this.runId) this.accept(state);
        return EMPTY;
      }));
    })).subscribe();
  }

  trackRun(runId: string | null) {
    if (runId === this.runId) return;
    this.runId = runId; this.synchronizing = true;
    this.state.update(state => state ? { ...state, run: null } : null);
    this.recover();
  }
  recover() { this.nextRecovery = 0; this.refresh.next(); }

  private async start(connection: OperationsConnection, generation: number, previous?: OperationsConnection) {
    this.starting = true;
    try {
      await previous?.stop().catch(() => {});
      if (generation !== this.generation) return;
      await connection.start();
      await this.join(connection, generation);
    } catch {
      if (generation === this.generation) { this.status.set('polling'); this.nextConnect = Date.now() + 5000; }
    } finally { if (generation === this.generation) this.starting = false; }
  }

  private async join(connection: OperationsConnection, generation: number) {
    if (generation !== this.generation) return;
    this.synchronizing = true;
    try {
      await connection.invoke('SubscribeScenario', this.scenarioId);
      if (generation !== this.generation) return;
      this.status.set('live'); this.recover();
    } catch {
      if (generation === this.generation) { this.status.set('polling'); this.nextConnect = Date.now() + 5000; this.recover(); }
    }
  }

  private accept(state: OperationsState) {
    if (state.scenarioId !== this.scenarioId || !Number.isSafeInteger(state.sequence) || state.sequence < 0) return;
    if (!state.resetRequired && state.sequence < this.sequence) { this.recover(); return; }
    this.sequence = state.sequence;
    this.state.set(state); this.error.set(''); this.synchronizing = false;
    for (const sequence of this.pending.keys()) if (sequence <= this.sequence) this.pending.delete(sequence);
    this.updates.next(state);
    this.drain();
  }

  private receive(message: OperationsMessage) {
    if (message.scenarioId !== this.scenarioId || !Number.isSafeInteger(message.sequence) || message.sequence <= this.sequence) return;
    if (message.simulationMinute !== null && (!Number.isFinite(message.simulationMinute) || message.simulationMinute < 0 || message.simulationMinute > 10080)) return;
    if (message.snapshot && (message.snapshot.zones.length > 100 || message.snapshot.zones.some(zone =>
      (zone.queuedPassengers !== null && (!Number.isInteger(zone.queuedPassengers) || zone.queuedPassengers < 0)) ||
      (zone.processedPassengers !== null && (!Number.isInteger(zone.processedPassengers) || zone.processedPassengers < 0)) ||
      (zone.notArrivedPassengers != null && (!Number.isInteger(zone.notArrivedPassengers) || zone.notArrivedPassengers < 0))))) return;
    this.pending.set(message.sequence, message);
    if (this.pending.size > 128) { this.pending.clear(); this.synchronizing = true; this.recover(); return; }
    this.drain();
  }

  private drain() {
    if (this.synchronizing || !this.state()) return;
    while (this.pending.has(this.sequence + 1)) {
      const message = this.pending.get(this.sequence + 1)!;
      this.pending.delete(message.sequence); this.sequence = message.sequence;
      const state = this.state()!;
      let runs = state.runs;
      let run = state.run;
      if (message.runId) {
        const previous = run?.runId === message.runId ? run : runs.find(item => item.runId === message.runId) ?? null;
        const minute = message.simulationMinute;
        // Never regress a run's simulated clock or overwrite terminal state with a late running frame.
        if (!(previous && ((minute !== null && previous.simulationMinute !== null && minute < previous.simulationMinute)
          || (['completed', 'failed'].includes(previous.status) && message.status === 'running')))) {
          const updated: OperationsRun = { runId: message.runId, status: message.status ?? previous?.status ?? 'running',
            revision: previous?.revision ?? 1, resultsValid: message.resultsValid ?? false,
            simulationMinute: minute ?? previous?.simulationMinute ?? null, snapshot: message.snapshot ?? previous?.snapshot ?? null };
          runs = [updated, ...runs.filter(item => item.runId !== updated.runId)].slice(0, 20);
          if (this.runId === updated.runId || (!this.runId && (message.type === 'run.started' || !run || run.runId === updated.runId))) run = updated;
        }
      }
      const next = { ...state, sequence: this.sequence, runs, run, optimization: message.optimization ?? state.optimization };
      this.state.set(next); this.updates.next(next);
      if (message.type === 'run.completed' || message.type === 'run.started') this.recover();
    }
    if (this.pending.size && !this.pending.has(this.sequence + 1)) { this.synchronizing = true; this.recover(); }
  }

  ngOnDestroy() {
    this.generation++; this.polling?.unsubscribe(); this.refresh.complete(); this.updates.complete();
    void this.connection?.stop().catch(() => {});
  }
}

import { DestroyRef, Injectable, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  EMPTY,
  Subject,
  Subscription,
  catchError,
  exhaustMap,
  finalize,
  forkJoin,
  of,
  switchMap,
  timeout,
  timer,
} from 'rxjs';
import { ApiClient } from '../../core/http/api-client';
import { ApiError } from '../../core/http/api-error.interceptor';
import { OPERATION_REQUEST_TIMEOUT_MS, operationError } from '../../core/http/operation-request';
import { validRunId } from '../operational-report/report.models';
import { RunDetails, RunSummary, ScenarioSummary } from '../operations/operations.models';
import {
  AlertAction,
  AlertActivity,
  AlertDetails,
  NotificationCommand,
  NotificationPage,
  NotificationReceipt,
  OperationalAlert,
  PublishQueueAlerts,
} from './notification.models';

export interface NotificationSelection {
  scenarioId: string | null;
  runId: string | null;
  alertId: string | null;
}
export interface NotificationFilters {
  level: string;
  acknowledged: string;
  fromUtc: string;
  toUtc: string;
  offset: number;
}

function notificationError(error: unknown): string {
  if (error instanceof ApiError && [400, 422].includes(error.status))
    return 'Datele introduse nu sunt valide. Verifică pragurile, perioada prognozei și câmpurile formularului.';
  return operationError(error).replace(/^Timeout:/, 'Timp de așteptare depășit:');
}

function runsMessage({ alertIds, duplicates, skippedRuns }: NotificationReceipt): string {
  const created = alertIds.length - duplicates;
  return (
    (alertIds.length
      ? `Gata: ${created} ${created === 1 ? 'alertă nouă' : 'alerte noi'}` +
        (duplicates ? `, ${duplicates} existau deja.` : '.')
      : 'Nicio coadă nu a depășit pragul de avertizare.') +
    (skippedRuns ? ` ${skippedRuns} rulări nu au date despre cozi și au fost sărite.` : '')
  );
}

@Injectable()
export class NotificationStore {
  private readonly api = inject(ApiClient);
  private readonly destroy = inject(DestroyRef);
  private readonly feedChanges = new Subject<void>();
  private readonly contexts = new Subject<NotificationSelection>();
  private catalogRequest?: Subscription;
  private commandRequest?: Subscription;
  private readonly storageKey = 'aerosynapse.notification-command.v1';
  readonly selection = signal<NotificationSelection>({
    scenarioId: null,
    runId: null,
    alertId: null,
  });
  readonly filters = signal<NotificationFilters>({
    level: '',
    acknowledged: '',
    fromUtc: '',
    toUtc: '',
    offset: 0,
  });
  readonly scenarios = signal<ScenarioSummary[]>([]);
  readonly runs = signal<RunSummary[]>([]);
  readonly run = signal<RunDetails | null>(null);
  readonly catalogLoading = signal(false);
  readonly contextLoading = signal(false);
  readonly loading = signal(false);
  readonly working = signal(false);
  readonly catalogError = signal('');
  readonly contextError = signal('');
  readonly error = signal('');
  readonly commandError = signal('');
  private validContext = true;
  readonly message = signal('');
  readonly alerts = signal<NotificationPage<OperationalAlert>>({
    items: [],
    total: 0,
    offset: 0,
    limit: 25,
  });
  readonly activity = signal<NotificationPage<AlertActivity>>({
    items: [],
    total: 0,
    offset: 0,
    limit: 25,
  });
  readonly detail = signal<AlertDetails | null>(null);
  readonly fresh = signal(false);
  readonly confirmedAt = signal<Date | null>(null);
  readonly pending = signal<NotificationCommand | null>(null);
  readonly accepted = new Subject<NotificationSelection>();
  readonly completed = new Subject<NotificationCommand>();
  readonly capture = computed(() => {
    const run = this.run();
    try {
      const value = JSON.parse(run?.inputSnapshotJson ?? 'null');
      if (
        !run ||
        value?.engine !== 'simpy-kpi-1.0' ||
        value.scenario?.scenario?.id !== this.selection().scenarioId ||
        value.scenario.scenario.isSynthetic !== true ||
        value.scenario.scenario.revision !== run.scenarioRevision ||
        value.scenario.inputs?.passengerGeneration?.flow
      )
        return null;
      const duration = value.scenario.inputs.durationMinutes,
        interval = value.snapshotIntervalMinutes ?? 1;
      return Number.isInteger(duration) &&
        duration > 0 &&
        duration <= 10080 &&
        [1, 2, 3, 4, 5].includes(interval)
        ? {
            duration,
            interval,
            scenarioName: String(value.scenario.scenario.name ?? this.selection().scenarioId),
          }
        : null;
    } catch {
      return null;
    }
  });

  readonly scenarioOptions = computed(() => {
    const rows = new Map(this.scenarios().map((s) => [s.id, { id: s.id, name: s.name }]));
    for (const alert of [
      ...this.alerts().items,
      ...(this.detail() ? [this.detail()!.alert] : []),
    ]) {
      if (!rows.has(alert.scenarioId))
        rows.set(alert.scenarioId, {
          id: alert.scenarioId,
          name: alert.scenarioName + ' · istoric',
        });
    }
    const id = this.selection().scenarioId,
      capture = this.capture();
    if (id && capture && !rows.has(id))
      rows.set(id, { id, name: capture.scenarioName + ' · capturat în rulare' });
    return [...rows.values()];
  });
  readonly selectedOutsidePage = computed(
    () => !!this.detail() && !this.alerts().items.some((a) => a.id === this.detail()!.alert.id),
  );

  constructor() {
    this.destroy.onDestroy(() => {
      this.commandRequest?.unsubscribe();
      this.accepted.complete();
      this.completed.complete();
    });
    this.contexts
      .pipe(
        switchMap((selection) => {
          this.contextLoading.set(!!selection.scenarioId);
          this.runs.set([]);
          this.run.set(null);
          this.contextError.set('');
          if (!selection.scenarioId) return of(null);
          const path = `scenarios/${selection.scenarioId}/runs`;
          return forkJoin({
            runs: this.api.get<RunSummary[]>(`${path}?limit=100`),
            run: selection.runId
              ? this.api.get<RunDetails>(`${path}/${selection.runId}`)
              : of(null),
          }).pipe(
            timeout(OPERATION_REQUEST_TIMEOUT_MS),
            catchError((error) => {
              this.contextError.set(notificationError(error));
              return of(null);
            }),
          );
        }),
        takeUntilDestroyed(this.destroy),
      )
      .subscribe((value) => {
        this.contextLoading.set(false);
        if (!value) return;
        if (
          value.run &&
          (value.run.id !== this.selection().runId ||
            value.run.scenarioId !== this.selection().scenarioId)
        ) {
          this.contextError.set('Rularea primită nu corespunde scenariului selectat.');
          return;
        }
        this.runs.set(value.runs);
        this.run.set(value.run);
      });
    this.feedChanges
      .pipe(
        switchMap(() => {
          if (!this.validContext) return EMPTY;
          const selection = this.selection(),
            filter = this.filters();
          const query = new URLSearchParams();
          if (selection.scenarioId) query.set('scenarioId', selection.scenarioId);
          if (selection.runId) query.set('runId', selection.runId);
          for (const key of ['level', 'acknowledged', 'fromUtc', 'toUtc'] as const)
            if (filter[key]) query.set(key, filter[key]);
          query.set('offset', `${filter.offset}`);
          query.set('limit', '25');
          const activityQuery = new URLSearchParams(query);
          activityQuery.set('offset', '0');
          return timer(0, 5000).pipe(
            exhaustMap((tick) => {
              // Show feedback for entry, filter changes and explicit refreshes. Periodic
              // reads keep the current content and controls stable while updating it.
              this.loading.set(tick === 0);
              return forkJoin({
                alerts: this.api.get<NotificationPage<OperationalAlert>>(
                  `operational-notifications?${query}`,
                ),
                activity: this.api.get<NotificationPage<AlertActivity>>(
                  `operational-notifications/activity?${activityQuery}`,
                ),
                detail: selection.alertId
                  ? this.api.get<AlertDetails>(`operational-notifications/${selection.alertId}`)
                  : of(null),
              }).pipe(
                timeout(OPERATION_REQUEST_TIMEOUT_MS),
                catchError((error) => {
                  this.error.set(notificationError(error));
                  this.fresh.set(false);
                  return EMPTY;
                }),
                finalize(() => this.loading.set(false)),
              );
            }),
          );
        }),
        takeUntilDestroyed(this.destroy),
      )
      .subscribe((value) => {
        const selection = this.selection(),
          detail = value.detail;
        if (
          value.alerts.items.some(
            (a) =>
              !a.isSynthetic ||
              (selection.scenarioId && a.scenarioId !== selection.scenarioId) ||
              (selection.runId && a.runId !== selection.runId),
          ) ||
          (detail &&
            (!detail.alert.isSynthetic ||
              detail.alert.id !== selection.alertId ||
              (selection.scenarioId && detail.alert.scenarioId !== selection.scenarioId) ||
              (selection.runId && detail.alert.runId !== selection.runId))) ||
          value.activity.items.some(
            (a) =>
              (selection.scenarioId && a.scenarioId !== selection.scenarioId) ||
              (selection.runId && a.runId !== selection.runId),
          )
        ) {
          this.error.set('Răspunsul de notificări nu corespunde contextului sintetic selectat.');
          this.fresh.set(false);
          this.detail.set(null);
          return;
        }
        this.alerts.set(value.alerts);
        this.activity.set(value.activity);
        this.detail.set(detail);
        this.error.set('');
        this.fresh.set(true);
        this.confirmedAt.set(new Date());
      });
    try {
      const saved = JSON.parse(localStorage.getItem(this.storageKey) ?? 'null');
      if (
        saved &&
        validRunId(saved.body?.commandId) &&
        ((saved.kind === 'publish' &&
          validRunId(saved.body.scenarioId) &&
          validRunId(saved.body.runId)) ||
          (saved.kind === 'runs' &&
            [saved.body.scenarioId, saved.body.runId].every((id) => id === null || validRunId(id))) ||
          (saved.kind === 'action' &&
            validRunId(saved.alertId) &&
            Number.isSafeInteger(saved.body.revision) &&
            ['acknowledge', 'comment', 'assign'].includes(saved.body.action)))
      ) {
        this.pending.set(saved);
        this.message.set(
          'Comandă restaurată. Reîncearcă aceeași comandă pentru a confirma rezultatul.',
        );
      }
    } catch {
      /* Optional storage must not block the page. */
    }
    this.loadCatalog();
  }
  loadCatalog() {
    this.catalogRequest?.unsubscribe();
    this.catalogLoading.set(true);
    this.catalogError.set('');
    this.catalogRequest = this.api
      .get<ScenarioSummary[]>('scenarios')
      .pipe(
        timeout(OPERATION_REQUEST_TIMEOUT_MS),
        finalize(() => this.catalogLoading.set(false)),
        takeUntilDestroyed(this.destroy),
      )
      .subscribe({
        next: (rows) =>
          this.scenarios.set(rows.filter((s) => s.isSynthetic === true && !s.deletedAtUtc)),
        error: (error) => this.catalogError.set(notificationError(error)),
      });
  }
  open(selection: NotificationSelection) {
    if (
      Object.values(selection).some((id) => id !== null && !validRunId(id)) ||
      (selection.runId && !selection.scenarioId)
    ) {
      this.validContext = false;
      this.detail.set(null);
      this.alerts.set({ items: [], total: 0, offset: 0, limit: 25 });
      this.activity.set({ items: [], total: 0, offset: 0, limit: 25 });
      this.contexts.next({ scenarioId: null, runId: null, alertId: null });
      this.refresh();
      this.error.set('Context invalid. Alege un scenariu sintetic și o rulare din catalog.');
      this.fresh.set(false);
      return;
    }
    this.validContext = true;
    const previous = this.selection();
    if (
      previous.scenarioId === selection.scenarioId &&
      previous.runId === selection.runId &&
      previous.alertId === selection.alertId &&
      this.confirmedAt()
    )
      return;
    this.selection.set(selection);
    this.detail.set(null);
    this.alerts.set({ items: [], total: 0, offset: 0, limit: 25 });
    this.activity.set({ items: [], total: 0, offset: 0, limit: 25 });
    this.filters.update((f) => ({ ...f, offset: 0 }));
    this.fresh.set(false);
    this.confirmedAt.set(null);
    if (
      previous.scenarioId !== selection.scenarioId ||
      previous.runId !== selection.runId ||
      !this.run()
    )
      this.contexts.next(selection);
    this.refresh();
  }
  applyFilters(filters: NotificationFilters) {
    if (
      (filters.fromUtc && !Number.isFinite(Date.parse(filters.fromUtc))) ||
      (filters.toUtc && !Number.isFinite(Date.parse(filters.toUtc))) ||
      (filters.fromUtc && filters.toUtc && Date.parse(filters.fromUtc) > Date.parse(filters.toUtc))
    ) {
      this.error.set(
        'Intervalul de timp este invalid. Data de început trebuie să fie înainte de sfârșit.',
      );
      return;
    }
    this.filters.set(filters);
    this.fresh.set(false);
    this.refresh();
  }
  refresh() {
    this.feedChanges.next();
  }
  publish(parameters: Omit<PublishQueueAlerts, 'commandId'>) {
    if (
      this.pending() ||
      this.working() ||
      !this.capture() ||
      this.contextLoading() ||
      !this.run() ||
      parameters.scenarioId !== this.selection().scenarioId ||
      parameters.runId !== this.run()!.id
    )
      return;
    this.save({
      kind: 'publish',
      body: { commandId: crypto.randomUUID(), ...structuredClone(parameters) },
    });
    this.retry();
  }
  /** Alerts on the recorded queues of every completed run in the selected scope, over the whole simulation. */
  publishRuns(warningThreshold: number, criticalThreshold: number) {
    if (this.pending() || this.working() || this.contextLoading()) return;
    const { scenarioId, runId } = this.selection();
    this.save({
      kind: 'runs',
      body: { commandId: crypto.randomUUID(), scenarioId, runId, warningThreshold, criticalThreshold },
    });
    this.retry();
  }
  act(action: AlertAction['action'], actor: string, text = '') {
    const alert = this.detail()?.alert;
    if (
      !alert ||
      !this.fresh() ||
      this.pending() ||
      this.working() ||
      !actor.trim() ||
      (action === 'acknowledge' && (alert.level === 'resolved' || alert.acknowledgedAtUtc)) ||
      (action === 'comment' && !text.trim())
    )
      return;
    this.save({
      kind: 'action',
      alertId: alert.id,
      body: {
        commandId: crypto.randomUUID(),
        revision: alert.revision,
        action,
        actor: actor.trim(),
        text: text.trim(),
      },
    });
    this.retry();
  }
  retry() {
    const command = this.pending();
    if (!command || this.working()) return;
    this.working.set(true);
    this.commandError.set('');
    const path =
      command.kind === 'publish'
        ? 'operational-notifications/queue-forecast'
        : command.kind === 'runs'
          ? 'operational-notifications/run-queues'
          : `operational-notifications/${command.alertId}/actions`;
    this.commandRequest = this.api
      .post<NotificationReceipt>(path, command.body)
      .pipe(
        timeout(OPERATION_REQUEST_TIMEOUT_MS),
        finalize(() => this.working.set(false)),
      )
      .subscribe({
        next: (receipt) => {
          if (
            receipt.commandId !== command.body.commandId ||
            !Array.isArray(receipt.alertIds) ||
            receipt.alertIds.some((id) => !validRunId(id)) ||
            (command.kind === 'action' &&
              (receipt.alertIds.length !== 1 || receipt.alertIds[0] !== command.alertId))
          ) {
            this.commandError.set(
              'Confirmarea nu corespunde comenzii. Reîncearcă aceeași comandă.',
            );
            return;
          }
          this.save(null);
          this.completed.next(command);
          this.message.set(
            command.kind === 'runs'
              ? runsMessage(receipt)
              : `Salvat și auditat. Duplicate ignorate: ${receipt.duplicates}; observații vechi ignorate: ${receipt.staleObservations}.`,
          );
          if (
            command.kind === 'publish' &&
            receipt.alertIds.length &&
            command.body.scenarioId === this.selection().scenarioId &&
            command.body.runId === this.selection().runId
          )
            this.accepted.next({
              scenarioId: command.body.scenarioId,
              runId: command.body.runId,
              alertId: receipt.alertIds[0],
            });
          this.refresh();
        },
        error: (error) => {
          if (error instanceof ApiError && [400, 404, 409, 422].includes(error.status)) {
            this.save(null);
            this.refresh();
          }
          this.commandError.set(notificationError(error));
        },
      });
  }
  private save(command: NotificationCommand | null) {
    this.pending.set(command);
    try {
      if (command) localStorage.setItem(this.storageKey, JSON.stringify(command));
      else localStorage.removeItem(this.storageKey);
    } catch {
      /* In-memory retry remains available. */
    }
  }
}

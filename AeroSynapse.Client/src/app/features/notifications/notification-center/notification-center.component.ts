import { DatePipe, DecimalPipe } from '@angular/common';
import { Component, computed, effect, inject, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { NotificationStore, NotificationSelection } from '../notification.store';
import {
  actionLabel,
  activityLabel,
  alertLevelLabel,
  OperationalAlert,
} from '../notification.models';
import { runStatus } from '../../control-tower/tower.models';
import { fromNow } from '../../../shared/ui/run-picker';

const ACTOR_KEY = 'aerosynapse.notification-actor.v1';
export const LEVELS = [
  { id: '', label: 'Toate' },
  { id: 'critical', label: 'Critice' },
  { id: 'warning', label: 'Avertizări' },
  { id: 'resolved', label: 'Rezolvate' },
] as const;

@Component({
  selector: 'app-notification-center',
  imports: [FormsModule, RouterLink, DatePipe, DecimalPipe],
  providers: [NotificationStore],
  templateUrl: './notification-center.component.html',
  styleUrl: './notification-center.component.css',
})
export class NotificationCenterComponent {
  readonly store = inject(NotificationStore);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  readonly activityLabel = activityLabel;
  readonly actionLabel = actionLabel;
  readonly levelLabel = alertLevelLabel;
  readonly statusLabel = runStatus;
  readonly fromNow = fromNow;
  readonly levels = LEVELS;
  /** The operator name is a per-browser convenience; the server still records it per action. */
  actor = readActor();
  comment = '';
  assignment: string | null = null;
  warningThreshold: number | null = null;
  criticalThreshold: number | null = null;
  horizonMinutes = 15;
  fromDate = '';
  toDate = '';
  level = '';
  acknowledged = '';
  private selectedAlert = '';
  readonly counts = computed(() =>
    this.store
      .alerts()
      .items.reduce((counts, alert) => ({ ...counts, [alert.level]: counts[alert.level] + 1 }), {
        warning: 0,
        critical: 0,
        resolved: 0,
      }),
  );
  /** A running simulation is forecast ahead; otherwise completed runs are checked over their whole period. */
  live() {
    return this.store.run()?.status === 'running';
  }
  canPublishRun() {
    const run = this.store.run();
    return (run?.status === 'completed' && run.resultsValid) || (this.live() && !!this.store.capture());
  }
  canPublish() {
    return (
      (!this.store.selection().runId || this.canPublishRun()) &&
      !this.store.working() &&
      !this.store.pending() &&
      !this.store.contextLoading() &&
      Number.isInteger(this.warningThreshold) &&
      this.warningThreshold! >= 0 &&
      Number.isInteger(this.criticalThreshold) &&
      this.criticalThreshold! > this.warningThreshold! &&
      this.criticalThreshold! <= 100000 &&
      (!this.live() ||
        (Number.isInteger(this.horizonMinutes) &&
          this.horizonMinutes >= 15 &&
          this.horizonMinutes <= 30))
    );
  }
  constructor() {
    this.route.queryParamMap.pipe(takeUntilDestroyed()).subscribe((params) => {
      const horizon = Number(params.get('horizonMinutes')),
        threshold = params.get('warningThreshold');
      if (
        params.has('horizonMinutes') &&
        Number.isInteger(horizon) &&
        horizon >= 15 &&
        horizon <= 30
      )
        this.horizonMinutes = horizon;
      if (
        threshold !== null &&
        Number.isInteger(Number(threshold)) &&
        Number(threshold) >= 0 &&
        Number(threshold) < 100000
      )
        this.warningThreshold = Number(threshold);
      this.store.open({
        scenarioId: params.get('scenarioId'),
        runId: params.get('runId'),
        alertId: params.get('alertId'),
      });
    });
    this.store.accepted
      .pipe(takeUntilDestroyed())
      .subscribe((selection) => this.navigate(selection));
    this.store.completed.pipe(takeUntilDestroyed()).subscribe((command) => {
      if (command.kind === 'action' && command.alertId === this.store.selection().alertId) {
        if (command.body.action === 'comment') this.comment = '';
        if (command.body.action === 'assign') this.assignment = null;
      }
    });
    effect(() => {
      const id = this.store.selection().alertId ?? '';
      untracked(() => {
        if (id !== this.selectedAlert) {
          this.selectedAlert = id;
          this.comment = '';
          this.assignment = null;
        }
      });
    });
  }
  private navigate(selection: NotificationSelection) {
    void this.router.navigate([], { relativeTo: this.route, queryParams: selection });
  }
  scenarioChanged(id: string) {
    this.navigate({ scenarioId: id || null, runId: null, alertId: null });
  }
  runChanged(id: string) {
    this.navigate({ ...this.store.selection(), runId: id || null, alertId: null });
  }
  selectAlert(id: string) {
    this.navigate({ ...this.store.selection(), alertId: id });
  }
  filter() {
    const utc = (value: string) =>
      !value
        ? ''
        : Number.isFinite(new Date(value).getTime())
          ? new Date(value).toISOString()
          : 'invalid';
    this.store.applyFilters({
      level: this.level,
      acknowledged: this.acknowledged,
      fromUtc: utc(this.fromDate),
      toUtc: utc(this.toDate),
      offset: 0,
    });
  }
  page(direction: number) {
    this.store.applyFilters({
      ...this.store.filters(),
      offset: Math.max(0, this.store.filters().offset + direction * 25),
    });
  }
  publish() {
    if (!this.canPublish()) return;
    if (!this.live()) return this.store.publishRuns(this.warningThreshold!, this.criticalThreshold!);
    this.store.publish({
      scenarioId: this.store.selection().scenarioId!,
      runId: this.store.run()!.id,
      fromMinute: 0,
      horizonMinutes: this.horizonMinutes,
      warningThreshold: this.warningThreshold!,
      criticalThreshold: this.criticalThreshold!,
      useLatest: true,
    });
  }
  links(alert: { scenarioId: string; runId: string; zoneId: string }) {
    return { scenarioId: alert.scenarioId, runId: alert.runId, zoneId: alert.zoneId };
  }
  setLevel(level: string) {
    this.level = level;
    this.filter();
  }
  toggleUnacknowledged() {
    this.acknowledged = this.acknowledged === 'false' ? '' : 'false';
    this.filter();
  }
  rememberActor(value: string) {
    this.actor = value;
    try {
      localStorage.setItem(ACTOR_KEY, value.trim());
    } catch {
      // The name only has to survive this page when storage is unavailable.
    }
  }
  recorded(alert: OperationalAlert) {
    return alert.source === 'recorded_queue';
  }
  /** Peak and both thresholds as percentages of one scale, for the severity gauge. */
  gauge(alert: OperationalAlert) {
    const peak = alert.evidence.peakQueuePassengers;
    const max = Math.max(peak, alert.criticalThreshold * 1.25, 1);
    const at = (value: number) => Math.min(100, (value / max) * 100);
    return { peak: at(peak), warning: at(alert.warningThreshold), critical: at(alert.criticalThreshold) };
  }
  /** Arrow keys move through the alert list like an inbox. */
  moveAlert(event: KeyboardEvent, index: number) {
    const items = this.store.alerts().items;
    const next = { ArrowDown: index + 1, ArrowUp: index - 1 }[event.key];
    if (next === undefined || next < 0 || next >= items.length) return;
    event.preventDefault();
    this.selectAlert(items[next].id);
    const list = (event.currentTarget as HTMLElement).parentElement;
    (list?.children[next] as HTMLElement | undefined)?.focus();
  }
}

function readActor() {
  try {
    return localStorage.getItem(ACTOR_KEY) ?? '';
  } catch {
    return '';
  }
}

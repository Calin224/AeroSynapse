import { DecimalPipe } from '@angular/common';
import {
  Component,
  computed,
  DestroyRef,
  effect,
  inject,
  signal,
  untracked,
  viewChild,
  WritableSignal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { SessionState } from '../../core/auth/session-state';
import { WriteAccess } from '../../core/auth/write-access';
import { fromNow, statusLabels } from '../../shared/ui/run-picker';
import { ScenarioDetails } from '../operations/operations.models';
import { FlightSchedulePanel } from '../operations/flight-schedule-panel';
import { PassengerGenerationPanel } from '../operations/passenger-generation-panel';
import { NavigationPanel } from '../operations/navigation-panel';
import { RunSettingsPanel } from '../operations/run-settings-panel';
import { EventsPanel } from '../operations/events-panel';
import { StaffingPanel } from '../operations/staffing-panel';
import { ResourcePanel } from '../operations/resource-panel';
import { QueueForecastPanel } from '../operations/queue-forecast-panel';
import { OperationsStream } from '../../core/http/operations-stream';
import { DigitalTwin } from '../digital-twin/digital-twin';
import { heatmapMinutes } from '../digital-twin/heatmap-model';
import { ControlTowerStore } from '../control-tower/control-tower.store';
import { RunOverview, runOverview } from '../control-tower/run-overview';
import { TowerTrends } from '../control-tower/tower-trends';
import { activeRun, runStatus } from '../control-tower/tower.models';
import { RunSummary } from './run-summary';
import { RevisionTimeline } from './revision-timeline';
import { AiOptimization } from './ai-optimization';
import { SimulationExecution, SimulationPanel } from './simulation-panel';

const TABS = [
  { id: 'schedule', phase: 'prepare', label: 'Orar zboruri', icon: 'pi-calendar' },
  { id: 'passengers', phase: 'prepare', label: 'Pasageri', icon: 'pi-users' },
  { id: 'routes', phase: 'prepare', label: 'Trasee', icon: 'pi-share-alt' },
  { id: 'events', phase: 'prepare', label: 'Evenimente', icon: 'pi-bolt' },
  { id: 'staffing', phase: 'prepare', label: 'Personal', icon: 'pi-id-card' },
  { id: 'resources', phase: 'prepare', label: 'Resurse integrate', icon: 'pi-box' },
  { id: 'runs', phase: 'results', label: 'Harta rulării', icon: 'pi-map' },
  { id: 'forecast', phase: 'results', label: 'Prognoza cozilor', icon: 'pi-chart-line' },
] as const;
type TabId = (typeof TABS)[number]['id'];
type Phase = (typeof TABS)[number]['phase'];

@Component({
  selector: 'app-scenario',
  imports: [
    DecimalPipe,
    RouterLink,
    DigitalTwin,
    SimulationPanel,
    RunSummary,
    RevisionTimeline,
    AiOptimization,
    TowerTrends,
    WriteAccess,
    FlightSchedulePanel,
    PassengerGenerationPanel,
    NavigationPanel,
    RunSettingsPanel,
    EventsPanel,
    StaffingPanel,
    ResourcePanel,
    QueueForecastPanel,
  ],
  providers: [ControlTowerStore, OperationsStream],
  templateUrl: './scenario.html',
  styleUrls: ['./scenario-workbench.css', '../control-tower/tower-panel.css', './scenario.css'],
})
export class Scenario {
  readonly store = inject(ControlTowerStore);
  readonly session = inject(SessionState);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  readonly simulationActive = signal(false);
  readonly focusedZoneId = signal<string | null>(null);
  /** The run whose AI option went into the scenario; its options are stale from then on. */
  readonly appliedRunId = signal<string | null>(null);
  private readonly focusedMinute = signal<number | null>(null);
  private readonly launcher = viewChild(SimulationPanel);
  protected readonly twin = viewChild(DigitalTwin);
  readonly statusLabel = (status: string) => statusLabels[status] ?? runStatus(status);
  /** Ticks every 30 s so relative times change between change-detection passes, never during one (NG0100). */
  private readonly now = signal(Date.now());
  readonly fromNow = (iso?: string | null) => fromNow(iso, this.now());
  readonly tab = signal<TabId>('runs');
  readonly phase = computed(() => TABS.find((item) => item.id === this.tab())!.phase);
  readonly tabs = computed(() => TABS.filter((item) => item.phase === this.phase()));
  /** Preparation is a sequence; offer the next step at the end of each one. */
  readonly nextTab = computed(() => {
    const tabs = this.tabs();
    return this.phase() === 'prepare'
      ? (tabs[tabs.findIndex((item) => item.id === this.tab()) + 1] ?? null)
      : null;
  });
  readonly galleryOpen = signal(false);
  readonly query = signal('');
  readonly visibleScenarios = computed(() => {
    const query = this.query().trim().toLocaleLowerCase('ro');
    return this.store
      .scenarios()
      .filter((item) =>
        `${item.name} ${item.code} ${item.description}`.toLocaleLowerCase('ro').includes(query),
      )
      .sort((a, b) => (b.updatedAtUtc ?? '').localeCompare(a.updatedAtUtc ?? ''));
  });
  readonly recentRuns = computed(() => this.store.runs().slice(0, 6));
  readonly scheduleBusy = signal(false);
  readonly passengerBusy = signal(false);
  readonly navigationBusy = signal(false);
  readonly runSettingsBusy = signal(false);
  readonly eventsBusy = signal(false);
  readonly resourcesBusy = signal(false);
  readonly staffingBusy = signal(false);
  private readonly panelActivity = [
    this.scheduleBusy,
    this.passengerBusy,
    this.navigationBusy,
    this.runSettingsBusy,
    this.eventsBusy,
    this.resourcesBusy,
    this.staffingBusy,
  ];
  readonly panelBusy = computed(() => this.panelActivity.some((flag) => flag()));
  readonly progress = computed(() => {
    if (!this.simulationActive() && !activeRun(this.store.status())) return null;
    const total = Math.max(
      1,
      activeRun(this.store.status())
        ? (this.store.context()?.scenario.inputs.durationMinutes ?? 1)
        : (this.store.selected()?.inputs.durationMinutes ?? 1),
    );
    const minute = this.store.progress()?.simulationMinute ?? 0;
    return { minute, total, percent: Math.min(100, Math.round((minute / total) * 100)) };
  });
  readonly launchError = computed(() => this.launcher()?.error() ?? '');
  readonly runFailure = computed(() => {
    const run = this.store.run();
    const execution = this.launcher()?.execution();
    return (
      run?.failureReason ??
      run?.error?.message ??
      (execution?.runId === this.store.selectedRunId() ? execution?.failureReason : null) ??
      ''
    );
  });
  readonly canResume = computed(() => !!this.launcher()?.command() && !!this.launchError());
  readonly submitting = computed(() => this.launcher()?.submitting() ?? false);
  readonly locked = computed(
    () =>
      this.simulationActive() ||
      activeRun(this.store.status()) ||
      this.store.loading() ||
      this.store.runLoading() ||
      this.panelBusy(),
  );
  readonly launchBlocked = computed(
    () =>
      this.panelBusy() ||
      this.store.loading() ||
      this.store.runLoading() ||
      this.store.runs().some((run) => activeRun(run.status)),
  );
  readonly overview = computed(() => {
    const run = this.store.run();
    return run?.status === 'completed' && run.resultsValid ? runOverview(run) : null;
  });
  readonly scene = computed(() => this.overview()?.scene ?? this.store.scene());
  readonly resources = computed(() => {
    const context = this.store.context();
    return (
      context?.layout.layout.checkpoints.map((cp) => {
        const allocations = context.scenario.inputs.staffAllocations.filter(
          (row) => row.checkpointId === cp.id,
        );
        return {
          ...cp,
          staff: allocations.length,
          people: allocations
            .map(
              (row) =>
                context.layout.layout.staff.find((person) => person.id === row.staffMemberId)
                  ?.displayName ?? row.staffMemberId,
            )
            .join(', '),
        };
      }) ?? []
    );
  });
  private shown = '';

  constructor() {
    const clock = setInterval(() => this.now.set(Date.now()), 30_000);
    inject(DestroyRef).onDestroy(() => clearInterval(clock));
    this.store.accepted.pipe(takeUntilDestroyed()).subscribe(({ browse, ...value }) => {
      if (browse) this.galleryOpen.set(true);
      const current = this.route.snapshot.queryParamMap;
      if (current.get('scenarioId') === value.scenarioId && current.get('runId') === value.runId)
        return;
      void this.router.navigate([], {
        relativeTo: this.route,
        queryParams: value,
        queryParamsHandling: 'merge',
        replaceUrl: true,
      });
    });
    this.route.queryParamMap.pipe(takeUntilDestroyed()).subscribe((params) => {
      this.focusedZoneId.set(params.get('zoneId'));
      const minute = Number(params.get('minute') ?? NaN);
      this.focusedMinute.set(Number.isFinite(minute) && minute >= 0 ? minute : null);
      this.store.open(params.get('scenarioId'), params.get('runId'));
    });
    effect(() => {
      const twin = this.twin(),
        minute = this.focusedMinute(),
        zone = this.focusedZoneId(),
        run = this.store.run()?.id;
      if (!twin || minute === null || !run || !this.store.heatmap()) return;
      const key = `${run}/${minute}/${zone}`;
      if (key === this.shown) return;
      this.shown = key;
      untracked(() => twin.playFrom(minute, zone ?? undefined));
    });
  }

  selectScenario(id: string) {
    if (this.locked()) return;
    this.galleryOpen.set(false);
    if (id === this.store.selected()?.scenario.id) return;
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { scenarioId: id, runId: null, zoneId: null, minute: null },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }
  selectRun(id: string) {
    if (this.simulationActive() || this.panelBusy()) return;
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { runId: id, zoneId: null, minute: null },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }
  follow(run: SimulationExecution) {
    this.galleryOpen.set(false);
    this.tab.set('runs');
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { scenarioId: run.scenarioId, runId: run.runId, zoneId: null, minute: null },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }
  restoreScenario(id: string) {
    if (id !== this.route.snapshot.queryParamMap.get('scenarioId'))
      void this.router.navigate([], {
        relativeTo: this.route,
        queryParams: { scenarioId: id, runId: null, zoneId: null, minute: null },
        queryParamsHandling: 'merge',
        replaceUrl: true,
      });
  }
  reload() {
    this.store.reload();
  }
  optionApplied(runId: string) {
    this.appliedRunId.set(runId);
    this.reload();
  }
  refreshPreview(runId: string) {
    if (this.store.selectedRunId() === runId) this.store.reload();
    else this.selectRun(runId);
  }
  playFromStart(overview: RunOverview) {
    const dataset = overview.scene.heatmap;
    this.twin()?.playFrom(dataset ? (heatmapMinutes(dataset)[0] ?? 0) : 0);
  }
  browse() {
    if (!this.locked()) this.galleryOpen.set(true);
  }
  run() {
    if (!this.session.canWrite() || this.launchBlocked() || this.simulationActive()) return;
    this.tab.set('runs');
    this.launcher()?.start();
  }
  resume() {
    this.launcher()?.resume();
  }
  showPhase(phase: Phase) {
    if (phase !== this.phase()) this.tab.set(phase === 'prepare' ? 'schedule' : 'runs');
  }
  openRun(id: string) {
    this.selectRun(id);
    this.tab.set('runs');
  }
  lockedExcept(own: WritableSignal<boolean>) {
    return (
      !this.session.canWrite() ||
      this.store.loading() ||
      this.store.runLoading() ||
      this.simulationActive() ||
      activeRun(this.store.status()) ||
      this.panelActivity.some((flag) => flag !== own && flag())
    );
  }
  openTab(id: TabId) {
    this.tab.set(id);
    const tab = document.getElementById(`tab-${id}`);
    tab?.focus({ preventScroll: true });
    tab?.scrollIntoView?.({ block: 'nearest', inline: 'center' });
    // The panel is still [hidden] until the next render, and hidden elements cannot scroll into view.
    requestAnimationFrame(() => document.getElementById(`tabpanel-${id}`)?.scrollIntoView?.({ block: 'start' }));
  }
  moveTab(event: KeyboardEvent) {
    const tabs = this.tabs(),
      index = tabs.findIndex((item) => item.id === this.tab());
    const next = { ArrowRight: index + 1, ArrowLeft: index - 1, Home: 0, End: tabs.length - 1 }[
      event.key
    ];
    if (next === undefined) return;
    event.preventDefault();
    const id = tabs[(next + tabs.length) % tabs.length].id;
    this.tab.set(id);
    (event.currentTarget as HTMLElement).parentElement
      ?.querySelector<HTMLElement>(`#tab-${id}`)
      ?.focus();
  }
  scheduleChanged(value: ScenarioDetails) {
    if (value.scenario.id !== this.store.selected()?.scenario.id) return;
    this.store.selected.set(value);
    this.store.scenarios.update((rows) =>
      rows.map((item) => (item.id === value.scenario.id ? value.scenario : item)),
    );
    const context = this.store.context();
    if (!this.store.run() && context) this.store.context.set({ ...context, scenario: value });
  }
  canLeave() {
    return !this.panelBusy();
  }
}

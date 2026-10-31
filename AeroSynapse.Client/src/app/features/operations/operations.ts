import { SessionState } from '../../core/auth/session-state';
import { SimulationContext } from '../../core/simulation-context';
import { WriteAccess } from '../../core/auth/write-access';
import { Component, computed, DestroyRef, inject, signal, WritableSignal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  catchError,
  EMPTY,
  exhaustMap,
  finalize,
  Subject,
  switchMap,
  takeWhile,
  timer,
} from 'rxjs';
import { ApiClient } from '../../core/http/api-client';
import { createDataResource } from '../../shared/data-resource';
import { CountUp } from '../../shared/ui/count-up';
import { DataState } from '../../shared/ui/data-state';
import { fromNow, statusLabels } from '../../shared/ui/run-picker';
import { OperationsStream } from '../../core/http/operations-stream';
import { FlightSchedulePanel } from './flight-schedule-panel';
import { PassengerGenerationPanel } from './passenger-generation-panel';
import { RunHeatmap } from '../digital-twin/run-heatmap';
import { NavigationPanel } from './navigation-panel';
import { RunSettingsPanel } from './run-settings-panel';
import { EventsPanel } from './events-panel';
import { StaffingPanel } from './staffing-panel';
import { ResourcePanel } from './resource-panel';
import { QueueForecastPanel } from './queue-forecast-panel';
import { RunDetails, RunSummary, ScenarioDetails, ScenarioSummary } from './operations.models';

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
const ACTIVE = ['pending', 'planned', 'running'];
const RECENT_RUNS = 6;

@Component({
  selector: 'app-operations',
  imports: [
    WriteAccess,
    CountUp,
    DataState,
    FlightSchedulePanel,
    RunHeatmap,
    PassengerGenerationPanel,
    NavigationPanel,
    RunSettingsPanel,
    EventsPanel,
    StaffingPanel,
    ResourcePanel,
    QueueForecastPanel,
  ],
  providers: [OperationsStream],
  templateUrl: './operations.html',
  styleUrl: './operations.css',
})
export class Operations {
  readonly session = inject(SessionState);
  private readonly api = inject(ApiClient);
  private readonly simulation = inject(SimulationContext);
  private readonly destroy = inject(DestroyRef);
  private readonly watch = new Subject<string | null>();
  readonly live = inject(OperationsStream);
  readonly scenarios = createDataResource(
    () => this.api.get<ScenarioSummary[]>('scenarios'),
    (data) => data.length === 0,
  );
  readonly tab = signal<TabId>('schedule');
  readonly phase = computed(() => TABS.find((item) => item.id === this.tab())!.phase);
  readonly tabs = computed(() => TABS.filter((item) => item.phase === this.phase()));
  readonly query = signal('');
  readonly selected = signal<ScenarioDetails | null>(null);
  readonly runs = signal<RunSummary[]>([]);
  readonly result = signal<RunDetails | null>(null);
  readonly busy = signal(false);
  readonly scheduleBusy = signal(false);
  readonly passengerBusy = signal(false);
  readonly navigationBusy = signal(false);
  readonly runSettingsBusy = signal(false);
  readonly eventsBusy = signal(false);
  readonly resourcesBusy = signal(false);
  readonly staffingBusy = signal(false);
  readonly message = signal('');
  readonly pollingError = signal('');
  private readonly router = inject(Router);
  private readonly activity = [
    this.busy,
    this.scheduleBusy,
    this.passengerBusy,
    this.navigationBusy,
    this.runSettingsBusy,
    this.eventsBusy,
    this.resourcesBusy,
    this.staffingBusy,
  ];
  /** Any load, run or panel save in flight: scenario switching and new runs wait for it. */
  readonly locked = computed(() => this.activity.some((working) => working()));
  readonly lastScenarioId = computed(() => this.simulation.current()?.scenarioId ?? null);
  readonly visibleScenarios = computed(() => {
    const state = this.scenarios.state();
    const query = this.query().trim().toLocaleLowerCase('ro');
    if (state.status !== 'ready') return [];
    return state.data
      .filter((scenario) =>
        `${scenario.name} ${scenario.code} ${scenario.description}`
          .toLocaleLowerCase('ro')
          .includes(query),
      )
      .sort((a, b) => (b.updatedAtUtc ?? '').localeCompare(a.updatedAtUtc ?? ''));
  });
  readonly recentRuns = computed(() => this.runs().slice(0, RECENT_RUNS));
  /** Live simulated minute of the run on screen, while it is still running. */
  readonly progress = computed(() => {
    const result = this.result();
    if (!result || !ACTIVE.includes(result.status)) return null;
    const live = this.live.state()?.run;
    const minute = live?.runId === result.id ? (live.simulationMinute ?? 0) : 0;
    const total = Math.max(1, this.selected()?.inputs.durationMinutes ?? 1);
    return { minute, total, percent: Math.min(100, Math.round((minute / total) * 100)) };
  });
  readonly statusLabel = (status: string) => statusLabels[status] ?? status;
  readonly fromNow = fromNow;

  constructor() {
    this.live.updates.pipe(takeUntilDestroyed(this.destroy)).subscribe((state) => {
      if (state.scenarioId !== this.selected()?.scenario.id) return;
      this.runs.update((items) =>
        items.map((item) => {
          const current = state.runs.find((run) => run.runId === item.id);
          return current ? { ...item, status: current.status, revision: current.revision } : item;
        }),
      );
      const current = state.run;
      const result = this.result();
      if (current && result?.id === current.runId && current.status !== result.status) {
        if (['completed', 'failed'].includes(current.status)) this.watch.next(current.runId);
        else this.result.set({ ...result, status: current.status, revision: current.revision });
      }
    });
    this.watch
      .pipe(
        switchMap((id) => {
          this.pollingError.set('');
          if (!id) return EMPTY;
          return timer(0, 1000).pipe(
            exhaustMap((tick) =>
              tick > 0 &&
              this.live.status() === 'live' &&
              !['completed', 'failed'].includes(this.live.state()?.run?.status ?? '')
                ? EMPTY
                : this.api.get<RunDetails>(`runs/${id}`).pipe(
                    catchError(() => {
                      this.pollingError.set('Reconectare la rezultate…');
                      return EMPTY;
                    }),
                  ),
            ),
            takeWhile((value) => ['pending', 'planned', 'running'].includes(value.status), true),
          );
        }),
        takeUntilDestroyed(this.destroy),
      )
      .subscribe((value) => {
        this.pollingError.set('');
        this.result.set(value);
        if (value.scenarioId === this.selected()?.scenario.id)
          this.simulation.remember(value.scenarioId!, value.id);
        this.runs.update((items) => items.map((item) => (item.id === value.id ? value : item)));
      });
    inject(ActivatedRoute)
      .queryParamMap.pipe(takeUntilDestroyed(this.destroy))
      .subscribe((params) => {
        const id = params.get('scenarioId');
        const runId = params.get('runId');
        if (id && id !== this.selected()?.scenario.id) this.select(id, runId);
        else if (runId && this.runs().some((run) => run.id === runId)) this.refreshRun(runId);
      });
  }

  create() {
    if (this.locked() || !this.session.canWrite()) return;
    void this.router.navigate(['/scenario-studio']);
  }
  select(id: string, runId: string | null = null) {
    if (this.locked()) return;
    this.busy.set(true);
    this.message.set('');
    this.watch.next(null);
    this.live.connect(null);
    this.selected.set(null);
    this.runs.set([]);
    this.result.set(null);
    this.api
      .get<ScenarioDetails>(`scenarios/${id}`)
      .pipe(
        switchMap((value) => {
          this.selected.set(value);
          this.simulation.remember(value.scenario.id);
          this.live.connect(value.scenario.id);
          return this.api.get<RunSummary[]>(`scenarios/${id}/runs`);
        }),
        finalize(() => this.busy.set(false)),
        takeUntilDestroyed(this.destroy),
      )
      .subscribe({
        next: (value) => {
          this.runs.set(value);
          if (runId) {
            if (value.some((run) => run.id === runId)) this.refreshRun(runId);
            else
              this.message.set(
                'Rularea selectată nu aparține scenariului sau nu mai este disponibilă.',
              );
          } else if (value[0]) this.refreshRun(value[0].id);
        },
        error: (error) => this.report(error),
      });
  }
  run() {
    const selected = this.selected();
    if (!selected || this.locked()) return;
    this.launch(`scenarios/${selected.scenario.id}/runs`, null);
  }
  /** Back to the scenario gallery; the live stream and any polling stop with the selection. */
  browse() {
    if (this.locked()) return;
    this.watch.next(null);
    this.live.connect(null);
    this.selected.set(null);
    this.runs.set([]);
    this.result.set(null);
    this.message.set('');
  }
  showPhase(phase: Phase) {
    if (phase !== this.phase()) this.tab.set(phase === 'prepare' ? 'schedule' : 'runs');
  }
  openRun(id: string) {
    if (id !== this.result()?.id) this.refreshRun(id);
    this.showPhase('results');
  }
  /** Panels are read-only for viewers and while another panel or run is working. */
  lockedExcept(own: WritableSignal<boolean>) {
    return (
      !this.session.canWrite() || this.activity.some((working) => working !== own && working())
    );
  }
  moveTab(event: KeyboardEvent) {
    const tabs = this.tabs();
    const last = tabs.length - 1;
    const index = tabs.findIndex((item) => item.id === this.tab());
    const next = { ArrowRight: index + 1, ArrowLeft: index - 1, Home: 0, End: last }[event.key];
    if (next === undefined) return;
    event.preventDefault();
    const id = tabs[(next + last + 1) % (last + 1)].id;
    this.tab.set(id);
    (event.currentTarget as HTMLElement).parentElement
      ?.querySelector<HTMLElement>(`#tab-${id}`)
      ?.focus();
  }
  refreshRun(id: string) {
    if (id) {
      const scenarioId = this.selected()?.scenario.id;
      if (scenarioId && this.runs().some((run) => run.id === id))
        this.simulation.remember(scenarioId, id);
      this.live.trackRun(id);
      this.watch.next(id);
    }
  }
  private launch(path: string, body: unknown) {
    this.busy.set(true);
    this.message.set('');
    this.api
      .post<RunSummary>(path, body)
      .pipe(
        finalize(() => this.busy.set(false)),
        takeUntilDestroyed(this.destroy),
      )
      .subscribe({
        next: (value) => {
          this.runs.update((items) => [value, ...items]);
          this.result.set(null);
          this.showPhase('results');
          this.refreshRun(value.id);
        },
        error: (error) => this.report(error),
      });
  }
  private report(error: unknown) {
    this.message.set(error instanceof Error ? error.message : 'Operația nu a putut fi finalizată.');
  }
  scheduleChanged(value: ScenarioDetails) {
    this.selected.set(value);
    this.scenarios.reload();
  }
}

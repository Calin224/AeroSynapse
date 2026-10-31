import { SimulationJourney } from '../../shared/ui/simulation-journey';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import {
  afterNextRender,
  Component,
  computed,
  ElementRef,
  inject,
  Injector,
  signal,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed, toObservable, toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { of, switchMap } from 'rxjs';
import { DataState } from '../../shared/ui/data-state';
import { DigitalTwin } from '../digital-twin/digital-twin';
import { heatmapMinutes } from '../digital-twin/heatmap-model';
import { RunOverview } from '../control-tower/run-overview';
import { RunSummary } from './run-summary';
import { ScenarioPreview, ScenarioStore } from './scenario.store';
import { SimulationPanel } from './simulation-panel';

@Component({
  selector: 'app-scenario',
  imports: [
    DataState,
    FormsModule,
    DigitalTwin,
    SimulationPanel,
    RunSummary,
    RouterLink,
    DatePipe,
    SimulationJourney,
  ],
  providers: [ScenarioStore],
  templateUrl: './scenario.html',
  styleUrl: './scenario.css',
})
export class Scenario {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);
  private readonly store = inject(ScenarioStore);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  protected readonly resource = this.store.resource;
  protected readonly selectedId = signal<string | null>(
    this.route.snapshot.queryParamMap.get('scenarioId'),
  );
  protected readonly selectedRunId = signal(this.route.snapshot.queryParamMap.get('runId'));
  protected readonly selected = computed(() => {
    const state = this.resource.state();
    return state.status === 'ready'
      ? state.data.find((item) => item.id === this.selectedId())
      : undefined;
  });
  protected readonly simulationActive = signal(false);
  private readonly revision = signal(0);
  protected readonly preview = toSignal(
    toObservable(
      computed(() => ({
        id: this.selected()?.id ?? null,
        runId: this.selectedRunId(),
        revision: this.revision(),
      })),
    ).pipe(
      switchMap(({ id, runId }) =>
        id ? this.store.preview(id, runId) : of<ScenarioPreview>({ status: 'none', runCount: 0 }),
      ),
    ),
    { initialValue: { status: 'loading' } as ScenarioPreview },
  );

  protected readonly twin = viewChild(DigitalTwin);
  constructor() {
    this.route.queryParamMap.pipe(takeUntilDestroyed()).subscribe((params) => {
      this.selectedId.set(params.get('scenarioId'));
      this.selectedRunId.set(params.get('runId'));
    });
  }
  protected playFromStart(overview: RunOverview) {
    const dataset = overview.scene.heatmap;
    this.twin()?.playFrom(dataset ? (heatmapMinutes(dataset)[0] ?? 0) : 0);
  }

  protected selectScenario(id: string) {
    if (this.simulationActive()) return;
    this.setScenario(id);
    afterNextRender(
      () => this.host.nativeElement.querySelector<HTMLElement>('#simulation-heading')?.focus(),
      { injector: this.injector },
    );
  }

  protected setScenario(id: string) {
    if (id === this.selectedId()) return;
    this.selectedId.set(id);
    this.selectedRunId.set(null);
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { scenarioId: id },
      replaceUrl: true,
    });
  }

  protected reload() {
    this.resource.reload();
    this.revision.update((value) => value + 1);
  }
  protected refreshPreview(runId?: string) {
    if (runId) this.selectedRunId.set(runId);
    this.revision.update((value) => value + 1);
  }
}

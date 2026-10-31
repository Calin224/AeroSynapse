import { WriteAccess } from '../../core/auth/write-access';
import { DecimalPipe } from '@angular/common';
import { Component, computed, effect, inject, signal, untracked, viewChild } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { OperationsStream } from '../../core/http/operations-stream';
import { DigitalTwin } from '../digital-twin/digital-twin';
import { BottleneckDiagnosisPanel } from './bottleneck-diagnosis-panel';
import { AgentPanel } from './agent-panel';
import { ControlTowerStore } from './control-tower.store';
import { TowerTrends } from './tower-trends';
import { activeRun, runStatus } from './tower.models';

@Component({
  selector: 'app-control-tower',
  imports: [
    WriteAccess,
    ButtonModule,
    RouterLink,
    DigitalTwin,
    TowerTrends,
    DecimalPipe,
    BottleneckDiagnosisPanel,
    AgentPanel,
  ],
  providers: [ControlTowerStore, OperationsStream],
  templateUrl: './control-tower.html',
  styleUrls: ['./tower-panel.css', './control-tower.css'],
})
export class ControlTower {
  readonly store = inject(ControlTowerStore);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  readonly focusedZoneId = signal<string | null>(null);
  /** The minute a link asked the map to show, with focusedZoneId (an agent decision, for instance). */
  readonly focusedMinute = signal<number | null>(null);
  private readonly twin = viewChild(DigitalTwin);
  private shown = '';
  readonly focusedZone = computed(
    () =>
      this.store
        .context()
        ?.layout.layout.checkpoints.find((cp) => cp.id === this.focusedZoneId()) ?? null,
  );
  readonly statusLabel = runStatus;
  readonly active = activeRun;
  readonly resources = computed(() => {
    const context = this.store.context();
    return (
      context?.layout.layout.checkpoints.map((cp) => ({
        ...cp,
        staff: context.scenario.inputs.staffAllocations.filter((row) => row.checkpointId === cp.id)
          .length,
        people: context.scenario.inputs.staffAllocations
          .filter((row) => row.checkpointId === cp.id)
          .map(
            (row) =>
              context.layout.layout.staff.find((person) => person.id === row.staffMemberId)
                ?.displayName ?? row.staffMemberId,
          )
          .join(', '),
      })) ?? []
    );
  });
  constructor() {
    this.store.accepted.pipe(takeUntilDestroyed()).subscribe((value) => {
      const current = this.route.snapshot.queryParamMap;
      if (current.get('scenarioId') === value.scenarioId && current.get('runId') === value.runId)
        return;
      void this.router.navigate([], {
        relativeTo: this.route,
        queryParams: value,
        replaceUrl: true,
      });
    });
    this.route.queryParamMap.pipe(takeUntilDestroyed()).subscribe((params) => {
      this.focusedZoneId.set(params.get('zoneId'));
      const minute = Number(params.get('minute') ?? NaN);
      this.focusedMinute.set(Number.isInteger(minute) && minute >= 0 ? minute : null);
      this.store.open(params.get('scenarioId'), params.get('runId'));
    });
    // Once the run's frames are loaded, move the map to the asked minute and zone, once per link.
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
  selectScenario(event: Event) {
    const scenarioId = (event.target as HTMLSelectElement).value;
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { scenarioId, runId: null },
    });
  }
  selectRun(event: Event) {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: {
        scenarioId: this.store.selected()?.scenario.id,
        runId: (event.target as HTMLSelectElement).value || null,
      },
    });
  }
}

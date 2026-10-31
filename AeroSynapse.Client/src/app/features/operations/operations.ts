import { Component, computed, DestroyRef, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { catchError, EMPTY, finalize, Subject, switchMap, takeWhile, timer } from 'rxjs';
import { ApiClient } from '../../core/http/api-client';
import { createDataResource } from '../../shared/data-resource';
import { DataState } from '../../shared/ui/data-state';
import {
  PublishedLayout,
  RunDetails,
  RunSummary,
  ScenarioDetails,
  ScenarioInputs,
  ScenarioSummary,
  SceneSummary,
} from './operations.models';

@Component({
  selector: 'app-operations',
  imports: [FormsModule, DataState],
  templateUrl: './operations.html',
  styleUrl: './operations.css',
})
export class Operations {
  private readonly api = inject(ApiClient);
  private readonly destroy = inject(DestroyRef);
  private readonly watch = new Subject<string | null>();
  readonly airports = createDataResource(
    () => this.api.get<SceneSummary[]>('scenes'),
    (data) => data.length === 0,
  );
  readonly scenarios = createDataResource(
    () => this.api.get<ScenarioSummary[]>('scenarios'),
    (data) => data.length === 0,
  );
  readonly selected = signal<ScenarioDetails | null>(null);
  readonly runs = signal<RunSummary[]>([]);
  readonly result = signal<RunDetails | null>(null);
  readonly busy = signal(false);
  readonly message = signal('');
  readonly pollingError = signal('');
  readonly checkpointNames = computed<Record<string, string>>(() => {
    try {
      const input = JSON.parse(this.result()?.inputSnapshotJson ?? '{}');
      return Object.fromEntries(
        (input.layout?.layout?.checkpoints ?? [])
          .filter(
            (item: { id: unknown; name: unknown }) =>
              typeof item.id === 'string' && typeof item.name === 'string',
          )
          .map((item: { id: string; name: string }) => [item.id, item.name]),
      );
    } catch {
      return {};
    }
  });
  airportId = '';
  code = '';
  name = '';
  description = '';
  duration = 60;
  passengers = 120;
  seed = 42;

  constructor() {
    this.watch
      .pipe(
        switchMap((id) => {
          this.pollingError.set('');
          if (!id) return EMPTY;
          return timer(0, 1000).pipe(
            switchMap(() => this.api.get<RunDetails>(`runs/${id}`)),
            takeWhile((value) => value.status === 'planned' || value.status === 'running', true),
            catchError(() => {
              this.pollingError.set('Rezultatul nu poate fi încărcat. Apasă Actualizează rularea.');
              return EMPTY;
            }),
          );
        }),
        takeUntilDestroyed(this.destroy),
      )
      .subscribe((value) => {
        this.result.set(value);
        this.runs.update((items) => items.map((item) => (item.id === value.id ? value : item)));
      });
  }

  create() {
    if (this.busy()) return;
    if (
      !this.airportId ||
      !this.code.trim() ||
      !this.name.trim() ||
      !Number.isInteger(this.duration) ||
      this.duration < 1 ||
      this.duration > 10080 ||
      !Number.isInteger(this.passengers) ||
      this.passengers < 1 ||
      this.passengers > 2147483647 ||
      !Number.isInteger(this.seed) ||
      this.seed < 0 ||
      this.seed > 2147483647
    ) {
      this.message.set(
        'Completează aeroportul, codul și numele. Durata: 1–10080 minute; pasageri: 1–2147483647; seed: 0–2147483647.',
      );
      return;
    }
    this.busy.set(true);
    this.message.set('');
    // A published layout freezes the catalog used to build the complete aggregate.
    this.api
      .post<PublishedLayout>(`scenes/${this.airportId}/layout-versions`)
      .pipe(
        switchMap((published) => {
          const gate = published.layout.gates.find((item) =>
            published.layout.routes.some((route) => route.toGateId === item.id),
          );
          const route = published.layout.routes.find((item) => item.toGateId === gate?.id);
          if (!gate || !route)
            throw new Error('Aeroportul nu are un traseu către o poartă disponibilă.');
          const start = new Date();
          start.setUTCMilliseconds(0);
          const flightId = crypto.randomUUID();
          const populationId = crypto.randomUUID();
          const inputs: ScenarioInputs = {
            startsAtUtc: start.toISOString(),
            durationMinutes: this.duration,
            seed: this.seed,
            timeZone: 'UTC',
            flights: [
              {
                id: flightId,
                code: `${this.code}-01`,
                gateId: gate.id,
                departureAtUtc: new Date(+start + this.duration * 60000).toISOString(),
                passengerCapacity: this.passengers,
              },
            ],
            populations: [
              { id: populationId, name: 'Pasageri la plecare', count: this.passengers },
            ],
            flows: [
              {
                id: crypto.randomUUID(),
                populationId,
                flightId,
                routeId: route.id,
                passengerCount: this.passengers,
                startsAtUtc: start.toISOString(),
                endsAtUtc: new Date(+start + this.duration * 60000).toISOString(),
              },
            ],
            staffAllocations: published.layout.staff
              .filter(
                (item) =>
                  item.checkpointId &&
                  published.layout.checkpoints.some(
                    (checkpoint) => checkpoint.id === item.checkpointId,
                  ),
              )
              .map((item) => ({ staffMemberId: item.id, checkpointId: item.checkpointId! })),
            events: [],
          };
          return this.api.post<ScenarioDetails>('scenarios', {
            code: this.code.trim(),
            name: this.name.trim(),
            description: this.description,
            sceneId: this.airportId,
            layoutVersionId: published.version.id,
            inputs,
          });
        }),
        finalize(() => this.busy.set(false)),
        takeUntilDestroyed(this.destroy),
      )
      .subscribe({
        next: (value) => {
          this.watch.next(null);
          this.selected.set(value);
          this.runs.set([]);
          this.result.set(null);
          this.scenarios.reload();
        },
        error: (error) => this.report(error),
      });
  }
  select(id: string) {
    if (this.busy()) return;
    this.busy.set(true);
    this.message.set('');
    this.watch.next(null);
    this.selected.set(null);
    this.runs.set([]);
    this.result.set(null);
    this.api
      .get<ScenarioDetails>(`scenarios/${id}`)
      .pipe(
        switchMap((value) => {
          this.selected.set(value);
          return this.api.get<RunSummary[]>(`scenarios/${id}/runs`);
        }),
        finalize(() => this.busy.set(false)),
        takeUntilDestroyed(this.destroy),
      )
      .subscribe({ next: (value) => this.runs.set(value), error: (error) => this.report(error) });
  }
  run() {
    const selected = this.selected();
    if (!selected || this.busy()) return;
    this.launch(`scenarios/${selected.scenario.id}/runs`, null);
  }
  retry() {
    const result = this.result();
    if (!result || result.status !== 'failed' || this.busy()) return;
    this.launch(`runs/${result.id}/retry`, { revision: result.revision });
  }
  refreshRun(id: string) {
    if (id) this.watch.next(id);
  }
  metricName(name: string): string {
    return (
      (
        {
          meanWaitMinutes: 'Așteptare medie',
          throughputPerMinute: 'Pasageri procesați pe minut',
          processedPassengers: 'Pasageri procesați',
        } as Record<string, string>
      )[name] ?? name
    );
  }
  checkpointName(id: string): string {
    return this.checkpointNames()[id] ?? `Checkpoint ${id.slice(0, 8)}`;
  }
  snapshotCounts(
    payload: string,
  ): { elapsedMinutes: number; queuedPassengers: number; processedPassengers: number } | null {
    try {
      const value = JSON.parse(payload);
      return value &&
        ['elapsedMinutes', 'queuedPassengers', 'processedPassengers'].every(
          (key) => typeof value[key] === 'number' && Number.isFinite(value[key]),
        )
        ? value
        : null;
    } catch {
      return null;
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
          this.refreshRun(value.id);
        },
        error: (error) => this.report(error),
      });
  }
  private report(error: unknown) {
    this.message.set(error instanceof Error ? error.message : 'Operația nu a putut fi finalizată.');
  }
}

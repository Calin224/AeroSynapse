import { DecimalPipe } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { Component, computed, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { catchError, finalize, forkJoin, of, Subscription, timeout } from 'rxjs';
import { ApiError } from '../../core/http/api-error.interceptor';
import { runtimeConfig } from '../../core/http/runtime-config';
import {
  AirsideAllocation,
  AirsideKpis,
  AirsideOptimization,
  AirsideResult,
  AirsideSkill,
  airsideDemoRequest,
  validateAirside,
} from './airside.models';

@Component({
  selector: 'app-airside-twin',
  imports: [FormsModule, DecimalPipe],
  templateUrl: './airside-twin.html',
  styleUrl: './airside-twin.css',
})
export class AirsideTwin {
  readonly Math = Math;
  private readonly http = inject(HttpClient);
  private readonly destroy = inject(DestroyRef);
  private readonly endpoint = `${runtimeConfig().intelligenceBaseUrl.replace(/\/$/, '')}/airside/v1`;
  private pending?: Subscription;
  draft = airsideDemoRequest();
  readonly members = signal(Array.from({ length: 6 }, (_, index) => `member-${index + 1}`));
  readonly busy = signal(false);
  readonly error = signal('');
  readonly optimizerError = signal('');
  readonly notice = signal('');
  readonly baseline = signal<AirsideResult | null>(null);
  readonly optimization = signal<AirsideOptimization | null>(null);
  readonly variant = signal<'baseline' | 'optimized'>('baseline');
  readonly minute = signal(0);
  readonly selected = computed(() =>
    this.variant() === 'optimized' ? (this.optimization()?.result ?? null) : this.baseline(),
  );
  readonly frame = computed(
    () => this.selected()?.snapshots.find((frame) => frame.minute === this.minute()) ?? null,
  );
  readonly lanes = computed(() => {
    const allocations = this.selected()?.allocations ?? [];
    const keys = [...new Set(allocations.map((row) => `${row.resourceType}:${row.resourceId}`))];
    return keys.map((key) => ({
      key,
      rows: allocations.filter((row) => `${row.resourceType}:${row.resourceId}` === key),
    }));
  });
  readonly metrics: { key: keyof AirsideKpis; label: string; unit: string }[] = [
    { key: 'turnaroundCount', label: 'Turnaround-uri sintetice', unit: 'aeronave' },
    { key: 'departedCount', label: 'Plecări simulate', unit: 'aeronave' },
    { key: 'backlogCount', label: 'Backlog la orizont', unit: 'aeronave' },
    { key: 'readyCount', label: 'Handling finalizat, așteaptă pushback', unit: 'aeronave' },
    { key: 'meanTurnaroundMinutes', label: 'Turnaround mediu (plecate)', unit: 'min' },
    { key: 'meanWaitingMinutes', label: 'Așteptare medie observată', unit: 'min' },
    { key: 'meanReadinessDelayMinutes', label: 'Întârziere readiness observată', unit: 'min' },
    { key: 'lateCount', label: 'Turnaround-uri întârziate', unit: 'aeronave' },
    { key: 'conflictCount', label: 'Conflicte / blocaje estimate', unit: 'evenimente' },
  ];
  readonly statuses: Record<string, string> = {
    pending: 'Neînceput / așteaptă stand',
    waiting: 'Așteaptă resurse',
    handling: 'Handling',
    ready: 'Handling finalizat, așteaptă pushback',
    pushback: 'Pushback demonstrativ',
    departed: 'Plecat',
  };
  readonly conflictReasons: Record<string, string> = {
    stand_occupied_or_unavailable: 'Stand ocupat sau indisponibil la momentul cerut',
    team_vehicle_or_member_unavailable: 'Echipă, membru sau vehicul indisponibil la momentul cerut',
    no_complete_available_path:
      'Nu există un interval complet cu toate resursele compatibile disponibile',
  };

  changed(): void {
    const hadOutput = !!this.baseline() || !!this.optimization() || this.busy();
    this.cancel(false);
    this.baseline.set(null);
    this.optimization.set(null);
    this.error.set('');
    this.optimizerError.set('');
    if (hadOutput)
      this.notice.set(
        'Configurația s-a schimbat. Rulează din nou pentru KPI și comparație actualizate.',
      );
  }
  cancel(showNotice = true): void {
    this.pending?.unsubscribe();
    this.pending = undefined;
    this.busy.set(false);
    if (showNotice) this.notice.set('Cereri anulate. Poți modifica scenariul sau rula din nou.');
  }
  run(): void {
    if (this.busy()) return;
    this.error.set('');
    this.optimizerError.set('');
    this.notice.set('');
    this.baseline.set(null);
    this.optimization.set(null);
    const errors = validateAirside(this.draft);
    if (errors.length) {
      this.error.set(errors.join(' '));
      return;
    }
    const request = structuredClone(this.draft);
    this.busy.set(true);
    this.pending = forkJoin({
      baseline: this.http
        .post<AirsideResult>(`${this.endpoint}/run`, structuredClone(request))
        .pipe(
          timeout(30_000),
          catchError((error: unknown) => {
            this.error.set(this.describeError(error));
            return of(null);
          }),
        ),
      optimized: this.http
        .post<AirsideOptimization>(`${this.endpoint}/optimize`, structuredClone(request))
        .pipe(
          timeout(30_000),
          catchError((error: unknown) => {
            this.optimizerError.set(this.describeError(error));
            return of(null);
          }),
        ),
    })
      .pipe(
        takeUntilDestroyed(this.destroy),
        finalize(() => this.busy.set(false)),
      )
      .subscribe(({ baseline, optimized }) => {
        if (
          (baseline && baseline.synthetic !== true) ||
          (optimized?.result && optimized.result.synthetic !== true)
        ) {
          this.error.set('Rezultatul nu confirmă date exclusiv sintetice; afișarea a fost oprită.');
          return;
        }
        this.baseline.set(baseline ? structuredClone(baseline) : null);
        if (
          baseline &&
          optimized?.result &&
          baseline.populationFingerprint !== optimized.result.populationFingerprint
        ) {
          this.optimizerError.set(
            'Populațiile diferă; comparația optimizerului nu poate fi afișată.',
          );
        } else {
          this.optimization.set(optimized ? structuredClone(optimized) : null);
        }
        this.variant.set(baseline ? 'baseline' : 'optimized');
        this.minute.set(request.durationMinutes);
      });
  }
  private describeError(error: unknown): string {
    return error instanceof ApiError && error.status === 404
      ? 'Modulul airside este dezactivat în Intelligence. Activează AIRSIDE_TWIN_ENABLED=true.'
      : error instanceof Error
        ? error.message
        : 'Cererea airside nu a reușit. Reîncearcă.';
  }
  toggle(values: string[], value: string, checked: boolean): void {
    const index = values.indexOf(value);
    if (checked && index < 0) values.push(value);
    if (!checked && index >= 0) values.splice(index, 1);
    this.changed();
  }
  toggleSkill(values: AirsideSkill[], value: AirsideSkill, checked: boolean): void {
    this.toggle(values, value, checked);
  }
  private nextId(prefix: string, ids: string[]): string {
    let number = 1;
    while (ids.includes(`${prefix}-${number}`)) number++;
    return `${prefix}-${number}`;
  }
  addMember(): void {
    if (this.members().length >= 40) return;
    this.members.update((members) => [...members, this.nextId('member', members)]);
  }
  addStand(): void {
    if (this.draft.stands.length >= 12) return;
    this.draft.stands.push({
      id: this.nextId(
        'stand',
        this.draft.stands.map((row) => row.id),
      ),
      available: [{ startMinute: 0, endMinute: this.draft.durationMinutes }],
    });
    this.changed();
  }
  removeStand(id: string): void {
    if (this.draft.stands.length <= 1 || this.draft.turnarounds.some((row) => row.standId === id))
      return;
    this.draft.stands = this.draft.stands.filter((row) => row.id !== id);
    for (const resource of [...this.draft.teams, ...this.draft.vehicles])
      resource.standIds = resource.standIds.filter((stand) => stand !== id);
    this.changed();
  }
  standInUse(id: string): boolean {
    return this.draft.turnarounds.some((row) => row.standId === id);
  }
  addTurnaround(): void {
    if (this.draft.turnarounds.length >= 30) return;
    this.draft.turnarounds.push({
      id: this.nextId(
        'turn',
        this.draft.turnarounds.map((row) => row.id),
      ),
      standId: this.draft.stands[0].id,
      arrivalMinute: 0,
      scheduledPushbackMinute: Math.min(25, this.draft.durationMinutes),
      handlingMinutes: 20,
      pushbackMinutes: 5,
    });
    this.changed();
  }
  addTeam(): void {
    if (this.draft.teams.length >= 20) return;
    this.draft.teams.push({
      id: this.nextId(
        'team',
        this.draft.teams.map((row) => row.id),
      ),
      memberIds: [this.members()[0]],
      skills: ['handling'],
      standIds: this.draft.stands.map((row) => row.id),
      available: [{ startMinute: 0, endMinute: this.draft.durationMinutes }],
    });
    this.changed();
  }
  addVehicle(): void {
    if (this.draft.vehicles.length >= 30) return;
    this.draft.vehicles.push({
      id: this.nextId(
        'vehicle',
        this.draft.vehicles.map((row) => row.id),
      ),
      kind: 'service',
      standIds: this.draft.stands.map((row) => row.id),
      available: [{ startMinute: 0, endMinute: this.draft.durationMinutes }],
    });
    this.changed();
  }
  remove(kind: 'teams' | 'vehicles' | 'turnarounds', id: string): void {
    if (this.draft[kind].length <= 1) return;
    if (kind === 'teams') this.draft.teams = this.draft.teams.filter((row) => row.id !== id);
    if (kind === 'vehicles')
      this.draft.vehicles = this.draft.vehicles.filter((row) => row.id !== id);
    if (kind === 'turnarounds')
      this.draft.turnarounds = this.draft.turnarounds.filter((row) => row.id !== id);
    this.changed();
  }
  occupying(standId: string): string {
    return (
      this.selected()?.allocations.find(
        (row) =>
          row.resourceType === 'stand' &&
          row.resourceId === standId &&
          row.startMinute <= this.minute() &&
          row.endMinute > this.minute(),
      )?.turnaroundId ?? 'Liber'
    );
  }
  left(row: AirsideAllocation): number {
    return Math.min(
      100,
      Math.max(0, (row.startMinute / (this.selected()?.request.durationMinutes ?? 1)) * 100),
    );
  }
  width(row: AirsideAllocation): number {
    return Math.max(
      0,
      Math.min(100, (row.endMinute / (this.selected()?.request.durationMinutes ?? 1)) * 100) -
        this.left(row),
    );
  }
}

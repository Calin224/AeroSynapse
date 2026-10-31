import { DecimalPipe } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { Component, computed, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { forkJoin, finalize, timeout } from 'rxjs';
import { ApiError } from '../../core/http/api-error.interceptor';
import { runtimeConfig } from '../../core/http/runtime-config';
import {
  BaggageKpis,
  BaggageRequest,
  BaggageResult,
  demoRequest,
  heatColor,
  RESOURCE_IDS,
  RESOURCE_LABELS,
} from './baggage.models';
import { BaggageView } from './baggage-view';

@Component({
  selector: 'app-baggage-twin',
  imports: [FormsModule, DecimalPipe, BaggageView],
  templateUrl: './baggage-twin.html',
  styleUrl: './baggage-twin.css',
})
export class BaggageTwin {
  private readonly http = inject(HttpClient);
  private readonly destroy = inject(DestroyRef);
  private readonly endpoint = `${runtimeConfig().intelligenceBaseUrl.replace(/\/$/, '')}/baggage/v1/run`;
  readonly ids = RESOURCE_IDS;
  readonly labels = RESOURCE_LABELS;
  readonly color = heatColor;
  readonly busy = signal(false);
  readonly error = signal('');
  readonly results = signal<{ baseline: BaggageResult; candidate: BaggageResult } | null>(null);
  readonly variant = signal<'baseline' | 'candidate'>('candidate');
  readonly minute = signal(0);
  readonly selected = computed(() => this.results()?.[this.variant()] ?? null);
  readonly frame = computed(() => this.selected()?.snapshots[this.minute()] ?? null);
  readonly metrics: { key: keyof BaggageKpis; label: string; unit: string }[] = [
    { key: 'loadedCount', label: 'Bagaje încărcate', unit: 'bagaje' },
    { key: 'throughputPerMinute', label: 'Throughput', unit: 'bagaje/min' },
    { key: 'backlogCount', label: 'Backlog la final', unit: 'bagaje' },
    { key: 'meanWaitingMinutes', label: 'Așteptare medie observată', unit: 'min/bagaj' },
    { key: 'meanDelayMinutes', label: 'Întârziere medie observată', unit: 'min/bagaj' },
    { key: 'lateCount', label: 'Bagaje întârziate', unit: 'bagaje' },
    { key: 'divertedCount', label: 'Bagaje deviate', unit: 'bagaje' },
  ];
  draft = demoRequest();
  incidentEnabled = true;
  incident = { ...demoRequest().incident! };
  extraSorting = 0;
  extraLoading = 0;
  readonly playing = signal(false);
  private timer?: ReturnType<typeof setInterval>;

  constructor() {
    this.destroy.onDestroy(() => this.stop());
  }

  run(): void {
    if (this.busy()) return;
    this.stop();
    this.error.set('');
    this.results.set(null);
    this.busy.set(true);
    const baseline: BaggageRequest = structuredClone({ ...this.draft, incident: null });
    const candidate: BaggageRequest = structuredClone(baseline);
    candidate.resources.sorting.capacity += this.extraSorting;
    candidate.resources.loading.capacity += this.extraLoading;
    candidate.incident = this.incidentEnabled ? { ...this.incident } : null;
    forkJoin({
      baseline: this.http.post<BaggageResult>(this.endpoint, baseline),
      candidate: this.http.post<BaggageResult>(this.endpoint, candidate),
    })
      .pipe(
        timeout(30_000),
        takeUntilDestroyed(this.destroy),
        finalize(() => this.busy.set(false)),
      )
      .subscribe({
        next: (pair) => {
          if (pair.baseline.populationFingerprint !== pair.candidate.populationFingerprint) {
            this.error.set('Populațiile diferă; comparația nu poate fi afișată.');
            return;
          }
          this.results.set(pair);
          this.variant.set('candidate');
          this.minute.set(pair.candidate.request.durationMinutes);
        },
        error: (error: unknown) =>
          this.error.set(
            error instanceof ApiError && error.status === 404
              ? 'Modulul de bagaje este dezactivat în serviciul Intelligence. Activează BAGGAGE_TWIN_ENABLED=true.'
              : error instanceof Error
                ? error.message
                : 'Simularea nu a reușit. Reîncearcă.',
          ),
      });
  }

  stop(): void {
    if (this.timer) clearInterval(this.timer);
    this.timer = undefined;
    this.playing.set(false);
  }
  play(): void {
    if (this.playing()) {
      this.stop();
      return;
    }
    const end = this.selected()?.request.durationMinutes;
    if (end === undefined) return;
    if (this.minute() >= end) this.minute.set(0);
    this.playing.set(true);
    this.timer = setInterval(() => {
      this.minute.update((value) => Math.min(value + 1, end));
      if (this.minute() >= end) this.stop();
    }, 400);
  }
  seek(value: number): void {
    this.stop();
    this.minute.set(value);
  }
}

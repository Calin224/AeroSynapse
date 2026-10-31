import { DecimalPipe, PercentPipe } from '@angular/common';
import { Component, computed, input } from '@angular/core';
import { QueueForecastResult, QueuePredictionResult } from './queue-forecast.models';

const FALLBACK: Record<string, string> = {
  predictor_disabled: 'Predictorul ML este dezactivat (QUEUE_ML_PREDICTOR=disabled); se afișează forecast-ul determinist.',
  model_warming: 'Modelul se antrenează din datasetul sintetic versionat; până atunci se afișează forecast-ul determinist.',
  model_unavailable: 'Antrenarea modelului a eșuat; se afișează forecast-ul determinist.',
};

/** Research comparison beside the forecast: the deterministic value at the horizon and, when trained, the ML estimate. */
@Component({
  selector: 'app-queue-prediction-card',
  imports: [DecimalPipe, PercentPipe],
  templateUrl: './queue-prediction-card.html',
  styleUrl: './queue-prediction-card.css',
})
export class QueuePredictionCard {
  readonly prediction = input<QueuePredictionResult | null>(null);
  readonly forecast = input.required<QueueForecastResult>();
  readonly names = input.required<Record<string, string>>();
  readonly selectedId = input<string | null>(null);
  readonly state = input<'idle' | 'loading' | 'failed'>('idle');
  readonly fallback = computed(() => {
    const predictor = this.prediction()?.predictor;
    return predictor?.kind === 'deterministic' ? FALLBACK[predictor.fallbackReason ?? ''] ?? FALLBACK['model_unavailable'] : null;
  });
  readonly rows = computed(() => {
    const prediction = this.prediction();
    if (!prediction) return [];
    return [...prediction.checkpoints].map(row => ({ ...row, delta: row.predictedQueuePassengers - row.deterministicQueuePassengers }))
      .sort((a, b) => (b.exceedanceProbability ?? b.predictedQueuePassengers) - (a.exceedanceProbability ?? a.predictedQueuePassengers));
  });
  name(id: string) { return this.names()[id] ?? `Checkpoint ${id.slice(0, 8)}`; }
  shortHash(value: string | null) { return value ? value.slice(0, 12) : '—'; }
}

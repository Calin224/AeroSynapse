import { DecimalPipe } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { finalize, Subscription, timeout } from 'rxjs';
import { ApiError } from '../../core/http/api-error.interceptor';
import { runtimeConfig } from '../../core/http/runtime-config';
import {
  CoefficientSource,
  EnergyComparison,
  EnergyOptimization,
  EnergyRequest,
  EnergyScenario,
  energyDemoRequest,
  energyGridCount,
  validateEnergy,
} from './energy.models';

@Component({
  selector: 'app-energy-analytics',
  imports: [FormsModule, DecimalPipe],
  templateUrl: './energy-analytics.html',
  styleUrl: './energy-analytics.css',
})
export class EnergyAnalytics {
  private readonly http = inject(HttpClient);
  private readonly destroy = inject(DestroyRef);
  private readonly endpoint = `${runtimeConfig().intelligenceBaseUrl.replace(/\/$/, '')}/energy/v1`;
  private comparePending?: Subscription;
  private optimizePending?: Subscription;
  draft = energyDemoRequest();
  readonly comparison = signal<EnergyComparison | null>(null);
  readonly optimization = signal<EnergyOptimization | null>(null);
  readonly comparing = signal(false);
  readonly optimizing = signal(false);
  readonly error = signal('');
  readonly optimizerError = signal('');
  readonly notice = signal('');
  readonly sourceLabels: Record<CoefficientSource, string> = {
    'configured-assumption': 'Ipoteză configurată',
    'default-assumption': 'Ipoteză implicită · coeficient lipsă',
  };
  readonly kindLabels = { checkpoint: 'Checkpoint', lighting: 'Lumini', resource: 'Resursă' };

  gridCount(): number {
    return energyGridCount(this.draft);
  }
  changed(): void {
    const hadOutput =
      !!this.comparison() || !!this.optimization() || this.comparing() || this.optimizing();
    this.cancel(false);
    this.comparison.set(null);
    this.optimization.set(null);
    this.error.set('');
    this.optimizerError.set('');
    this.notice.set(
      hadOutput ? 'Configurația s-a schimbat. Rulează din nou pentru rezultate actualizate.' : '',
    );
  }
  cancel(showNotice = true): void {
    this.comparePending?.unsubscribe();
    this.optimizePending?.unsubscribe();
    this.comparePending = undefined;
    this.optimizePending = undefined;
    this.comparing.set(false);
    this.optimizing.set(false);
    if (showNotice) this.notice.set('Cererile în curs au fost anulate.');
  }
  compare(): void {
    if (this.comparing()) return;
    this.error.set('');
    this.notice.set('');
    const request = this.validRequest(false);
    if (!request) return;
    this.comparison.set(null);
    this.comparing.set(true);
    this.comparePending = this.http
      .post<EnergyComparison>(`${this.endpoint}/compare`, request)
      .pipe(
        timeout(60_000),
        takeUntilDestroyed(this.destroy),
        finalize(() => this.comparing.set(false)),
      )
      .subscribe({
        next: (result) => {
          if (
            result.synthetic !== true ||
            result.request.synthetic !== true ||
            result.contractVersion !== '1.0'
          ) {
            this.error.set('Răspunsul nu confirmă contractul sintetic; afișarea a fost oprită.');
            return;
          }
          if (
            result.baseline.populationFingerprint !== result.candidate.populationFingerprint ||
            (this.optimization()?.best &&
              this.optimization()!.best!.populationFingerprint !==
                result.baseline.populationFingerprint)
          ) {
            this.error.set('Populațiile diferă; rezultatele nu pot fi comparate.');
            return;
          }
          this.comparison.set(structuredClone(result));
        },
        error: (error) => this.error.set(this.describeError(error)),
      });
  }
  optimize(): void {
    if (this.optimizing()) return;
    this.optimizerError.set('');
    this.notice.set('');
    const request = this.validRequest(true);
    if (!request) return;
    this.optimization.set(null);
    this.optimizing.set(true);
    this.optimizePending = this.http
      .post<EnergyOptimization>(`${this.endpoint}/optimize`, request)
      .pipe(
        timeout(60_000),
        takeUntilDestroyed(this.destroy),
        finalize(() => this.optimizing.set(false)),
      )
      .subscribe({
        next: (result) => {
          if (
            result.synthetic !== true ||
            result.request.synthetic !== true ||
            result.contractVersion !== '1.0'
          ) {
            this.optimizerError.set(
              'Răspunsul nu confirmă contractul sintetic; afișarea a fost oprită.',
            );
            return;
          }
          const scenarios = [...result.candidates, ...(result.best ? [result.best] : [])];
          const fingerprint =
            this.comparison()?.candidate.populationFingerprint ??
            scenarios[0]?.populationFingerprint;
          if (scenarios.some((row) => row.populationFingerprint !== fingerprint)) {
            this.optimizerError.set('Populațiile diferă; optimizarea nu poate fi comparată.');
            return;
          }
          this.optimization.set(structuredClone(result));
        },
        error: (error) => this.optimizerError.set(this.describeError(error)),
      });
  }
  private validRequest(optimizer: boolean): EnergyRequest | null {
    const errors = validateEnergy(this.draft);
    if (optimizer && (this.gridCount() > 128 || this.gridCount() < 1))
      errors.push('Optimizarea permite maximum 128 combinații. Restrânge intervalele min/max.');
    if (errors.length) {
      (optimizer ? this.optimizerError : this.error).set(errors.join(' '));
      return null;
    }
    return structuredClone(this.draft);
  }
  private describeError(error: unknown): string {
    return error instanceof ApiError && error.status === 404
      ? 'Modulul energetic este dezactivat în Intelligence. Activează ENERGY_ANALYTICS_ENABLED=true.'
      : error instanceof Error
        ? error.message
        : 'Cererea energetică nu a reușit. Reîncearcă.';
  }
  addCheckpoint(): void {
    if (this.draft.checkpoints.length >= 6) return;
    this.draft.checkpoints.push({
      id: crypto.randomUUID(),
      label: `Checkpoint sintetic ${this.draft.checkpoints.length + 1}`,
      minUnits: 0,
      maxUnits: 2,
      baselineUnits: 1,
      candidateUnits: 1,
      serviceRatePerMinute: 3,
    });
    this.changed();
  }
  removeCheckpoint(index: number): void {
    if (this.draft.checkpoints.length <= 1) return;
    this.draft.checkpoints.splice(index, 1);
    this.changed();
  }
  addLoad(): void {
    if (this.draft.loads.length >= 16) return;
    let index = 1;
    while (this.draft.loads.some((row) => row.id === `resursa-${index}`)) index++;
    this.draft.loads.push({
      id: `resursa-${index}`,
      kind: 'resource',
      units: 1,
      powerKw: null,
      baselineActiveMinutes: this.draft.durationMinutes,
      candidateActiveMinutes: this.draft.durationMinutes,
    });
    this.changed();
  }
  removeLoad(index: number): void {
    this.draft.loads.splice(index, 1);
    this.changed();
  }
  scenarioName(variant: EnergyScenario['variant']): string {
    return { baseline: 'Referință', candidate: 'Variantă', optimized: 'Optim de grilă' }[variant];
  }
  checkpointName(id: string): string {
    const request = this.comparison()?.request ?? this.optimization()?.request ?? this.draft;
    return request.checkpoints.find((row) => row.id === id)?.label ?? id;
  }
  assignmentLabel(scenario: EnergyScenario): string {
    return scenario.assignment
      .map((row) => `${this.checkpointName(row.checkpointId)}: ${row.activeUnits}`)
      .join(' · ');
  }
  budgetLabel(scenario: EnergyScenario): string {
    return scenario.energy.withinBudget === null
      ? 'Fără buget'
      : scenario.energy.withinBudget
        ? 'În buget'
        : 'Depășit';
  }
}

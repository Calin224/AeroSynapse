import { DecimalPipe } from '@angular/common';
import { Component, DestroyRef, effect, inject, input, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { finalize } from 'rxjs';
import { ApiClient } from '../../core/http/api-client';
import { PassengerBatchConfig, PassengerFlowConfig, PassengerFlowReport, PassengerGenerationConfig, PassengerGenerationResult,
  PassengerSettings, ScenarioDetails } from './operations.models';

const minutes = (minimum: number, maximum: number) =>
  ({ kind: 'uniform', minimum, maximum, mean: (minimum + maximum) / 2, stdDev: (maximum - minimum) / 6, mode: (minimum + maximum) / 2 });

@Component({ selector: 'app-passenger-generation-panel', imports: [FormsModule, DecimalPipe],
  templateUrl: './passenger-generation-panel.html', styleUrl: './passenger-generation-panel.css' })
export class PassengerGenerationPanel {
  private readonly api = inject(ApiClient);
  private readonly destroy = inject(DestroyRef);
  private readonly refresh = signal(0);
  readonly scenario = input.required<ScenarioDetails>();
  readonly disabled = input(false);
  readonly changed = output<ScenarioDetails>();
  readonly working = output<boolean>();
  readonly settings = signal<PassengerSettings | null>(null);
  readonly preview = signal<PassengerGenerationResult | null>(null);
  readonly flowReport = signal<PassengerFlowReport | null>(null);
  readonly loading = signal(false);
  readonly busy = signal(false);
  readonly message = signal('');
  readonly dirty = signal(false);
  readonly kinds = [{ value: 'uniform', label: 'Uniformă' }, { value: 'normal', label: 'Normală' },
    { value: 'triangular', label: 'Triunghiulară' }, { value: 'exponential', label: 'Exponențială' }, { value: 'constant', label: 'Constantă' }];
  readonly segmentLabels: Record<string, string> = { standard: 'Standard', holdBaggage: 'Bagaj de cală', reducedMobility: 'Mobilitate redusă', transfer: 'Transfer' };
  readonly stageLabels: Record<string, string> = { bagDrop: 'Predare bagaj de cală', security: 'Control securitate (checkpoint)',
    assistedSecurity: 'Control asistat', transferSecurity: 'Control transfer' };
  config: PassengerGenerationConfig | null = null;
  seed = 42;

  constructor() {
    effect(onCleanup => {
      const scenario = this.scenario(); this.refresh();
      untracked(() => {
        this.settings.set(null); this.preview.set(null); this.flowReport.set(null); this.config = null; this.dirty.set(false);
        if (!scenario.inputs.flights.length) return;
        this.loading.set(true);
        const subscription = this.api.get<PassengerSettings>(`scenarios/${scenario.scenario.id}/passengers/settings`)
          .pipe(finalize(() => this.loading.set(false)))
          .subscribe({ next: value => { this.settings.set(value); this.config = structuredClone(value.config ?? value.defaults); this.seed = value.seed; },
            error: error => this.report(error) });
        onCleanup(() => subscription.unsubscribe());
      });
    });
  }
  reload() { if (!this.busy()) { this.message.set(''); this.refresh.update(value => value + 1); } }
  modified() { this.dirty.set(true); this.preview.set(null); this.flowReport.set(null); }
  // Spread keeps the flight's own checkpoint as its reference; pinning a control also makes it the reference.
  laneChanged(batch: PassengerBatchConfig, value: string) {
    if (value === 'spread') batch.checkpointMode = 'spread';
    else { batch.checkpointMode = 'fixed'; batch.checkpointId = value; }
    this.modified();
  }
  countChanged(batch: PassengerBatchConfig) {
    if (batch.countMode === 'full') batch.modeledCount = this.flight(batch.flightId)?.capacity ?? 0;
    this.modified();
  }
  groupsChanged(batch: PassengerBatchConfig, preset: string) {
    batch.groups = preset === 'solo' ? [{ kind: 'solo', weight: 1, minSize: 1, maxSize: 1, serviceMultiplier: 1, travelMultiplier: 1 }]
      : [{ kind: 'solo', weight: .65, minSize: 1, maxSize: 1, serviceMultiplier: 1, travelMultiplier: 1 },
        { kind: 'family', weight: .25, minSize: 2, maxSize: 4, serviceMultiplier: 1.2, travelMultiplier: 1.15 },
        { kind: 'business', weight: .1, minSize: 1, maxSize: 1, serviceMultiplier: .8, travelMultiplier: .9 }];
    this.modified();
  }
  surgeChanged(batch: PassengerBatchConfig, enabled: boolean) {
    const width = batch.arrival.maximum - batch.arrival.minimum;
    batch.surges = enabled ? [{ startsMinute: batch.arrival.minimum + width / 2,
      endsMinute: Math.min(batch.arrival.maximum, batch.arrival.minimum + width / 2 + Math.min(5, width / 2)), share: .7 }] : [];
    this.modified();
  }
  addGroup(batch: PassengerBatchConfig) {
    const kind = ['solo', 'family', 'business', 'tour'].find(kind => !batch.groups.some(group => group.kind === kind));
    if (!kind) return;
    batch.groups.forEach(group => group.weight *= .9);
    batch.groups.push({ kind, weight: .1, minSize: kind === 'tour' ? 5 : kind === 'family' ? 2 : 1,
      maxSize: kind === 'tour' ? 12 : kind === 'family' ? 4 : 1, serviceMultiplier: 1.2, travelMultiplier: 1.1 });
    this.modified();
  }
  removeGroup(batch: PassengerBatchConfig, index: number) {
    if (batch.groups.length <= 1) return;
    batch.groups.splice(index, 1);
    const total = batch.groups.reduce((total, group) => total + group.weight, 0);
    batch.groups.forEach(group => group.weight = total > 0 ? group.weight / total : 1 / batch.groups.length);
    this.modified();
  }
  flowChanged(config: PassengerGenerationConfig, enabled: boolean) {
    config.flow = enabled ? this.flowPreset() : null;
    this.modified();
  }
  addClosure(flow: PassengerFlowConfig) {
    flow.closures.push({ stage: 'security', startsMinute: 10, endsMinute: 20 });
    this.modified();
  }
  removeClosure(flow: PassengerFlowConfig, index: number) {
    flow.closures.splice(index, 1);
    this.modified();
  }
  analyzeFlows() {
    const settings = this.settings();
    if (!settings?.config || this.blocked() || this.dirty()) return;
    this.busy.set(true); this.working.emit(true); this.message.set('');
    this.api.get<PassengerFlowReport>(`scenarios/${settings.scenarioId}/passengers/flows?revision=${settings.revision}`)
      .pipe(finalize(() => { this.busy.set(false); this.working.emit(false); }), takeUntilDestroyed(this.destroy))
      .subscribe({ next: value => this.flowReport.set(value), error: error => { this.flowReport.set(null); this.report(error); } });
  }
  save(config = this.config) {
    const settings = this.settings();
    if (!settings || this.blocked()) return;
    if (!Number.isInteger(this.seed) || this.seed < 0 || this.seed > 2147483647) { this.message.set('Seed-ul trebuie să fie un întreg între 0 și 2147483647.'); return; }
    this.busy.set(true); this.working.emit(true); this.message.set('');
    this.api.put<ScenarioDetails>(`scenarios/${settings.scenarioId}/passengers/settings`, { revision: settings.revision, seed: this.seed, config })
      .pipe(finalize(() => { this.busy.set(false); this.working.emit(false); }), takeUntilDestroyed(this.destroy))
      .subscribe({ next: value => { this.dirty.set(false); this.changed.emit(value); this.message.set('Populațiile demo și profilurile au fost salvate în scenariu.'); },
        error: error => this.report(error) });
  }
  generate(offset = 0) {
    const settings = this.settings();
    if (!settings || (!settings.config && !this.hasIncidents()) || this.blocked() || this.dirty()) return;
    this.busy.set(true); this.working.emit(true); this.message.set('');
    this.api.get<PassengerGenerationResult>(`scenarios/${settings.scenarioId}/passengers?revision=${settings.revision}&offset=${offset}&limit=100`)
      .pipe(finalize(() => { this.busy.set(false); this.working.emit(false); }), takeUntilDestroyed(this.destroy))
      .subscribe({ next: value => this.preview.set(value), error: error => { this.preview.set(null); this.report(error); } });
  }
  flight(id: string) { return this.settings()?.flights.find(flight => flight.id === id); }
  checkpointName(id: string) { return this.settings()?.checkpoints.find(checkpoint => checkpoint.id === id)?.name ?? id; }
  blocked() { return this.disabled() || this.loading() || this.busy(); }
  hasIncidents() { return this.scenario().inputs.events.some(event => event.type === 'incident.passenger-surge'); }
  // Synthetic starting point; every value stays editable and is validated by the shared contract.
  private flowPreset(): PassengerFlowConfig {
    return {
      segments: [
        { kind: 'standard', share: .45, holdBaggage: false, control: 'security', serviceMultiplier: 1, travelMultiplier: 1 },
        { kind: 'holdBaggage', share: .3, holdBaggage: true, control: 'security', serviceMultiplier: 1.1, travelMultiplier: 1 },
        { kind: 'reducedMobility', share: .1, holdBaggage: true, control: 'assistedSecurity', serviceMultiplier: 1.5, travelMultiplier: 2 },
        { kind: 'transfer', share: .15, holdBaggage: false, control: 'transferSecurity', serviceMultiplier: 1, travelMultiplier: 1, transferMinutes: minutes(3, 15) },
      ],
      stages: [{ kind: 'bagDrop', staffCount: 3, service: minutes(.5, 3) }, { kind: 'assistedSecurity', staffCount: 1, service: minutes(1, 3) },
        { kind: 'transferSecurity', staffCount: 1, service: minutes(.2, 1) }],
      closures: [],
    };
  }
  private report(error: unknown) { this.message.set(error instanceof Error ? error.message : 'Generarea pasagerilor nu a putut fi finalizată.'); }
}

import { Component, computed, DestroyRef, effect, inject, input, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { finalize, Subscription } from 'rxjs';
import { ApiClient } from '../../core/http/api-client';
import { ScenarioDetails } from './operations.models';
import { IntegratedResourceConfig, ResourceResult, ResourceSettings, ResourceWindow } from './resource.models';

@Component({
  selector: 'app-resource-panel', imports: [FormsModule], templateUrl: './resource-panel.html',
  styles: `:host { display: block; } fieldset { border: 1px solid var(--border-color, #64748b); border-radius: var(--radius); padding: 1rem; margin-bottom: 1rem; }
    label { display: inline-flex; flex-direction: column; gap: .3rem; margin: .4rem; }
    input[type=number] { width: 6rem; } select[multiple] { min-height: 5rem; min-width: 12rem; }
    .window { display: inline-flex; align-items: end; flex-wrap: wrap; } th, td { padding: .5rem; text-align: left; }
    table { width: 100%; } .scroll { overflow-x: auto; }`,
})
export class ResourcePanel {
  readonly scenario = input.required<ScenarioDetails>();
  readonly active = input(false);
  readonly disabled = input(false);
  readonly changed = output<ScenarioDetails>();
  readonly working = output<boolean>();
  readonly settings = signal<ResourceSettings | null>(null);
  readonly freeStaff = computed(() => this.settings()?.staff.filter(worker => !worker.reservedForSecurity) ?? []);
  readonly loading = signal(false);
  readonly busy = signal(false);
  readonly message = signal('');
  readonly result = signal<ResourceResult | null>(null);
  readonly dirty = signal(false);
  draft: IntegratedResourceConfig | null = null;
  timeLimitSeconds = 5;
  private readonly api = inject(ApiClient);
  private pending?: Subscription;
  private readonly scenarioContext = computed(() => `${this.scenario().scenario.id}/${this.scenario().scenario.revision}`);
  private context = '';

  constructor() {
    inject(DestroyRef).onDestroy(() => this.pending?.unsubscribe());
    effect(() => {
      const context = this.scenarioContext(); const active = this.active();
      untracked(() => {
        if (context !== this.context) {
          this.pending?.unsubscribe(); this.context = context;
          this.settings.set(null); this.result.set(null); this.draft = null; this.message.set(''); this.dirty.set(false);
        }
        if (active && !this.settings()) this.load();
      });
    });
  }
  load() {
    if (!this.active() || this.loading() || this.busy()) return;
    this.pending?.unsubscribe(); this.message.set(''); this.loading.set(true);
    const scenario = this.scenario().scenario;
    this.pending = this.api.get<ResourceSettings>(`scenarios/${scenario.id}/resources/settings`)
      .pipe(finalize(() => this.loading.set(false))).subscribe({ next: settings => {
        if (settings.scenarioId !== scenario.id || settings.revision !== scenario.revision) {
          this.message.set('Scenariul s-a modificat. Selectează-l din nou pentru revizia curentă.'); return;
        }
        this.settings.set(settings);
        this.draft = settings.resources ? structuredClone(settings.resources) : {
          contractVersion: '1.0', gates: [], workers: [],
          teams: [], requirements: settings.flights.map(flight => ({ flightId: flight.id, compatibleGateIds: [], requiredSkills: [], minimumTeamSize: 1 })),
        };
      }, error: error => this.report(error) });
  }
  edit() { this.dirty.set(true); this.result.set(null); this.message.set(''); }
  blocked() { return this.disabled() || this.loading() || this.busy(); }
  skills(text: string) { return text.split(',').map(item => item.trim()).filter(Boolean); }
  addWindow(windows: ResourceWindow[]) { windows.push({ startMinute: 0, endMinute: this.settings()!.durationMinutes }); this.edit(); }
  removeWindow(windows: ResourceWindow[], index: number) { windows.splice(index, 1); this.edit(); }
  hasGate(id: string) { return this.draft?.gates.some(item => item.gateId === id); }
  hasWorker(id: string) { return this.draft?.workers.some(item => item.staffMemberId === id); }
  includeGate(id: string) {
    if (this.draft && this.draft.gates.length < 50 && !this.hasGate(id) && this.settings()?.gates.some(gate => gate.id === id)) {
      this.draft.gates.push({ gateId: id, availability: [] }); this.edit();
    }
  }
  removeGate(id: string) {
    if (!this.draft) return;
    this.draft.gates = this.draft.gates.filter(gate => gate.gateId !== id);
    for (const team of this.draft.teams) team.gateIds = team.gateIds.filter(gate => gate !== id);
    for (const requirement of this.draft.requirements) requirement.compatibleGateIds = requirement.compatibleGateIds.filter(gate => gate !== id);
    this.edit();
  }
  includeWorker(id: string) {
    if (this.draft && this.draft.workers.length < 200 && !this.hasWorker(id) && this.freeStaff().some(worker => worker.id === id)) {
      this.draft.workers.push({ staffMemberId: id, skills: [], shifts: [] }); this.edit();
    }
  }
  removeWorker(id: string) {
    if (!this.draft) return;
    this.draft.workers = this.draft.workers.filter(worker => worker.staffMemberId !== id);
    for (const team of this.draft.teams) team.memberIds = team.memberIds.filter(member => member !== id);
    this.edit();
  }
  addTeam() {
    if (this.draft && this.draft.teams.length < 50) {
      this.draft.teams.push({ teamId: crypto.randomUUID(), name: 'Echipă handling', memberIds: [], gateIds: [] }); this.edit();
    }
  }
  removeTeam(index: number) { this.draft?.teams.splice(index, 1); this.edit(); }
  name(id: string | null) {
    const settings = this.settings();
    return settings?.flights.find(item => item.id === id)?.code ?? settings?.gates.find(item => item.id === id)?.name
      ?? settings?.staff.find(item => item.id === id)?.name ?? this.draft?.teams.find(item => item.teamId === id)?.name ?? id ?? '';
  }
  interval(id: string) {
    const settings = this.settings()!; const flight = settings.flights.find(item => item.id === id)!;
    const minute = (at: string) => (Date.parse(at) - Date.parse(settings.startsAtUtc)) / 60000 + (flight.delayMinutes ?? 0);
    return `${minute(flight.boardingOpensAtUtc!)}–${minute(flight.departureAtUtc)} min`;
  }
  assignmentInterval(flightId: string) {
    const flight = this.result()?.constraints.flights.find(item => item.flightId === flightId);
    return flight ? `${flight.startMinute}–${flight.endMinute} min` : '';
  }
  memberNames(teamId: string) {
    return this.result()?.constraints.resources.teams.find(team => team.teamId === teamId)?.memberIds.map(id => this.name(id)).join(', ') ?? '';
  }
  save(disable = false) {
    const settings = this.settings();
    if (this.blocked() || !settings || !disable && (!settings.compatible || !this.draft?.gates.length)) return;
    this.start();
    this.pending = this.api.put<ScenarioDetails>(`scenarios/${settings.scenarioId}/resources/settings`,
      { revision: settings.revision, resources: disable ? null : this.draft }).pipe(finalize(() => this.finish()))
      .subscribe({ next: value => { this.result.set(null); this.dirty.set(false); this.changed.emit(value); }, error: error => this.report(error) });
  }
  propose() {
    const settings = this.settings();
    if (this.blocked() || !settings?.compatible || !settings.resources || this.dirty()) return;
    if (!Number.isFinite(this.timeLimitSeconds) || this.timeLimitSeconds < 0 || this.timeLimitSeconds > 30) {
      this.message.set('Limita de timp trebuie să fie între 0 și 30 secunde.'); return;
    }
    this.result.set(null); this.start();
    this.pending = this.api.post<ResourceResult>(`scenarios/${settings.scenarioId}/resources/proposals`,
      { revision: settings.revision, timeLimitSeconds: this.timeLimitSeconds }).pipe(finalize(() => this.finish()))
      .subscribe({ next: result => {
        if (result.constraints.scenarioId !== settings.scenarioId || result.constraints.scenarioRevision !== settings.revision || !result.advisoryOnly) {
          this.message.set('Propunerea nu corespunde reviziei afișate. Reîncarcă scenariul.'); return;
        }
        this.result.set(result);
      }, error: error => this.report(error) });
  }
  status(value: ResourceResult) {
    return { optimal: 'Optim · număr minim de schimbări de poartă', feasible: 'Soluție fezabilă · limita de timp a oprit căutarea',
      infeasible: 'Infezabil · regulile nu pot fi respectate simultan', timeout: 'Timp expirat fără o soluție', invalid_model: 'Model invalid' }[value.status];
  }
  private start() { this.message.set(''); this.busy.set(true); this.working.emit(true); }
  private finish() { this.busy.set(false); this.working.emit(false); }
  private report(error: unknown) { this.message.set(error instanceof Error ? error.message : 'Operația a eșuat. Încearcă din nou.'); }
}

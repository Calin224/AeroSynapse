import { DecimalPipe } from '@angular/common';
import { Component, computed, DestroyRef, effect, inject, input, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { finalize, Subscription } from 'rxjs';
import { ApiClient } from '../../core/http/api-client';
import { clockTime } from '../../shared/absolute-time';
import { DigitalTwin } from '../digital-twin/digital-twin';
import { TwinScene } from '../digital-twin/twin-model';
import { NavigationConfig, NavigationNode, NavigationResult, NavigationSettings } from './navigation.models';
import { RunDetails, ScenarioDetails } from './operations.models';

const ZONE_GROUPS = [{ kind: 'Walkway', label: 'Coridoare' }, { kind: 'Security', label: 'Controale de securitate' }, { kind: 'Gate', label: 'Porți' }];

@Component({ selector: 'app-navigation-panel', imports: [FormsModule, DecimalPipe, DigitalTwin],
  templateUrl: './navigation-panel.html', styleUrl: './navigation-panel.css' })
export class NavigationPanel {
  private readonly api = inject(ApiClient);
  private readonly destroy = inject(DestroyRef);
  readonly scenario = input.required<ScenarioDetails>();
  readonly run = input<RunDetails | null>(null);
  readonly disabled = input(false);
  /** False while the tab is hidden, so the automatic impact preview waits until someone looks at it. */
  readonly active = input(true);
  readonly changed = output<ScenarioDetails>();
  readonly working = output<boolean>();
  /** Asks the page to open the step that unblocks this one. */
  readonly goTo = output<'schedule' | 'passengers'>();
  readonly settings = signal<NavigationSettings | null>(null);
  readonly result = signal<NavigationResult | null>(null);
  readonly loading = signal(false);
  readonly busy = signal(false);
  /** Previews are read-only, so they never lock the page; a newer one cancels the older. */
  readonly previewing = signal(false);
  readonly previewError = signal('');
  readonly dirty = signal(false);
  readonly message = signal('');
  readonly showingRun = signal<string | null>(null);
  readonly showRoutes = signal(false);
  readonly renderScene = computed<TwinScene>(() => ({ aircraft: [], navigationRoutes: this.result()?.routes ?? [] }));
  private previewSubscription?: Subscription;
  config: NavigationConfig | null = null;
  graphJson = '';
  airportMapId = '';
  constructor() {
    effect(onCleanup => {
      const scenario = this.scenario();
      untracked(() => {
        this.previewSubscription?.unsubscribe();
        this.settings.set(null); this.result.set(null); this.config = null; this.graphJson = ''; this.dirty.set(false); this.showingRun.set(null);
        if (!scenario.inputs.flights.length) return;
        this.loading.set(true);
        const subscription = this.api.get<NavigationSettings>(`scenarios/${scenario.scenario.id}/navigation/settings`)
          .pipe(finalize(() => this.loading.set(false)))
          .subscribe({ next: settings => { this.settings.set(settings); this.config = structuredClone(settings.config ?? settings.defaults); this.syncJson(); }, error: error => this.report(error) });
        onCleanup(() => subscription.unsubscribe());
      });
    });
    // Show the impact as soon as the step is open, instead of waiting for a button press.
    effect(() => {
      const ready = this.active() && !this.disabled() && this.settings()?.passengerProfilesActive;
      untracked(() => { if (ready && !this.result() && !this.previewing() && !this.dirty()) this.preview(); });
    });
  }
  private syncJson() { this.graphJson = JSON.stringify(this.config, null, 2); }
  modified() { this.previewSubscription?.unsubscribe(); this.dirty.set(true); this.result.set(null); this.showingRun.set(null); this.showRoutes.set(false); }
  private parse(): boolean {
    try {
      const config: unknown = JSON.parse(this.graphJson);
      if (!config || typeof config !== 'object' || Array.isArray(config)) throw new Error();
      const value = config as NavigationConfig;
      if (!value.graph || !Array.isArray(value.graph.nodes) || !Array.isArray(value.graph.edges)
        || !Array.isArray(value.closedZoneIds) || !Array.isArray(value.closedEdgeIds) || !Array.isArray(value.accessProfiles)
        || !Array.isArray(value.checkpointBindings) || !Array.isArray(value.gateBindings)
        || !value.graph.nodes.every(node => node && typeof node.id === 'string' && typeof node.name === 'string' && typeof node.kind === 'string')
        || ![...value.checkpointBindings, ...value.gateBindings].every(binding => binding && typeof binding.resourceId === 'string' && typeof binding.zoneId === 'string')) throw new Error();
      this.config = value; return true;
    } catch { this.message.set('Configurația trebuie să fie un obiect JSON cu graf, legături și profiluri de acces.'); return false; }
  }
  applyJson() { if (this.parse()) { this.modified(); this.message.set('JSON aplicat în editor. Salvează configurația pentru următoarea execuție.'); } }
  closeZone(id: string, closed: boolean) {
    if (!this.config || this.blocked()) return;
    this.config.closedZoneIds = closed ? [...new Set([...this.config.closedZoneIds, id])] : this.config.closedZoneIds.filter(item => item !== id);
    this.syncJson(); this.modified(); this.preview();
  }
  zoneGroups(nodes: NavigationNode[]) {
    const known = ZONE_GROUPS.map(group => group.kind);
    return [...ZONE_GROUPS.map(group => ({ label: group.label, nodes: nodes.filter(node => node.kind === group.kind) })),
      { label: 'Alte zone', nodes: nodes.filter(node => !known.includes(node.kind)) }].filter(group => group.nodes.length);
  }
  connections(id: string) {
    const graph = this.config?.graph;
    if (!graph) return 'Legături indisponibile';
    const outgoing = graph.edges.filter(edge => edge.isActive && (edge.fromZoneId === id || (edge.isBidirectional && edge.toZoneId === id)));
    const names = outgoing.map(edge => {
      const destination = edge.fromZoneId === id ? edge.toZoneId : edge.fromZoneId;
      const name = graph.nodes.find(node => node.id === destination)?.name ?? destination;
      return this.config?.closedEdgeIds.includes(edge.id) ? `${name} (legătură închisă)` : name;
    });
    return names.length ? `Legături de plecare pe hartă: ${names.join(', ')}.` : 'Nu există legături de plecare active în hartă.';
  }
  binding(kind: 'checkpointBindings' | 'gateBindings', id: string) { return this.config?.[kind].find(item => item.resourceId === id)?.zoneId ?? ''; }
  bind(kind: 'checkpointBindings' | 'gateBindings', id: string, zoneId: string) {
    if (!this.config || this.blocked()) return;
    this.config[kind] = [...this.config[kind].filter(item => item.resourceId !== id), ...(zoneId ? [{ resourceId: id, zoneId }] : [])];
    this.syncJson(); this.modified();
  }
  importMap() {
    if (!this.airportMapId.trim() || this.blocked()) return;
    this.start();
    this.api.get<NavigationConfig>(`navigation/airports/${this.airportMapId.trim()}`)
      .pipe(finalize(() => this.stop()), takeUntilDestroyed(this.destroy))
      .subscribe({ next: value => {
        // The imported graph and the operational scene use different resource IDs. Never bind them by coincidence or array position.
        this.config = { ...value, checkpointBindings: [], gateBindings: [] }; this.syncJson(); this.modified();
        this.message.set('Graful a fost importat. Asociază explicit checkpoint-urile și porțile scenariului cu zonele hărții.');
      }, error: error => this.report(error) });
  }
  save(disable = false) {
    const settings = this.settings();
    if (!settings || this.blocked() || !settings.passengerProfilesActive || !disable && !this.parse()) return;
    this.start();
    this.api.put<ScenarioDetails>(`scenarios/${settings.scenarioId}/navigation/settings`, { revision: settings.revision, config: disable ? null : this.config })
      .pipe(finalize(() => this.stop()), takeUntilDestroyed(this.destroy))
      .subscribe({ next: scenario => { this.dirty.set(false); this.changed.emit(scenario); this.message.set('Rutarea și închiderile au fost salvate pentru următoarea execuție.'); }, error: error => this.report(error) });
  }
  preview(offset = 0) {
    const settings = this.settings();
    if (!settings || !settings.passengerProfilesActive || this.blocked() || !this.parse()) return;
    this.previewSubscription?.unsubscribe();
    this.showingRun.set(null); this.previewing.set(true); this.previewError.set('');
    this.previewSubscription = this.api.post<NavigationResult>(`scenarios/${settings.scenarioId}/navigation/preview${offset ? `?offset=${offset}&limit=100` : ''}`,
      { revision: settings.revision, config: this.config })
      .pipe(finalize(() => this.previewing.set(false)), takeUntilDestroyed(this.destroy))
      .subscribe({ next: result => this.result.set(result), error: error => {
        this.result.set(null); this.previewError.set(error instanceof Error ? error.message : 'Traseele nu au putut fi calculate.');
      } });
  }
  inspectRun(offset = 0) {
    const run = this.run();
    if (!run?.resultsValid || this.blocked()) return;
    this.loadRun(run.id, offset);
  }
  private loadRun(id: string, offset: number) {
    if (this.blocked()) return;
    this.previewSubscription?.unsubscribe();
    this.start(); this.showingRun.set(id);
    this.api.get<NavigationResult>(`runs/${id}/navigation?offset=${offset}&limit=100`)
      .pipe(finalize(() => this.stop()), takeUntilDestroyed(this.destroy))
      .subscribe({ next: result => this.result.set(result), error: error => { this.result.set(null); this.report(error); } });
  }
  next(offset: number) { const id = this.showingRun(); if (id) this.loadRun(id, offset); else this.preview(offset); }
  walkingMinutes() {
    const routes = this.result()?.routes ?? [];
    const count = routes.reduce((sum, route) => sum + route.passengerCount, 0);
    return count ? routes.reduce((sum, route) => sum + route.passengerCount * route.baseWalkingMinutes, 0) / count : null;
  }
  toggleRoutes() { this.showRoutes.update(value => !value); }
  /** Simulation minutes count from the scenario start; show them as the local wall clock. */
  clock(minute: number | null) {
    const inputs = this.scenario().inputs;
    return minute === null ? '—' : clockTime(new Date(Date.parse(inputs.startsAtUtc) + minute * 60000).toISOString(), inputs.timeZone);
  }
  flightName(id: string) { return this.scenario().inputs.flights.find(item => item.id === id)?.code ?? id; }
  gateName(id: string) { return this.settings()?.gates.find(item => item.id === id)?.name ?? id; }
  risk(kind: string) { return ({ pending: 'Necesită simularea cozii', safe: 'Ajunge la timp', late: 'Risc: ajunge după închidere', unserved: 'Etape neterminate în interval', unreachable: 'Risc: traseu indisponibil' } as Record<string, string>)[kind] ?? kind; }
  blocked() { return this.disabled() || this.loading() || this.busy(); }
  private start() { this.busy.set(true); this.working.emit(true); this.message.set(''); }
  private stop() { this.busy.set(false); this.working.emit(false); }
  private report(error: unknown) { this.message.set(error instanceof Error ? error.message : 'Rutarea nu a putut fi finalizată.'); }
}

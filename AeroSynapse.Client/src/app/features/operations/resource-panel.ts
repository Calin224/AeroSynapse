import {
  Component,
  computed,
  DestroyRef,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { finalize, map, Observable, of, Subscription, switchMap, tap, timeout } from 'rxjs';
import { ApiClient } from '../../core/http/api-client';
import { OPERATION_REQUEST_TIMEOUT_MS, operationError } from '../../core/http/operation-request';
import { ScenarioDetails } from './operations.models';
import {
  IntegratedResourceConfig,
  ResourceResult,
  ResourceSettings,
  ResourceWindow,
} from './resource.models';
import { addMinutes, clockTime } from '../../shared/absolute-time';

/** What each solver rule means for the person reading the proposal. */
const RULES: Record<string, string> = {
  gate_compatibility: 'zborul nu are voie la această poartă',
  gate_availability: 'poarta nu e deschisă pe tot intervalul zborului',
  gate_coverage: 'nicio poartă permisă nu e liberă pe tot intervalul zborului',
  gate_capacity: 'poarta ar avea două zboruri în același timp',
  team_size: 'echipa are prea puțini oameni pentru zbor',
  team_skills: 'echipei îi lipsesc competențele cerute',
  team_shift: 'unii membri nu sunt în tură în timpul zborului',
  team_gate_compatibility: 'echipa nu deservește nicio poartă potrivită',
  handling_coverage: 'nicio echipă nu poate prelua zborul',
  team_capacity: 'echipa ar avea două zboruri în același timp',
  staff_capacity: 'o persoană ar fi în două zboruri în același timp',
};
const key = (scenario: ScenarioDetails) => `${scenario.scenario.id}/${scenario.scenario.revision}`;
type Team = IntegratedResourceConfig['teams'][number];

@Component({
  selector: 'app-resource-panel',
  imports: [FormsModule],
  templateUrl: './resource-panel.html',
  styleUrl: './setup-panels.css',
})
export class ResourcePanel {
  readonly scenario = input.required<ScenarioDetails>();
  readonly active = input(false);
  readonly disabled = input(false);
  readonly changed = output<ScenarioDetails>();
  readonly working = output<boolean>();
  readonly settings = signal<ResourceSettings | null>(null);
  readonly freeStaff = computed(
    () => this.settings()?.staff.filter((worker) => !worker.reservedForSecurity) ?? [],
  );
  readonly loading = signal(false);
  readonly busy = signal(false);
  readonly message = signal('');
  readonly notice = signal('');
  readonly result = signal<ResourceResult | null>(null);
  readonly dirty = signal(false);
  /** Flights the proposal puts at another gate than the schedule. */
  readonly moves = computed(() => {
    const result = this.result();
    return (
      result?.assignments.filter((item) => item.gateId !== this.currentGate(item.flightId)) ?? []
    );
  });
  draft: IntegratedResourceConfig | null = null;
  timeLimitSeconds = 5;
  private readonly api = inject(ApiClient);
  private pending?: Subscription;
  private readonly scenarioContext = computed(() => key(this.scenario()));
  private context = '';

  constructor() {
    inject(DestroyRef).onDestroy(() => this.pending?.unsubscribe());
    effect(() => {
      const context = this.scenarioContext();
      const active = this.active();
      untracked(() => {
        if (context !== this.context) {
          this.pending?.unsubscribe();
          this.context = context;
          this.settings.set(null);
          this.result.set(null);
          this.draft = null;
          this.message.set('');
          this.notice.set('');
          this.dirty.set(false);
        }
        if (active && !this.settings()) this.load();
      });
    });
  }
  load() {
    if (!this.active() || this.loading() || this.busy()) return;
    this.pending?.unsubscribe();
    this.message.set('');
    this.loading.set(true);
    const scenario = this.scenario().scenario;
    this.pending = this.api
      .get<ResourceSettings>(`scenarios/${scenario.id}/resources/settings`)
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: (settings) => {
          if (settings.scenarioId !== scenario.id || settings.revision !== scenario.revision) {
            this.message.set(
              'Scenariul s-a modificat. Selectează-l din nou pentru revizia curentă.',
            );
            return;
          }
          this.settings.set(settings);
          const draft = settings.resources
            ? structuredClone(settings.resources)
            : this.defaults(settings);
          this.draft = draft;
          // One rule per current flight: new flights get the defaults, removed ones drop out.
          draft.requirements = settings.flights.map(
            (flight) =>
              draft.requirements.find((item) => item.flightId === flight.id) ??
              this.requirement(flight.id),
          );
        },
        error: (error) => this.report(error),
      });
  }
  /** A plan that works as is: every gate open all day, free people in pairs serving their terminal's gates. */
  private defaults(settings: ResourceSettings): IntegratedResourceConfig {
    const gates = settings.gates.slice(0, 50);
    const people = this.freeStaff().slice(0, 200);
    const teams = [...new Set(people.map((worker) => worker.terminalId))]
      .flatMap((terminal) => {
        const members = people
          .filter((worker) => worker.terminalId === terminal)
          .map((worker) => worker.id);
        const gateIds = gates.filter((gate) => gate.terminalId === terminal).map((gate) => gate.id);
        return gateIds.length
          ? Array.from({ length: Math.ceil(members.length / 2) }, (_, index) => ({
              memberIds: members.slice(index * 2, index * 2 + 2),
              gateIds,
            }))
          : [];
      })
      .slice(0, 50)
      .map((team, index) => ({
        teamId: crypto.randomUUID(),
        name: `Echipa ${index + 1}`,
        ...team,
      }));
    return {
      contractVersion: '1.0',
      gates: gates.map((gate) => ({ gateId: gate.id, availability: [this.wholeDay()] })),
      workers: this.workers([], teams),
      teams,
      requirements: [],
    };
  }
  private wholeDay(): ResourceWindow {
    return { startMinute: 0, endMinute: this.settings()!.durationMinutes };
  }
  private terminal(gateId: string) {
    return this.settings()?.gates.find((gate) => gate.id === gateId)?.terminalId;
  }
  private flightTerminal(flightId: string) {
    return this.terminal(this.settings()!.flights.find((flight) => flight.id === flightId)!.gateId);
  }
  /** By default a flight may use any chosen gate of its terminal. */
  private requirement(flightId: string) {
    return {
      flightId,
      compatibleGateIds: this.draft!.gates.filter(
        (item) => this.terminal(item.gateId) === this.flightTerminal(flightId),
      ).map((item) => item.gateId),
      requiredSkills: [],
      minimumTeamSize: 1,
    };
  }
  /** Workers are the team members; saved skills and shifts stay, a new member works the whole day. */
  private workers(current: IntegratedResourceConfig['workers'], teams: Team[]) {
    return [...new Set(teams.flatMap((team) => team.memberIds))].map(
      (id) =>
        current.find((worker) => worker.staffMemberId === id) ?? {
          staffMemberId: id,
          skills: [],
          shifts: [this.wholeDay()],
        },
    );
  }
  edit() {
    this.dirty.set(true);
    this.result.set(null);
    this.message.set('');
    this.notice.set('');
  }
  blocked() {
    return this.disabled() || this.loading() || this.busy();
  }
  toggle(values: string[], id: string) {
    this.edit();
    return values.includes(id) ? values.filter((value) => value !== id) : [...values, id];
  }
  toggleMember(team: Team, id: string) {
    team.memberIds = this.toggle(team.memberIds, id);
    this.draft!.workers = this.workers(this.draft!.workers, this.draft!.teams);
  }
  /** Scenario minutes every 15 minutes, plus the value already set, for the time pickers. */
  times(current: number) {
    const end = this.settings()?.durationMinutes ?? 0;
    const grid = Array.from({ length: Math.floor(end / 15) + 1 }, (_, index) => index * 15);
    return [...new Set([...grid, end, current])].sort((a, b) => a - b);
  }
  addWindow(windows: ResourceWindow[]) {
    windows.push(this.wholeDay());
    this.edit();
  }
  removeWindow(windows: ResourceWindow[], index: number) {
    windows.splice(index, 1);
    this.edit();
  }
  opened(id: string) {
    return this.draft?.gates.find((item) => item.gateId === id);
  }
  includeGate(id: string) {
    const draft = this.draft;
    if (
      !draft ||
      draft.gates.length >= 50 ||
      this.opened(id) ||
      !this.settings()?.gates.some((gate) => gate.id === id)
    )
      return;
    draft.gates.push({ gateId: id, availability: [this.wholeDay()] });
    // Like the defaults: a new gate is open to the teams and flights of its terminal.
    for (const team of draft.teams)
      if (team.gateIds.some((gate) => this.terminal(gate) === this.terminal(id)))
        team.gateIds.push(id);
    for (const requirement of draft.requirements)
      if (this.flightTerminal(requirement.flightId) === this.terminal(id))
        requirement.compatibleGateIds.push(id);
    this.edit();
  }
  removeGate(id: string) {
    if (!this.draft) return;
    this.draft.gates = this.draft.gates.filter((gate) => gate.gateId !== id);
    for (const team of this.draft.teams) team.gateIds = team.gateIds.filter((gate) => gate !== id);
    for (const requirement of this.draft.requirements)
      requirement.compatibleGateIds = requirement.compatibleGateIds.filter((gate) => gate !== id);
    this.edit();
  }
  addTeam() {
    if (!this.draft || this.draft.teams.length >= 50) return;
    this.draft.teams.push({
      teamId: crypto.randomUUID(),
      name: `Echipa ${this.draft.teams.length + 1}`,
      memberIds: [],
      gateIds: this.draft.gates.map((gate) => gate.gateId),
    });
    this.edit();
  }
  removeTeam(index: number) {
    if (!this.draft) return;
    this.draft.teams.splice(index, 1);
    this.draft.workers = this.workers(this.draft.workers, this.draft.teams);
    this.edit();
  }
  name(id: string | null) {
    const settings = this.settings();
    return (
      settings?.flights.find((item) => item.id === id)?.code ??
      settings?.gates.find((item) => item.id === id)?.name ??
      settings?.staff.find((item) => item.id === id)?.name ??
      this.draft?.teams.find((item) => item.teamId === id)?.name ??
      id ??
      ''
    );
  }
  interval(id: string) {
    const settings = this.settings()!;
    const flight = settings.flights.find((item) => item.id === id)!;
    const minute = (at: string) =>
      (Date.parse(at) - Date.parse(settings.startsAtUtc)) / 60000 + (flight.delayMinutes ?? 0);
    return `${this.clock(minute(flight.boardingOpensAtUtc!))}–${this.clock(minute(flight.departureAtUtc))}`;
  }
  assignmentInterval(flightId: string) {
    const flight = this.result()?.constraints.flights.find((item) => item.flightId === flightId);
    return flight ? `${this.clock(flight.startMinute)}–${this.clock(flight.endMinute)}` : '';
  }
  currentGate(flightId: string) {
    return (
      this.result()?.constraints.flights.find((item) => item.flightId === flightId)
        ?.currentGateId ?? null
    );
  }
  /** Scenario minute as local HH:mm, so minute offsets read as clock times. */
  clock(minute: number) {
    const settings = this.settings();
    return settings
      ? clockTime(addMinutes(settings.startsAtUtc, minute), this.scenario().inputs.timeZone)
      : '—';
  }
  memberNames(teamId: string) {
    return (
      this.result()
        ?.constraints.resources.teams.find((team) => team.teamId === teamId)
        ?.memberIds.map((id) => this.name(id))
        .join(', ') ?? ''
    );
  }
  rule(value: ResourceResult['rules'][number]) {
    const subject = [value.flightId, value.resourceId]
      .filter(Boolean)
      .map((id) => this.name(id))
      .join(' · ');
    return `${subject ? subject + ': ' : ''}${RULES[value.code] ?? value.message}`;
  }
  /** Saves the plan when it changed, then asks for a proposal on that revision. */
  calculate() {
    const settings = this.settings();
    const draft = this.draft;
    if (this.blocked() || !settings?.compatible || !draft?.gates.length) return;
    if (
      !Number.isFinite(this.timeLimitSeconds) ||
      this.timeLimitSeconds < 0 ||
      this.timeLimitSeconds > 30
    ) {
      this.message.set('Timpul maxim de calcul trebuie să fie între 0 și 30 de secunde.');
      return;
    }
    const revision: Observable<number> =
      this.dirty() || !settings.resources
        ? this.api
            .put<ScenarioDetails>(`scenarios/${settings.scenarioId}/resources/settings`, {
              revision: settings.revision,
              resources: draft,
            })
            .pipe(
              tap((value) => this.saved(value)),
              map((value) => value.scenario.revision),
            )
        : of(settings.revision);
    this.result.set(null);
    this.start();
    this.pending = revision
      .pipe(
        switchMap((value) =>
          this.api
            .post<ResourceResult>(`scenarios/${settings.scenarioId}/resources/proposals`, {
              revision: value,
              timeLimitSeconds: this.timeLimitSeconds,
            })
            .pipe(timeout(OPERATION_REQUEST_TIMEOUT_MS)),
        ),
        finalize(() => this.finish()),
      )
      .subscribe({
        next: (result) => {
          const current = this.settings()!;
          if (
            result.constraints.scenarioId !== current.scenarioId ||
            result.constraints.scenarioRevision !== current.revision ||
            !result.advisoryOnly
          ) {
            this.message.set('Propunerea nu corespunde reviziei afișate. Reîncarcă scenariul.');
            return;
          }
          this.result.set(result);
        },
        error: (error) => this.report(error),
      });
  }
  /** Moves the flights to the proposed gates in the schedule; their passenger routes follow. */
  apply() {
    const settings = this.settings();
    const moves = this.moves();
    if (this.blocked() || !settings || !moves.length) return;
    this.start();
    this.pending = this.api
      .post<ScenarioDetails>(`scenarios/${settings.scenarioId}/resources/apply`, {
        revision: settings.revision,
        flights: moves.map((item) => ({ flightId: item.flightId, gateId: item.gateId })),
      })
      .pipe(finalize(() => this.finish()))
      .subscribe({
        next: (value) => {
          this.saved(value);
          this.settings.update(
            (current) =>
              current && {
                ...current,
                flights: current.flights.map((flight) => ({
                  ...flight,
                  gateId:
                    value.inputs.flights.find((item) => item.id === flight.id)?.gateId ??
                    flight.gateId,
                })),
              },
          );
          this.result.set(null);
          this.notice.set(
            `Am mutat ${moves.length === 1 ? 'un zbor' : moves.length + ' zboruri'} la porțile propuse. Le vezi în Orar zboruri.`,
          );
        },
        error: (error) => this.report(error),
      });
  }
  deactivate() {
    const settings = this.settings();
    if (this.blocked() || !settings?.resources) return;
    this.start();
    this.pending = this.api
      .put<ScenarioDetails>(`scenarios/${settings.scenarioId}/resources/settings`, {
        revision: settings.revision,
        resources: null,
      })
      .pipe(finalize(() => this.finish()))
      .subscribe({
        next: (value) => {
          this.saved(value);
          this.result.set(null);
        },
        error: (error) => this.report(error),
      });
  }
  /** Our own write: keep the draft and follow the new revision instead of reloading. */
  private saved(value: ScenarioDetails) {
    this.context = key(value);
    this.settings.update(
      (current) =>
        current && {
          ...current,
          revision: value.scenario.revision,
          resources: value.inputs.integratedResources ?? null,
        },
    );
    this.dirty.set(false);
    this.changed.emit(value);
  }
  status(value: ResourceResult) {
    return {
      optimal: 'Am găsit o propunere',
      feasible:
        'Am găsit o propunere bună, dar timpul de calcul s-a terminat înainte să confirmăm că e cea mai bună',
      infeasible: 'Nu se poate: regulile de mai jos nu pot fi respectate în același timp',
      timeout: 'Timpul de calcul s-a terminat fără o soluție',
      invalid_model: 'Configurația nu poate fi calculată',
    }[value.status];
  }
  private start() {
    this.message.set('');
    this.notice.set('');
    this.busy.set(true);
    this.working.emit(true);
  }
  private finish() {
    this.busy.set(false);
    this.working.emit(false);
  }
  private report(error: unknown) {
    this.message.set(operationError(error));
  }
}

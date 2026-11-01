import {
  afterNextRender,
  Component,
  DestroyRef,
  ElementRef,
  HostListener,
  Injector,
  inject,
  signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { finalize, map, Observable, switchMap } from 'rxjs';
import { ApiClient } from '../../core/http/api-client';
import { SimulationContext } from '../../core/simulation-context';
import { ApiError } from '../../core/http/api-error.interceptor';
import { createDataResource } from '../../shared/data-resource';
import { DataState } from '../../shared/ui/data-state';
import { AbsoluteTimeInput } from '../../shared/ui/absolute-time-input';
import { absoluteTimeLabel, addMinutes } from '../../shared/absolute-time';
import {
  OperatorRole,
  ScenarioDetails,
  ScenarioFlight,
  ScenarioOperator,
  SceneSummary,
} from '../operations/operations.models';
import { FieldValue, ObjectFields } from './object-fields';
import { IncidentDesigner, newSurge } from './incident-designer';
import {
  basicErrors,
  departureSpacingErrors,
  defaultCheckIn,
  FieldErrors,
  fromScenario,
  InputEvent,
  makeDraft,
  MINIMUM_DEPARTURE_SPACING_MINUTES,
  PRESETS,
  shiftStart,
  StudioDraft,
  StudioLayout,
  withOperators,
} from './studio.models';

@Component({
  selector: 'app-scenario-studio',
  imports: [FormsModule, DataState, ObjectFields, IncidentDesigner, AbsoluteTimeInput],
  templateUrl: './scenario-studio.html',
  styleUrl: './scenario-studio.css',
})
export class ScenarioStudio {
  private readonly injector = inject(Injector);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly api = inject(ApiClient);
  private readonly destroy = inject(DestroyRef);
  private readonly simulation = inject(SimulationContext);
  readonly scenes = createDataResource(
    () => this.api.get<(SceneSummary & { isSynthetic?: boolean })[]>('scenes'),
    (rows) => !rows.length,
  );
  readonly saved = signal<ScenarioDetails | null>(null);
  readonly busy = signal(false);
  readonly message = signal('');
  readonly errors = signal<FieldErrors>({});
  readonly pending = signal<{ label: string; action: () => void } | null>(null);
  readonly presets = PRESETS;
  readonly showOtherPresets = signal(false);
  readonly step = signal(1);
  readonly steps = ['Detalii', 'Zboruri și pasageri', 'Resurse și incidente', 'Verificare'];
  readonly advancedTraffic = signal(false);
  readonly advancedGeneral = signal(false);
  readonly advancedConfig = signal(false);
  readonly completed = signal(false);
  readonly statuses = ['scheduled', 'delayed', 'boarding', 'closed', 'departed', 'cancelled'];
  readonly statusLabels: Record<string, string> = {
    scheduled: 'Programat',
    delayed: 'Întârziat',
    boarding: 'Îmbarcare',
    closed: 'Îmbarcare închisă',
    departed: 'Plecat',
    cancelled: 'Anulat',
  };
  draft: StudioDraft | null = null;
  layout: StudioLayout | null = null;
  sceneId = '';
  duplicateName = '';
  duplicateCode = '';
  duplicating = false;
  private clean = '';
  private validated = '';
  private lastStart = '';
  private readonly lastDepartures = new Map<string, string>();

  selectedScene() {
    const state = this.scenes.state();
    return state.status === 'ready'
      ? (state.data.find((scene) => scene.id === this.sceneId) ?? this.defaultScene())
      : null;
  }
  defaultScene() {
    const state = this.scenes.state();
    return state.status === 'ready'
      ? (state.data.find((scene) => scene.isSynthetic) ?? state.data[0])
      : null;
  }

  constructor() {
    const scenarioId = inject(ActivatedRoute).snapshot.queryParamMap.get('scenarioId');
    if (scenarioId) this.select(scenarioId);
  }

  dirty(): boolean {
    return !!this.draft && JSON.stringify(this.draft) !== this.clean;
  }
  isValidated(): boolean {
    return !!this.validated && this.validated === JSON.stringify(this.draft);
  }
  private changed() {
    this.errors.set({});
    this.message.set('');
    this.validated = '';
    this.completed.set(false);
  }
  edited() {
    if (this.draft) this.draft.inputs = { ...this.draft.inputs };
    this.changed();
  }
  private protect(label: string, action: () => void) {
    if (this.busy()) return;
    if (this.dirty()) {
      this.pending.set({ label, action });
      return;
    }
    action();
  }
  discardAndContinue() {
    const pending = this.pending();
    this.pending.set(null);
    pending?.action();
  }
  newScenario(preset: string | null = null) {
    const state = this.scenes.state();
    if (state.status !== 'ready') return;
    const sceneId =
      this.sceneId || state.data.find((scene) => scene.isSynthetic)?.id || state.data[0]?.id;
    if (!sceneId) return;
    this.protect(preset ? 'încărcare configurație predefinită' : 'creare scenariu', () => {
      this.busy.set(true);
      this.changed();
      this.api
        .post<StudioLayout>(`scenes/${sceneId}/layout-versions`)
        .pipe(
          map((layout) => ({ layout, draft: makeDraft(layout, preset) })),
          finalize(() => this.busy.set(false)),
          takeUntilDestroyed(this.destroy),
        )
        .subscribe({
          next: ({ layout, draft }) => {
            this.layout = layout;
            this.draft = draft;
            this.sceneId = sceneId;
            this.saved.set(null);
            this.duplicating = false;
            this.lastStart = draft.inputs.startsAtUtc;
            this.clean = '';
            if (!preset) {
              this.restoreStaff();
              this.addFlight();
            }
            this.step.set(1);
            this.advancedTraffic.set(false);
            this.advancedGeneral.set(false);
            this.advancedConfig.set(false);
            this.message.set('');
            this.focusStep();
          },
          error: (error) => this.report(error),
        });
    });
  }
  select(id: string) {
    this.protect('deschidere scenariu salvat', () => {
      this.busy.set(true);
      this.changed();
      this.api
        .get<ScenarioDetails>(`scenarios/${id}`)
        .pipe(
          switchMap((value) =>
            this.api
              .get<StudioLayout>(
                `scenes/${value.scenario.sceneId}/layout-versions/${value.scenario.layoutVersionId}`,
              )
              .pipe(map((layout) => ({ value, layout }))),
          ),
          finalize(() => this.busy.set(false)),
          takeUntilDestroyed(this.destroy),
        )
        .subscribe({
          next: ({ value, layout }) => {
            this.layout = layout;
            this.accept(value);
            this.step.set(1);
            this.completed.set(false);
            this.focusStep();
          },
          error: (error) => this.report(error),
        });
    });
  }
  reload() {
    const id = this.saved()?.scenario.id;
    if (id) this.select(id);
  }
  private accept(value: ScenarioDetails) {
    this.saved.set(value);
    this.draft = fromScenario(value);
    this.clean = JSON.stringify(this.draft);
    // A team filled in for an older scenario is unsaved, so the studio asks to save it before leaving.
    withOperators(this.draft, this.layout!);
    this.lastStart = value.inputs.startsAtUtc;
    this.sceneId = value.scenario.sceneId;
    this.duplicating = false;
    this.pending.set(null);
    this.validated = '';
  }
  private request(): StudioDraft | null {
    if (!this.draft || this.busy()) return null;
    this.errors.set(this.draftErrors());
    if (Object.keys(this.errors()).length) {
      this.message.set(
        'Validare eșuată. Câmpurile indicate necesită corectare. Configurația nu a fost salvată.',
      );
      return null;
    }
    return structuredClone(this.draft);
  }
  validate() {
    const request = this.request();
    if (!request) return;
    this.busy.set(true);
    this.message.set('');
    this.api
      .post<{ isValid: boolean }>('scenarios/validate', request)
      .pipe(
        finalize(() => this.busy.set(false)),
        takeUntilDestroyed(this.destroy),
      )
      .subscribe({
        next: () => {
          this.validated = JSON.stringify(request);
          this.message.set(
            'Configurația este validă. Salvarea va verifica și unicitatea codului și revizia curentă.',
          );
        },
        error: (error) => this.report(error),
      });
  }
  save() {
    const request = this.request();
    if (!request) return;
    this.busy.set(true);
    this.message.set('');
    const saved = this.saved();
    const mutation = saved
      ? this.api.put<ScenarioDetails>(`scenarios/${saved.scenario.id}`, request)
      : this.api.post<ScenarioDetails>('scenarios', request);
    this.mutate(
      mutation,
      'Scenariul a fost salvat. Toți parametrii sunt păstrați în această revizie.',
    );
  }
  beginDuplicate() {
    if (!this.saved() || this.busy() || this.dirty()) return;
    this.changed();
    this.duplicating = true;
    this.duplicateCode = `${this.draft!.code.slice(0, 48)}-copy-${crypto.randomUUID().slice(0, 6)}`;
    this.duplicateName = `${this.draft!.name.slice(0, 150)} · copie`;
  }
  duplicate() {
    const saved = this.saved();
    if (!saved || this.busy() || this.dirty()) return;
    if (
      !this.duplicateCode.trim() ||
      this.duplicateCode.length > 64 ||
      !this.duplicateName.trim() ||
      this.duplicateName.length > 160
    ) {
      this.message.set(
        'Copia necesită un cod nou de maximum 64 de caractere și un nume de maximum 160 de caractere.',
      );
      return;
    }
    this.busy.set(true);
    this.changed();
    this.mutate(
      this.api.post<ScenarioDetails>(`scenarios/${saved.scenario.id}/duplicate`, {
        code: this.duplicateCode.trim(),
        name: this.duplicateName.trim(),
      }),
      'Copia independentă a fost salvată. Originalul rămâne disponibil în bibliotecă.',
    );
  }
  private mutate(request: Observable<ScenarioDetails>, message: string) {
    request
      .pipe(
        finalize(() => this.busy.set(false)),
        takeUntilDestroyed(this.destroy),
      )
      .subscribe({
        next: (value) => {
          this.accept(value);
          // "Scenariile mele" opens the scenario just saved instead of an older (maybe deleted) one.
          this.simulation.remember(value.scenario.id);
          this.errors.set({});
          this.message.set(message);
          this.step.set(4);
          this.completed.set(true);
          this.focusStep();
        },
        error: (error) => this.report(error),
      });
  }
  private report(error: unknown) {
    const fields = error instanceof ApiError ? error.fieldErrors : {};
    this.errors.set(fields);
    this.validated = '';
    this.message.set(
      Object.keys(fields).length
        ? 'Configurație invalidă. Erorile indicate necesită corectare.'
        : error instanceof ApiError && error.status === 409
          ? 'Salvarea a fost refuzată: revizia s-a schimbat sau codul este deja utilizat. Modificările sunt păstrate. Este necesară verificarea codului sau reîncărcarea scenariului.'
          : error instanceof Error
            ? error.message
            : 'Operația nu a putut fi finalizată. Modificările sunt păstrate.',
    );
  }
  setStart(value: string) {
    if (!this.draft) return;
    const timestamp = value;
    if (Number.isFinite(Date.parse(this.draft.inputs.startsAtUtc)))
      this.lastStart = this.draft.inputs.startsAtUtc;
    if (
      Number.isFinite(Date.parse(timestamp)) &&
      !Number.isFinite(Date.parse(this.draft.inputs.startsAtUtc))
    )
      this.draft.inputs.startsAtUtc = this.lastStart;
    shiftStart(
      this.draft,
      Number.isFinite(Date.parse(timestamp)) ? new Date(timestamp).toISOString() : '',
    );
    this.changed();
  }
  clock(value: string | null | undefined) {
    return absoluteTimeLabel(value, this.draft?.inputs.timeZone ?? 'UTC');
  }
  setTime<T extends object, K extends keyof T>(row: T, key: K, utc: string) {
    row[key] = utc as T[K];
    this.changed();
  }
  addFlight() {
    if (!this.draft || !this.layout) return;
    if (!Number.isFinite(Date.parse(this.draft.inputs.startsAtUtc))) {
      this.message.set(
        'Completează data și ora de început a scenariului înainte de a adăuga zboruri.',
      );
      return;
    }
    // Spread new flights over free gates first; only reuse a gate once every gate is taken.
    const gates = this.connectedGates();
    const flights = this.draft.inputs.flights;
    const gate =
      gates.find((item) => !flights.some((flight) => flight.gateId === item.id)) ??
      gates[flights.length % Math.max(1, gates.length)];
    if (!gate) {
      this.message.set(
        'Niciun traseu disponibil între control și poartă. Este necesară selectarea altui aeroport sau configurarea traseelor de către administrator.',
      );
      return;
    }
    const start = this.draft.inputs.startsAtUtc;
    const lastDeparture = (rows: ScenarioFlight[]) =>
      Math.max(
        Date.parse(start),
        ...rows
          .filter((row) => row.status !== 'cancelled')
          .map((row) => Date.parse(addMinutes(row.departureAtUtc, row.delayMinutes ?? 0)))
          .filter(Number.isFinite),
      );
    const spacing =
      this.draft.inputs.minimumDepartureSpacingMinutes ?? MINIMUM_DEPARTURE_SPACING_MINUTES;
    // A new flight leaves 15 minutes after the last one, later if its gate still needs the spacing.
    const horizon =
      (Math.max(
        lastDeparture(flights) + 15 * 60000,
        lastDeparture(flights.filter((row) => row.gateId === gate.id)) + spacing * 60000,
      ) -
        Date.parse(start)) /
      60000;
    const departureAtUtc = addMinutes(start, horizon);
    if (!departureAtUtc || horizon > 10080) {
      this.message.set(
        'Adăugarea zborului depășește orizontul de simulare permis. Este necesară ajustarea orarului.',
      );
      return;
    }
    this.draft.inputs.durationMinutes = Math.max(
      this.draft.inputs.durationMinutes,
      Math.ceil(horizon),
    );
    let number = 101 + flights.length;
    while (flights.some((row) => row.code === `DEMO ${number}`)) number++;
    this.draft.inputs.flights.push({
      id: crypto.randomUUID(),
      code: `DEMO ${number}`,
      gateId: gate?.id ?? '',
      departureAtUtc,
      boardingOpensAtUtc: addMinutes(departureAtUtc, -12),
      boardingClosesAtUtc: addMinutes(departureAtUtc, -2),
      passengerCapacity: 120,
      delayMinutes: 0,
      status: 'scheduled',
      isSynthetic: true,
    });
    this.addPassengers(flights[flights.length - 1]);
    this.changed();
  }
  connectedGates() {
    return (
      this.layout?.layout.gates.filter((gate) =>
        this.layout!.layout.routes.some(
          (route) =>
            route.toGateId === gate.id &&
            this.layout!.layout.checkpoints.some((cp) => cp.id === route.fromCheckpointId),
        ),
      ) ?? []
    );
  }
  totalPassengers() {
    return (
      this.draft?.inputs.flows.reduce((sum, flow) => sum + (Number(flow.passengerCount) || 0), 0) ??
      0
    );
  }
  passengersFor(flight: ScenarioFlight) {
    return (
      this.draft?.inputs.flows
        .filter((flow) => flow.flightId === flight.id)
        .reduce((sum, flow) => sum + (Number(flow.passengerCount) || 0), 0) ?? 0
    );
  }
  /** Only edit a private, single-flow group through the simplified controls. */
  simpleGroup(flight: ScenarioFlight) {
    const input = this.draft!.inputs;
    if (input.passengerGeneration || input.navigation || input.integratedResources) return null;
    const flows = input.flows.filter((flow) => flow.flightId === flight.id);
    if (flows.length !== 1) return null;
    const flow = flows[0];
    const population = input.populations.find((row) => row.id === flow.populationId);
    if (
      !population ||
      input.flows.some((row) => row.id !== flow.id && row.populationId === population.id)
    )
      return null;
    return { flow, population };
  }
  setPassengers(flight: ScenarioFlight, count: number) {
    const group = this.simpleGroup(flight);
    if (!group) return;
    group.flow.passengerCount = count;
    group.population.count = count;
    if (Number.isFinite(count))
      flight.passengerCapacity = Math.max(flight.passengerCapacity, count);
    this.edited();
  }
  setGate(flight: ScenarioFlight, gateId: string) {
    const group = this.simpleGroup(flight);
    const route = this.layout?.layout.routes.find((row) => row.toGateId === gateId);
    if (!group || !route) return;
    flight.gateId = gateId;
    group.flow.routeId = route.id;
    this.edited();
  }
  setDeparture(flight: ScenarioFlight, utc: string) {
    const group = this.simpleGroup(flight);
    if (!group) return;
    if (Number.isFinite(Date.parse(flight.departureAtUtc)))
      this.lastDepartures.set(flight.id, flight.departureAtUtc);
    const previous = this.lastDepartures.get(flight.id);
    if (utc && previous && Number.isFinite(Date.parse(utc))) {
      const shift = (value: string) =>
        addMinutes(value, (Date.parse(utc) - Date.parse(previous)) / 60000);
      flight.boardingOpensAtUtc = shift(flight.boardingOpensAtUtc || addMinutes(previous, -12));
      flight.boardingClosesAtUtc = shift(flight.boardingClosesAtUtc || addMinutes(previous, -2));
      group.flow.endsAtUtc = shift(group.flow.endsAtUtc || addMinutes(previous, -2));
    }
    flight.departureAtUtc = utc;
    this.edited();
  }
  addPassengers(flight: ScenarioFlight) {
    if (!this.draft || this.passengersFor(flight) > 0) return;
    if (
      ![this.draft.inputs.startsAtUtc, flight.departureAtUtc].every((value) =>
        Number.isFinite(Date.parse(value)),
      )
    ) {
      this.message.set(
        'Completează data și ora scenariului și a plecării înainte de a adăuga pasageri.',
      );
      return;
    }
    const route = this.layout?.layout.routes.find((row) => row.toGateId === flight.gateId);
    if (!route) {
      this.message.set('Adăugarea pasagerilor necesită o poartă cu traseu disponibil.');
      return;
    }
    const population = {
      id: crypto.randomUUID(),
      name: `Pasageri ${flight.code}`,
      count: flight.passengerCapacity,
    };
    this.draft.inputs.populations.push(population);
    this.draft.inputs.flows.push({
      id: crypto.randomUUID(),
      populationId: population.id,
      flightId: flight.id,
      routeId: route.id,
      passengerCount: population.count,
      startsAtUtc: new Date(
        Math.max(
          Date.parse(this.draft.inputs.startsAtUtc),
          Date.parse(addMinutes(flight.departureAtUtc, -90)),
        ),
      ).toISOString(),
      endsAtUtc: flight.boardingClosesAtUtc ?? flight.departureAtUtc,
    });
    this.edited();
  }
  removeFlightWithPassengers(flight: ScenarioFlight) {
    const input = this.draft!.inputs;
    const group = this.simpleGroup(flight);
    if (!group || input.events.some((row) => row.flightId === flight.id)) {
      this.removeFlight(flight);
      return;
    }
    input.flows = input.flows.filter((row) => row.id !== group.flow.id);
    input.populations = input.populations.filter((row) => row.id !== group.population.id);
    this.removeFlight(flight);
  }
  restoreStaff() {
    if (!this.draft || !this.layout) return;
    this.draft.inputs.staffAllocations = this.layout.layout.staff
      .filter((member) =>
        this.layout!.layout.checkpoints.some(
          (cp) => cp.id === member.checkpointId && cp.terminalId === member.terminalId,
        ),
      )
      .map((member) => ({ staffMemberId: member.id, checkpointId: member.checkpointId! }));
    withOperators(this.draft, this.layout);
    this.edited();
  }
  checkInArea() {
    return this.layout?.layout.checkInAreas?.find(
      (area) => area.id === (this.draft?.inputs.checkIn?.areaId ?? area.id),
    );
  }
  protected readonly operatorGroups: { role: OperatorRole; title: string; empty: string }[] = [
    { role: 'security', title: 'Control de securitate', empty: 'Niciun operator la securitate.' },
    { role: 'checkIn', title: 'Check-in', empty: 'Niciun operator la check-in.' },
  ];
  operators(role: OperatorRole) {
    return this.draft?.inputs.operators?.filter((person) => person.role === role) ?? [];
  }
  /** Security operators fill the layout's staff slots; check-in operators its desks. */
  canAdd(role: OperatorRole) {
    if (role === 'security') return !!this.freeSlot();
    return this.operators('checkIn').length < (this.checkInArea()?.deskCount ?? 0);
  }
  private freeSlot() {
    const taken = new Set(this.draft?.inputs.operators?.map((person) => person.id));
    return this.layout?.layout.staff.find((member) => !taken.has(member.id));
  }
  addOperator(role: OperatorRole) {
    if (!this.draft || !this.layout || !this.canAdd(role)) return;
    const inputs = this.draft.inputs;
    const number = this.operators(role).length + 1;
    const team = (inputs.operators ??= []);
    if (role === 'security') {
      const slot = this.freeSlot()!;
      team.push({ id: slot.id, name: `Operator securitate ${number}`, role });
      const checkpoint = this.layout.layout.checkpoints.find(
        (cp) => cp.terminalId === slot.terminalId,
      );
      if (checkpoint) this.allocate(slot.id, checkpoint.id);
    } else {
      // The first check-in operator starts the check-in queue; each one opens a desk.
      team.push({ id: crypto.randomUUID(), name: `Operator check-in ${number}`, role });
      inputs.checkIn = {
        ...(inputs.checkIn ?? defaultCheckIn(this.checkInArea()!)),
        staffCount: number,
      };
    }
    this.changed();
  }
  removeOperator(person: ScenarioOperator) {
    const inputs = this.draft!.inputs;
    inputs.operators = (inputs.operators ?? []).filter((item) => item.id !== person.id);
    if (person.role === 'security') this.allocate(person.id, '');
    if (person.role === 'checkIn') {
      const desks = this.operators('checkIn').length;
      inputs.checkIn = desks && inputs.checkIn ? { ...inputs.checkIn, staffCount: desks } : null;
    }
    this.changed();
  }
  /** An approved shift plan overrides the fixed roster until the scenario goes back to it. */
  useFixedPlan() {
    if (!this.draft) return;
    this.draft.inputs.staffShifts = null;
    this.changed();
  }
  staffCount(checkpointId: string) {
    return (
      this.draft?.inputs.staffAllocations.filter((row) => row.checkpointId === checkpointId)
        .length ?? 0
    );
  }
  startOver() {
    this.protect('selectare configurație inițială', () => {
      this.draft = null;
      this.saved.set(null);
      this.changed();
      this.step.set(1);
    });
  }
  private draftErrors(): FieldErrors {
    if (!this.draft) return {};
    return {
      ...basicErrors(this.draft),
      ...(!this.draft.inputs.flights.length
        ? { 'inputs.flights': ['Adaugă cel puțin un zbor pentru simulare.'] }
        : {}),
    };
  }
  private timingErrors(): FieldErrors {
    return Object.fromEntries(
      Object.entries(this.draft ? basicErrors(this.draft) : {}).filter(
        ([field]) =>
          field.endsWith('AtUtc') ||
          field === 'inputs.timeZone' ||
          field === 'inputs.minimumDepartureSpacingMinutes' ||
          /^inputs\.flights\[\d+\]\.delayMinutes$/.test(field),
      ),
    );
  }
  flightRuleProblems(): string[] {
    if (!this.draft) return [];
    const errors = this.timingErrors(),
      spacing = departureSpacingErrors(this.draft.inputs);
    const fields = Object.keys(errors).filter(
      (field) =>
        field.startsWith('inputs.flights[') ||
        ['inputs.startsAtUtc', 'inputs.timeZone', 'inputs.minimumDepartureSpacingMinutes'].includes(
          field,
        ),
    );
    const problems: string[] = [];
    if (
      fields.some(
        (field) => (field.endsWith('AtUtc') && !spacing[field]) || field === 'inputs.timeZone',
      )
    ) {
      problems.push(
        `Introdu date și ore absolute valide (HH:MM), în fusul ${this.draft.inputs.timeZone}. Pentru o programare după miezul nopții, alege data corespunzătoare.`,
      );
    }
    for (const field of fields) {
      if (spacing[field]) problems.push(...spacing[field]);
      else if (!field.endsWith('AtUtc') && field !== 'inputs.timeZone')
        problems.push(`${this.fieldLabel(field)}: ${errors[field].join(' ')}`);
    }
    return [...new Set(problems)];
  }
  navigationBlocked(target: number): boolean {
    if (this.busy()) return true;
    return (
      target > this.step() &&
      Object.keys(this.timingErrors()).some((field) => this.stepFor(field) < target)
    );
  }
  goStep(target: number) {
    if (this.busy() || !this.draft) return;
    if (target > this.step()) {
      const errors = Object.fromEntries(
        Object.entries(this.draftErrors()).filter(([field]) => this.stepFor(field) < target),
      );
      this.errors.set(errors);
      if (Object.keys(errors).length) {
        this.message.set('Continuare blocată. Câmpurile indicate necesită completare.');
        this.step.set(Math.min(...Object.keys(errors).map((field) => this.stepFor(field))));
        this.focusStep();
        return;
      }
    }
    this.step.set(target);
    if (target === 4 && !this.completed()) this.validate();
    this.focusStep();
  }
  private stepFor(field: string) {
    const section = this.section(field);
    return ['flights', 'populations', 'flows'].includes(section)
      ? 2
      : ['staff', 'events', 'advanced'].includes(section)
        ? 3
        : 1;
  }
  showIssue(field: string) {
    this.step.set(this.stepFor(field));
    this.advancedGeneral.set(true);
    this.advancedTraffic.set(true);
    this.advancedConfig.set(true);
    afterNextRender(
      () => {
        const section = this.host.nativeElement.querySelector<HTMLElement>(
          `#studio-${this.section(field)}`,
        );
        let ancestor = section?.parentElement;
        while (ancestor && ancestor !== this.host.nativeElement) {
          if (ancestor instanceof HTMLDetailsElement) ancestor.open = true;
          ancestor = ancestor.parentElement;
        }
        section?.querySelectorAll('details').forEach((details) => (details.open = true));
        section?.scrollIntoView({ block: 'start' });
        section?.querySelector<HTMLElement>('input, select, button, summary')?.focus();
      },
      { injector: this.injector },
    );
  }
  private focusStep() {
    afterNextRender(
      () => this.host.nativeElement.querySelector<HTMLElement>('#step-heading')?.focus(),
      { injector: this.injector },
    );
  }
  @HostListener('window:beforeunload', ['$event'])
  beforeUnload(event: BeforeUnloadEvent) {
    if (this.dirty() || this.busy()) {
      event.preventDefault();
      event.returnValue = '';
    }
  }
  removeFlight(flight: ScenarioFlight) {
    const input = this.draft!.inputs;
    if (
      input.flows.some((row) => row.flightId === flight.id) ||
      input.events.some((row) => row.flightId === flight.id) ||
      input.integratedResources?.requirements.some((row) => row.flightId === flight.id) ||
      (Array.isArray(input.passengerGeneration?.batches) &&
        input.passengerGeneration.batches.some((row) => row.flightId === flight.id))
    ) {
      this.message.set(
        `Zborul ${flight.code} este folosit de pasageri, evenimente sau resurse. Eliminarea necesită ștergerea referințelor din configurarea detaliată. Resursele integrate sunt editabile în Scenarii și rulări.`,
      );
      return;
    }
    input.flights = input.flights.filter((row) => row.id !== flight.id);
    this.changed();
  }
  addPopulation() {
    this.draft!.inputs.populations.push({
      id: crypto.randomUUID(),
      name: 'Populație demo',
      count: 120,
    });
    this.changed();
  }
  removePopulation(id: string) {
    if (this.draft!.inputs.flows.some((row) => row.populationId === id)) {
      this.message.set(
        'Eliminare blocată: populația este referențiată de un flux. Este necesară eliminarea fluxului asociat.',
      );
      return;
    }
    this.draft!.inputs.populations = this.draft!.inputs.populations.filter((row) => row.id !== id);
    this.changed();
  }
  /** Flights no passenger flow feeds: their gate stays empty in the simulation. */
  flightsWithoutPassengers(): ScenarioFlight[] {
    const input = this.draft?.inputs;
    if (!input) return [];
    return input.flights.filter(
      (flight) =>
        !input.flows.some((flow) => flow.flightId === flight.id && flow.passengerCount > 0),
    );
  }
  missingPassengerCodes(): string {
    return this.flightsWithoutPassengers()
      .map((flight) => flight.code)
      .join(', ');
  }
  addFlow() {
    const input = this.draft!.inputs,
      flight = this.flightsWithoutPassengers()[0] ?? input.flights[0];
    if (
      ![input.startsAtUtc, flight?.departureAtUtc ?? input.startsAtUtc].every((value) =>
        Number.isFinite(Date.parse(value)),
      )
    ) {
      this.message.set(
        'Completează data și ora scenariului și a plecării înainte de a adăuga fluxuri.',
      );
      return;
    }
    this.draft!.inputs.flows.push({
      id: crypto.randomUUID(),
      populationId: input.populations[0]?.id ?? '',
      flightId: flight?.id ?? '',
      routeId:
        this.layout?.layout.routes.find((route) => route.toGateId === flight?.gateId)?.id ?? '',
      passengerCount: 120,
      startsAtUtc: flight
        ? new Date(
            Math.max(
              Date.parse(input.startsAtUtc),
              Date.parse(addMinutes(flight.departureAtUtc, -90)),
            ),
          ).toISOString()
        : input.startsAtUtc,
      endsAtUtc:
        flight?.boardingClosesAtUtc ?? addMinutes(input.startsAtUtc, input.durationMinutes - 2),
    });
    this.changed();
  }
  /** Routes toward the flow's flight gate; every route while no flight is chosen. */
  routesFor(flightId: string) {
    const gateId = this.draft?.inputs.flights.find((flight) => flight.id === flightId)?.gateId;
    const routes = this.layout?.layout.routes ?? [];
    return gateId ? routes.filter((route) => route.toGateId === gateId) : routes;
  }
  removeFlow(id: string) {
    this.draft!.inputs.flows = this.draft!.inputs.flows.filter((row) => row.id !== id);
    this.changed();
  }
  allocate(memberId: string, checkpointId: string) {
    const input = this.draft!.inputs;
    input.staffAllocations = input.staffAllocations.filter((row) => row.staffMemberId !== memberId);
    if (checkpointId) input.staffAllocations.push({ staffMemberId: memberId, checkpointId });
    this.changed();
  }
  assignment(id: string) {
    return (
      this.draft?.inputs.staffAllocations.find((row) => row.staffMemberId === id)?.checkpointId ??
      ''
    );
  }
  addIncident() {
    if (!this.draft || !this.layout) return;
    this.draft.inputs.events = [
      ...this.draft.inputs.events,
      newSurge(this.draft.inputs, this.layout),
    ];
    this.edited();
  }
  isIncident(event: InputEvent) {
    return event.type.startsWith('incident.');
  }
  updateIncident(event: InputEvent) {
    this.draft!.inputs.events = this.draft!.inputs.events.map((row) =>
      row.id === event.id ? event : row,
    );
    this.edited();
  }
  removeEvent(id: string) {
    this.draft!.inputs.events = this.draft!.inputs.events.filter((row) => row.id !== id);
    this.changed();
  }
  payload(event: InputEvent): FieldValue {
    try {
      return JSON.parse(event.payloadJson);
    } catch {
      return {};
    }
  }
  setPayload(event: InputEvent, value: FieldValue) {
    event.payloadJson = JSON.stringify(value);
    this.changed();
  }
  advanced(value: unknown): FieldValue {
    return value as FieldValue;
  }
  setAdvanced(field: 'passengerGeneration' | 'navigation', value: FieldValue) {
    (this.draft!.inputs[field] as unknown) = value;
    this.changed();
  }
  canLeave(): boolean {
    return (
      !this.busy() &&
      (!this.dirty() ||
        window.confirm(
          'Modificări nesalvate în Scenario Studio. Confirmați eliminarea modificărilor și închiderea editorului.',
        ))
    );
  }
  issues(prefix = '') {
    return Object.entries(this.errors())
      .filter(([field]) => field.startsWith(prefix))
      .flatMap(([field, messages]) =>
        messages.map((message) => ({
          field,
          label: this.fieldLabel(field),
          message: this.friendly(message),
        })),
      );
  }
  section(field: string) {
    return field.includes('flights')
      ? 'flights'
      : field.includes('populations')
        ? 'populations'
        : field.includes('flows')
          ? 'flows'
          : field.includes('staffAllocations') ||
              field.includes('operators') ||
              field.includes('checkIn')
            ? 'staff'
            : field.includes('events')
              ? 'events'
              : field.includes('navigation') || field.includes('passengerGeneration')
                ? 'advanced'
                : 'general';
  }
  fieldLabel(field: string) {
    const normalized = field
      .replace(/\/(\d+)(?=\/|$)/g, '[$1]')
      .replaceAll('/', '.')
      .replace(/^\./, '');
    const match = normalized.match(
      /inputs\.(flights|populations|flows|staffAllocations|operators|events)\[(\d+)\]/,
    );
    const labels: Record<string, string> = {
      flights: 'Zbor',
      populations: 'Populație',
      flows: 'Flux',
      staffAllocations: 'Alocare personal',
      operators: 'Operator',
      events: 'Eveniment',
      id: 'Identificator',
      code: 'Cod',
      name: 'Nume',
      description: 'Descriere',
      startsAtUtc: 'Început UTC',
      durationMinutes: 'Durată',
      seed: 'Seed',
      timeZone: 'Fus orar',
      snapshotIntervalMinutes: 'Interval snapshot',
      minimumDepartureSpacingMinutes: 'Interval minim între plecări',
      passengerCapacity: 'Capacitate pasageri',
      gateId: 'Poartă',
      departureAtUtc: 'Plecare',
      boardingOpensAtUtc: 'Deschidere îmbarcare',
      boardingClosesAtUtc: 'Închidere îmbarcare',
      delayMinutes: 'Întârziere',
      status: 'Status demo',
      count: 'Număr pasageri',
      passengerCount: 'Pasageri alocați',
      populationId: 'Populație',
      flightId: 'Zbor',
      routeId: 'Traseu',
      endsAtUtc: 'Sfârșit',
      staffMemberId: 'Persoană',
      checkpointId: 'Checkpoint',
      occurredAtUtc: 'Moment',
      type: 'Tip',
      payloadJson: 'Detalii eveniment',
      share: 'Pondere passenger surge',
      contractVersion: 'Versiune incident',
      isSynthetic: 'Statut sintetic',
      inputs: 'Configurație',
      navigation: 'Navigare',
      passengerGeneration: 'Profiluri de pasageri',
      layoutVersionId: 'Versiune layout',
      sceneId: 'Aeroport',
    };
    const last = normalized.split('.').at(-1)!;
    return `${match ? `${labels[match[1]]} ${Number(match[2]) + 1} → ` : ''}${labels[last] ?? normalized}`;
  }
  private friendly(message: string): string {
    const translations: Record<string, string> = {
      'A value is required.': 'Completează acest câmp.',
      'Must be positive.': 'Valoarea trebuie să fie pozitivă.',
      'Boarding must close after it opens.': 'Închiderea îmbarcării trebuie să urmeze deschiderii.',
      'Boarding must close strictly before departure.':
        'Îmbarcarea trebuie să se închidă înainte de plecare.',
      'Must fall within the scenario time window.':
        'Momentul trebuie să fie în intervalul scenariului.',
      'Must be after the flow start.': 'Sfârșitul trebuie să urmeze începutului fluxului.',
      "Flow must finish by the flight's effective boarding closure.":
        'Fluxul trebuie să se încheie cel târziu la închiderea efectivă a îmbarcării.',
      'A staff member can be allocated only once per scenario.':
        'Aceeași persoană poate fi alocată o singură dată.',
      'Staff member is not available in the selected layout version.':
        'Persoana nu este disponibilă în această versiune de layout.',
      "Route must lead to the flight's gate.": 'Traseul trebuie să ajungă la poarta zborului.',
      'Flight codes must be unique within the scenario.':
        'Codul zborului trebuie să fie unic în scenariu.',
    };
    let text = translations[message] ?? message;
    for (const row of this.draft?.inputs.flights ?? []) text = text.replaceAll(row.id, row.code);
    for (const row of this.draft?.inputs.populations ?? [])
      text = text.replaceAll(row.id, row.name);
    return text;
  }
}

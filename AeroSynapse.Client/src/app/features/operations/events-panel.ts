import { WriteAccess } from '../../core/auth/write-access';
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
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { finalize, Observable } from 'rxjs';
import { ApiClient } from '../../core/http/api-client';
import { NavigationSettings } from './navigation.models';
import { ScenarioDetails } from './operations.models';
import {
  EVENT_TYPES,
  EventType,
  eventLabel,
  isOperational,
  priority,
  ScenarioEvent,
} from './events.models';
import { AbsoluteTimeInput } from '../../shared/ui/absolute-time-input';
import { absoluteTimeLabel, addMinutes, clockTime } from '../../shared/absolute-time';

interface Draft {
  type: EventType;
  occurredAtUtc: string;
  checkpointId: string;
  flightId: string;
  zoneId: string;
  target: 'checkpoint' | 'zone';
  passengerCount: number;
  windowMinutes: number;
  staffCount: number;
  delayMinutes: number;
}

@Component({
  selector: 'app-events-panel',
  imports: [WriteAccess, FormsModule, AbsoluteTimeInput],
  templateUrl: './events-panel.html',
  styleUrl: './setup-panels.css',
})
export class EventsPanel {
  private readonly api = inject(ApiClient);
  private readonly destroy = inject(DestroyRef);
  readonly scenario = input.required<ScenarioDetails>();
  readonly disabled = input(false);
  /** Controls and corridors load when the tab is first shown, not on every scenario selection. */
  readonly active = input(false);
  readonly changed = output<ScenarioDetails>();
  readonly working = output<boolean>();
  readonly settings = signal<NavigationSettings | null>(null);
  readonly loading = signal(false);
  readonly busy = signal(false);
  readonly message = signal('');
  readonly types = EVENT_TYPES;
  draft: Draft | null = null;
  private scenarioId: string | null = null;

  /** Operational events in application order; descriptive notes stay out of the timeline. */
  readonly timeline = computed(() =>
    this.scenario()
      .inputs.events.filter((item) => isOperational(item.type))
      .map((item) => ({
        item,
        time: clockTime(item.occurredAtUtc, this.scenario().inputs.timeZone),
        full: this.clock(item.occurredAtUtc),
      }))
      .sort(
        (a, b) =>
          Date.parse(a.item.occurredAtUtc) - Date.parse(b.item.occurredAtUtc) ||
          priority(a.item.type) - priority(b.item.type) ||
          (a.item.id < b.item.id ? -1 : a.item.id > b.item.id ? 1 : 0),
      ),
  );
  readonly notes = computed(
    () => this.scenario().inputs.events.filter((item) => !isOperational(item.type)).length,
  );
  readonly zones = computed(() => this.settings()?.config?.graph.nodes ?? []);

  constructor() {
    effect((onCleanup) => {
      const id = this.scenario().scenario.id;
      const revision = this.scenario().scenario.revision;
      if (id !== this.scenarioId) {
        this.scenarioId = id;
        this.draft = null;
        this.message.set('');
      }
      if (!this.active()) return;
      untracked(() => {
        if (this.settings()?.scenarioId === id && this.settings()?.revision === revision) return;
        this.settings.set(null);
        this.loading.set(true);
        const subscription = this.api
          .get<NavigationSettings>(`scenarios/${id}/navigation/settings`)
          .pipe(finalize(() => this.loading.set(false)))
          .subscribe({
            next: (value) => this.settings.set(value),
            error: (error) => this.report(error),
          });
        onCleanup(() => subscription.unsubscribe());
      });
    });
  }

  add() {
    if (this.blocked()) return;
    const settings = this.settings();
    this.draft = {
      type: 'checkpoint_unavailable',
      occurredAtUtc:
        this.scenario().inputs.flights[0]?.boardingOpensAtUtc ?? this.scenario().inputs.startsAtUtc,
      checkpointId: settings?.checkpoints[0]?.id ?? '',
      flightId: this.scenario().inputs.flights[0]?.id ?? '',
      zoneId: this.zones()[0]?.id ?? '',
      target: 'checkpoint',
      passengerCount: 50,
      windowMinutes: 10,
      staffCount: 0,
      delayMinutes: 15,
    };
    this.message.set('');
  }

  save() {
    const draft = this.draft;
    const inputs = this.scenario().inputs;
    if (!draft || this.blocked()) return;
    const timestamp = Date.parse(draft.occurredAtUtc);
    if (
      !Number.isFinite(timestamp) ||
      timestamp < Date.parse(inputs.startsAtUtc) ||
      timestamp >= Date.parse(addMinutes(inputs.startsAtUtc, inputs.durationMinutes)) ||
      (timestamp - Date.parse(inputs.startsAtUtc)) % 60000 !== 0
    ) {
      this.message.set(
        'Alege o dată și o oră (HH:MM) în intervalul scenariului, la precizie de minut.',
      );
      return;
    }
    const uses = (field: string) => this.uses(draft, field);
    const parameters: Record<string, number | string> = {};
    for (const name of ['passengerCount', 'windowMinutes', 'staffCount', 'delayMinutes'] as const)
      if (uses(name)) parameters[name] = draft[name];
    if (uses('zoneId')) parameters['zoneId'] = draft.zoneId;
    const event: ScenarioEvent = {
      id: crypto.randomUUID(),
      type: draft.type,
      occurredAtUtc: draft.occurredAtUtc,
      checkpointId: uses('checkpointId') ? draft.checkpointId || null : null,
      flightId: uses('flightId') ? draft.flightId || null : null,
      gateId: null,
      payloadJson: JSON.stringify(parameters),
    };
    const scenario = this.scenario().scenario;
    this.mutate(
      this.api.post<ScenarioDetails>(`scenarios/${scenario.id}/events`, {
        revision: scenario.revision,
        event,
      }),
    );
  }

  cancel() {
    this.draft = null;
    this.message.set('');
  }

  remove(event: ScenarioEvent) {
    const scenario = this.scenario().scenario;
    if (this.blocked()) return;
    this.mutate(
      this.api.delete<ScenarioDetails>(
        `scenarios/${scenario.id}/events/${event.id}?revision=${scenario.revision}`,
      ),
    );
  }

  /** Which inputs an event type takes; mirrors the engine's field rules. */
  uses(draft: Draft, field: string): boolean {
    switch (draft.type) {
      case 'passenger_surge':
        return ['checkpointId', 'passengerCount', 'windowMinutes', 'flightId'].includes(field);
      case 'checkpoint_unavailable':
        return field === 'checkpointId';
      case 'staff_reduction':
        return field === 'checkpointId' || field === 'staffCount';
      case 'flight_change':
        return field === 'flightId' || field === 'delayMinutes';
      case 'corridor_closure':
        return field === 'zoneId';
      case 'resume':
        return field === (draft.target === 'zone' ? 'zoneId' : 'checkpointId');
    }
  }

  label = eventLabel;
  target(event: ScenarioEvent): string {
    const payload = this.payload(event);
    if (event.checkpointId)
      return (
        this.settings()?.checkpoints.find((item) => item.id === event.checkpointId)?.name ??
        event.checkpointId
      );
    if (typeof payload['zoneId'] === 'string')
      return (
        this.zones().find((item) => item.id === payload['zoneId'])?.name ??
        String(payload['zoneId'])
      );
    if (event.flightId)
      return (
        this.scenario().inputs.flights.find((item) => item.id === event.flightId)?.code ??
        event.flightId
      );
    return '—';
  }
  details(event: ScenarioEvent): string {
    const payload = this.payload(event);
    const flight =
      event.type === 'passenger_surge' && event.flightId
        ? ` · zbor ${this.scenario().inputs.flights.find((item) => item.id === event.flightId)?.code ?? event.flightId}`
        : '';
    const delay = Number(payload['delayMinutes']);
    switch (event.type) {
      case 'passenger_surge':
        return `${payload['passengerCount']} pasageri în ${payload['windowMinutes']} min${flight}`;
      case 'staff_reduction':
        return `rămân ${payload['staffCount']} operatori`;
      case 'flight_change':
        return this.scenario().inputs.journey
          ? `zborul întârzie ${delay} min; porțile se replanifică`
          : `boardingul se mută cu ${delay > 0 ? '+' : ''}${delay} min`;
      case 'resume':
        return typeof payload['zoneId'] === 'string'
          ? 'coridorul se redeschide'
          : 'controlul revine la normal';
      case 'checkpoint_unavailable':
      case 'corridor_closure':
        return 'închis până la reluare';
      default:
        return '—';
    }
  }
  clock(time: string) {
    return absoluteTimeLabel(time, this.scenario().inputs.timeZone);
  }
  blocked() {
    return this.disabled() || this.loading() || this.busy();
  }

  private payload(event: ScenarioEvent): Record<string, unknown> {
    try {
      return JSON.parse(event.payloadJson) as Record<string, unknown>;
    } catch {
      return {};
    }
  }
  private mutate(request: Observable<ScenarioDetails>) {
    this.busy.set(true);
    this.working.emit(true);
    this.message.set('');
    request
      .pipe(
        finalize(() => {
          this.busy.set(false);
          this.working.emit(false);
        }),
        takeUntilDestroyed(this.destroy),
      )
      .subscribe({
        next: (value) => {
          this.draft = null;
          this.changed.emit(value);
        },
        error: (error) => this.report(error),
      });
  }
  private report(error: unknown) {
    this.message.set(error instanceof Error ? error.message : 'Operația nu a putut fi finalizată.');
  }
}

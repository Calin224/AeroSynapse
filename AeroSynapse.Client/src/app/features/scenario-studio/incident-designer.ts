import { Component, computed, input, output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ScenarioInputs } from '../operations/operations.models';
import { InputEvent, StudioLayout } from './studio.models';
import { AbsoluteTimeInput } from '../../shared/ui/absolute-time-input';
import { absoluteTimeLabel, addMinutes, clockTime } from '../../shared/absolute-time';

export const PASSENGER_SURGE = 'incident.passenger-surge';
export interface SurgePayload {
  contractVersion: '1.0';
  durationMinutes: number;
  share: number;
  isSynthetic: true;
}
export function surgePayload(event: InputEvent): SurgePayload | null {
  try {
    const value = JSON.parse(event.payloadJson);
    return value &&
      Object.keys(value).sort().join(',') === 'contractVersion,durationMinutes,isSynthetic,share' &&
      value.contractVersion === '1.0' &&
      value.isSynthetic === true &&
      typeof value.durationMinutes === 'number' &&
      Number.isFinite(value.durationMinutes) &&
      typeof value.share === 'number' &&
      Number.isFinite(value.share)
      ? value
      : null;
  } catch {
    return null;
  }
}
export function incidentCheckpoint(
  inputs: ScenarioInputs,
  layout: StudioLayout,
  flightId: string,
): string | null {
  const batch = inputs.passengerGeneration?.batches.find((batch) => batch.flightId === flightId);
  if (inputs.passengerGeneration) return batch?.checkpointId ?? null;
  const flight = inputs.flights.find((flight) => flight.id === flightId);
  const preferred = inputs.flows.find((flow) => flow.flightId === flightId)?.routeId;
  return (
    layout.layout.routes
      .filter((route) => route.toGateId === flight?.gateId)
      .sort(
        (a, b) =>
          Number(b.id === preferred) - Number(a.id === preferred) || a.code.localeCompare(b.code),
      )[0]?.fromCheckpointId ?? null
  );
}
/** Match the API's derived profiles and effective boarding closure, including legacy flights. */
export function incidentArrivalWindow(inputs: ScenarioInputs, flightId: string | null | undefined) {
  const flight = inputs.flights.find((row) => row.id === flightId);
  if (!flight || flight.status === 'cancelled') return null;
  const scenarioStart = Date.parse(inputs.startsAtUtc);
  const departure = Date.parse(flight.departureAtUtc);
  const available = Math.max(0, departure - scenarioStart);
  const close =
    (flight.boardingClosesAtUtc
      ? Date.parse(flight.boardingClosesAtUtc)
      : !flight.boardingOpensAtUtc
        ? departure - Math.min(300000, Math.min(1800000, available) / 2)
        : NaN) +
    (flight.delayMinutes ?? 0) * 60000;
  const batch = inputs.passengerGeneration?.batches.find((row) => row.flightId === flightId);
  if (inputs.passengerGeneration && !batch) return null;
  // Only the legacy passenger-profile contract uses offsets. Convert them to instants at this boundary.
  const minimum = Math.max(
    scenarioStart,
    batch ? Date.parse(addMinutes(inputs.startsAtUtc, batch.arrival.minimum)) : scenarioStart,
  );
  const maximum = Math.min(
    Date.parse(addMinutes(inputs.startsAtUtc, inputs.durationMinutes)),
    batch ? Date.parse(addMinutes(inputs.startsAtUtc, batch.arrival.maximum)) : close,
    close,
  );
  return Number.isFinite(minimum) && Number.isFinite(maximum) && maximum > minimum
    ? { start: new Date(minimum).toISOString(), end: new Date(maximum).toISOString() }
    : null;
}
export function scenarioClock(inputs: ScenarioInputs, utc: string) {
  return clockTime(utc, inputs.timeZone || 'UTC');
}
export function flightPassengers(inputs: ScenarioInputs, flightId: string) {
  const batch = inputs.passengerGeneration?.batches.find((row) => row.flightId === flightId);
  return (
    batch?.modeledCount ??
    inputs.flows
      .filter((row) => row.flightId === flightId)
      .reduce((sum, row) => sum + row.passengerCount, 0)
  );
}
/** One line a person can choose a flight by: code, departure time, gate, passengers. */
export function flightLabel(inputs: ScenarioInputs, layout: StudioLayout, flightId: string | null) {
  const flight = inputs.flights.find((row) => row.id === flightId);
  if (!flight) return 'zbor lipsă';
  const gate = layout.layout.gates.find((row) => row.id === flight.gateId)?.code ?? 'poartă lipsă';
  return [
    flight.code,
    `pleacă ${scenarioClock(inputs, flight.departureAtUtc)}`,
    gate,
    `${flightPassengers(inputs, flight.id)} pasageri`,
    ...(flight.status === 'cancelled' ? ['anulat'] : []),
  ].join(' · ');
}
/** Plain-language summary of a saved surge, e.g. "Val de pasageri · AB12 · 09:05–09:10 · 50%". */
export function describeSurge(event: InputEvent, inputs: ScenarioInputs) {
  const payload = surgePayload(event);
  const flight = inputs.flights.find((row) => row.id === event.flightId)?.code ?? 'zbor lipsă';
  if (event.type !== PASSENGER_SURGE || !payload) return `Incident ${event.type} · ${flight}`;
  return `Val de pasageri · ${flight} · ${scenarioClock(inputs, event.occurredAtUtc)}–${scenarioClock(inputs, addMinutes(event.occurredAtUtc, payload.durationMinutes))} · ${Math.round(payload.share * 100)}% din pasageri`;
}
export const SURGE_PRESETS = [
  { label: 'Ușor', share: 0.25 },
  { label: 'Mediu', share: 0.5 },
  { label: 'Puternic', share: 0.8 },
] as const;
export function newSurge(inputs: ScenarioInputs, layout: StudioLayout): InputEvent {
  const flight = inputs.flights.find((flight) => flight.status !== 'cancelled');
  const window = incidentArrivalWindow(inputs, flight?.id ?? null);
  return {
    id: crypto.randomUUID(),
    type: PASSENGER_SURGE,
    occurredAtUtc: window?.start ?? flight?.boardingOpensAtUtc ?? inputs.startsAtUtc,
    flightId: flight?.id ?? null,
    gateId: flight?.gateId ?? null,
    checkpointId: flight ? incidentCheckpoint(inputs, layout, flight.id) : null,
    payloadJson: JSON.stringify({
      contractVersion: '1.0',
      durationMinutes: window
        ? Math.min(5, (Date.parse(window.end) - Date.parse(window.start)) / 60000)
        : 5,
      share: 0.5,
      isSynthetic: true,
    }),
  };
}

export function incidentProblems(
  event: InputEvent,
  inputs: ScenarioInputs,
  layout: StudioLayout,
): string[] {
  const payload = surgePayload(event),
    errors: string[] = [];
  const start = Date.parse(event.occurredAtUtc);
  const end = Date.parse(addMinutes(event.occurredAtUtc, payload?.durationMinutes ?? NaN));
  if (event.type !== PASSENGER_SURGE) errors.push('Tip de incident nesuportat.');
  if (!payload)
    return [...errors, 'Payload invalid sau versiune nesuportată. Nu poate fi executat.'];
  if (payload.share <= 0 || payload.share > 1)
    errors.push('Ponderea trebuie să fie mai mare decât 0% și cel mult 100%.');
  if (
    payload.durationMinutes <= 0 ||
    !Number.isFinite(start) ||
    !Number.isFinite(end) ||
    start < Date.parse(inputs.startsAtUtc) ||
    end > Date.parse(addMinutes(inputs.startsAtUtc, inputs.durationMinutes))
  )
    errors.push('Întregul interval trebuie să fie în scenariu și durata trebuie să fie pozitivă.');
  const flight = inputs.flights.find((f) => f.id === event.flightId);
  if (!flight || flight.status === 'cancelled') errors.push('Este necesar un zbor activ.');
  if (flight && flight.status !== 'cancelled') {
    const window = incidentArrivalWindow(inputs, flight.id);
    if (!window) errors.push('Zborul selectat nu are o fereastră validă de sosire.');
    else if (start < Date.parse(window.start) || end > Date.parse(window.end))
      errors.push(
        `Intervalul incidentului pentru ${flight.code} trebuie inclus în fereastra de sosire ${absoluteTimeLabel(window.start, inputs.timeZone)} – ${absoluteTimeLabel(window.end, inputs.timeZone)}.`,
      );
  }
  if (
    flight &&
    (event.gateId !== flight.gateId ||
      event.checkpointId !== incidentCheckpoint(inputs, layout, flight.id))
  )
    errors.push(
      'Resursele nu corespund zborului. Actualizarea resurselor necesită reselectarea zborului.',
    );
  return errors;
}

@Component({
  selector: 'app-incident-designer',
  imports: [FormsModule, AbsoluteTimeInput],
  template: `
    <fieldset [disabled]="disabled()">
      <div class="fields">
        <label
          >Tip<select [ngModel]="event().type" (ngModelChange)="setType($event)">
            @if (event().type !== surgeType) {
              <option [value]="event().type">Tip nesuportat: {{ event().type }}</option>
            }
            <option [value]="surgeType">Passenger surge · vârf de sosire</option>
          </select></label
        >
        <label
          >Zbor afectat<select [ngModel]="event().flightId" (ngModelChange)="setFlight($event)">
            <option [ngValue]="null">Selectare zbor</option>
            @for (flight of inputs().flights; track flight.id) {
              <option [value]="flight.id">{{ flightName(flight.id) }}</option>
            }
          </select></label
        >
        <div>
          <app-absolute-time-input
            label="Ora de început a incidentului"
            [value]="event().occurredAtUtc"
            [timeZone]="inputs().timeZone"
            [disabled]="disabled()"
            (changed)="setTime($event)"
          />
          <small>{{ timing() }}</small>
        </div>
        <label
          >Durată (minute)<input
            type="number"
            step="any"
            min="0.001"
            [max]="maximumDuration()"
            [ngModel]="duration()"
            (ngModelChange)="setPayload('durationMinutes', $event)"
        /></label>
        <div class="intensity">
          <label
            >Ponderea pasagerilor afectați (%)<input
              type="number"
              step="any"
              min="0.001"
              max="100"
              [ngModel]="percentage()"
              (ngModelChange)="setPayload('share', $event === null ? null : $event / 100)"
          /></label>
          <div class="presets" role="group" aria-label="Intensitate rapidă">
            @for (preset of presets; track preset.share) {
              <button
                type="button"
                [attr.aria-pressed]="percentage() === preset.share * 100"
                (click)="setPayload('share', preset.share)"
              >
                {{ preset.label }} · {{ preset.share * 100 }}%
              </button>
            }
          </div>
        </div>
        @if (!payload()) {
          <button type="button" (click)="resetPayload()">Resetare parametri incident v1.0</button>
        }
      </div>
    </fieldset>
    @if (arrivalWindow(); as window) {
      <p class="hint" data-testid="incident-arrival-window">
        Fereastră de sosire acceptată: {{ clock(window.start) }}–{{ clock(window.end) }} ({{
          inputs().timeZone
        }}).
      </p>
    }
    @if (payload(); as value) {
      <p class="impact">
        Între {{ clock(event().occurredAtUtc) }} și {{ endClock() }} aproximativ
        {{ value.share * 100 }}% dintre pasagerii zborului sosesc în intervalul specificat la
        {{ impact() }}.
      </p>
    } @else {
      <p class="impact">Resurse afectate: {{ impact() }}.</p>
    }
    @for (problem of problems(); track $index) {
      <p class="error" role="alert">{{ problem }}</p>
    }
  `,
  styles: `
    :host {
      display: block;
    }
    fieldset {
      border: 0;
      padding: 0;
      margin: 0;
      min-width: 0;
    }
    .fields {
      display: grid;
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 0.9rem;
      margin: 1rem 0;
    }
    label {
      display: grid;
      gap: 0.4rem;
      font-size: 0.85rem;
    }
    small {
      color: var(--text-muted);
      font-size: 0.8rem;
    }
    .intensity {
      display: grid;
      gap: 0.5rem;
      grid-column: 1 / -1;
    }
    .presets {
      display: flex;
      flex-wrap: wrap;
      gap: 0.4rem;
    }
    .presets button[aria-pressed='true'] {
      border-color: var(--accent);
      background: var(--accent-soft);
      color: var(--accent);
      font-weight: 600;
    }
    input,
    select {
      width: 100%;
      min-width: 0;
    }
    .identifier,
    .hint {
      color: var(--text-muted);
      font-size: 0.8rem;
    }
    code {
      overflow-wrap: anywhere;
    }
    .impact {
      margin: 0 0 0.5rem;
      padding: 0.75rem 1rem;
      border-radius: var(--radius);
      background: var(--accent-soft);
      color: var(--text);
    }
    .error {
      color: var(--danger);
    }
    @media (max-width: 650px) {
      .fields {
        grid-template-columns: 1fr;
      }
    }
  `,
})
export class IncidentDesigner {
  readonly arrivalWindow = computed(() =>
    incidentArrivalWindow(this.inputs(), this.event().flightId),
  );
  readonly maximumDuration = computed(() => {
    const window = this.arrivalWindow(),
      start = Date.parse(this.event().occurredAtUtc);
    return window && Number.isFinite(start)
      ? Math.max(0, (Date.parse(window.end) - start) / 60000)
      : null;
  });
  readonly event = input.required<InputEvent>();
  readonly inputs = input.required<ScenarioInputs>();
  readonly layout = input.required<StudioLayout>();
  readonly disabled = input(false);
  readonly changed = output<InputEvent>();
  readonly surgeType = PASSENGER_SURGE;
  readonly presets = SURGE_PRESETS;
  private readonly formPayload = computed<Record<string, unknown>>(() => {
    try {
      const value = JSON.parse(this.event().payloadJson);
      return value && typeof value === 'object' && !Array.isArray(value) ? value : {};
    } catch {
      return {};
    }
  });
  readonly duration = computed(() =>
    typeof this.formPayload()['durationMinutes'] === 'number'
      ? (this.formPayload()['durationMinutes'] as number)
      : null,
  );
  readonly percentage = computed(() =>
    typeof this.formPayload()['share'] === 'number'
      ? (this.formPayload()['share'] as number) * 100
      : null,
  );
  readonly payload = computed(() => surgePayload(this.event()));
  readonly endClock = computed(() =>
    this.clock(addMinutes(this.event().occurredAtUtc, this.duration() ?? NaN)),
  );
  readonly impact = computed(() => {
    const event = this.event(),
      layout = this.layout().layout;
    return [
      this.inputs().flights.find((f) => f.id === event.flightId)?.code ?? 'zbor lipsă',
      layout.checkpoints.find((c) => c.id === event.checkpointId)?.name ?? 'checkpoint lipsă',
      layout.gates.find((g) => g.id === event.gateId)?.name ?? 'poartă lipsă',
    ].join(' · ');
  });
  /** Wall clock and distance to the chosen flight's departure, for the start field. */
  readonly timing = computed(() => {
    const inputs = this.inputs(),
      utc = this.event().occurredAtUtc,
      flight = inputs.flights.find((row) => row.id === this.event().flightId);
    if (!Number.isFinite(Date.parse(utc))) return 'Introdu data și ora de început (HH:MM).';
    const clock = `ora ${scenarioClock(inputs, utc)} · ${inputs.timeZone}`;
    if (!flight) return clock;
    return `${clock} · ${flight.code} pleacă la ${scenarioClock(inputs, flight.departureAtUtc)}`;
  });
  readonly problems = computed(() => incidentProblems(this.event(), this.inputs(), this.layout()));
  flightName(id: string) {
    return flightLabel(this.inputs(), this.layout(), id);
  }
  clock(utc: string) {
    return scenarioClock(this.inputs(), utc);
  }
  setType(type: string) {
    this.changed.emit({ ...this.event(), type });
  }
  setFlight(flightId: string | null) {
    const flight = this.inputs().flights.find((f) => f.id === flightId);
    const window = incidentArrivalWindow(this.inputs(), flightId);
    const current = Date.parse(this.event().occurredAtUtc),
      duration = this.duration();
    const start = window
      ? Math.max(
          Date.parse(window.start),
          Math.min(
            Number.isFinite(current) ? current : Date.parse(window.start),
            Date.parse(window.end) -
              (duration !== null && Number.isFinite(duration) ? duration : 5) * 60000,
          ),
        )
      : current;
    this.changed.emit({
      ...this.event(),
      flightId,
      gateId: flight?.gateId ?? null,
      checkpointId: flight ? incidentCheckpoint(this.inputs(), this.layout(), flight.id) : null,
      occurredAtUtc:
        window && Number.isFinite(start) && Math.abs(start) <= 8.64e15
          ? new Date(start).toISOString()
          : this.event().occurredAtUtc,
    });
  }
  setTime(utc: string) {
    this.changed.emit({
      ...this.event(),
      occurredAtUtc: utc,
    });
  }
  setPayload(field: 'durationMinutes' | 'share', value: number | null) {
    this.changed.emit({
      ...this.event(),
      payloadJson: JSON.stringify({ ...this.formPayload(), [field]: value }),
    });
  }
  resetPayload() {
    this.changed.emit({
      ...this.event(),
      payloadJson: JSON.stringify({
        contractVersion: '1.0',
        durationMinutes: 5,
        share: 0.5,
        isSynthetic: true,
      }),
    });
  }
}

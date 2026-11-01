import {
  Component,
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
import { FlightSchedule, ScenarioDetails, ScenarioFlight } from './operations.models';
import { AbsoluteTimeInput } from '../../shared/ui/absolute-time-input';
import { absoluteTimeLabel, addMinutes, clockTime } from '../../shared/absolute-time';

@Component({
  selector: 'app-flight-schedule-panel',
  imports: [FormsModule, AbsoluteTimeInput],
  templateUrl: './flight-schedule-panel.html',
  styleUrl: './flight-schedule-panel.css',
})
export class FlightSchedulePanel {
  private readonly api = inject(ApiClient);
  private readonly destroy = inject(DestroyRef);
  private readonly refresh = signal(0);
  readonly scenario = input.required<ScenarioDetails>();
  readonly disabled = input(false);
  readonly changed = output<ScenarioDetails>();
  readonly working = output<boolean>();
  readonly schedule = signal<FlightSchedule | null>(null);
  readonly loading = signal(false);
  readonly busy = signal(false);
  readonly message = signal('');
  readonly confirmDelete = signal(false);
  readonly statuses = [
    { value: 'scheduled', label: 'Programat' },
    { value: 'delayed', label: 'Întârziat' },
    { value: 'boarding', label: 'Îmbarcare' },
    { value: 'closed', label: 'Îmbarcare închisă' },
    { value: 'departed', label: 'Plecat' },
    { value: 'cancelled', label: 'Anulat' },
  ];
  draft: {
    id: string;
    code: string;
    gateId: string;
    departure: string;
    open: string;
    close: string;
    capacity: number;
    delay: number;
    status: string;
  } | null = null;
  private scenarioId = '';

  constructor() {
    effect((onCleanup) => {
      const scenario = this.scenario();
      this.refresh();
      untracked(() => {
        if (this.scenarioId !== scenario.scenario.id) this.draft = null;
        this.scenarioId = scenario.scenario.id;
        this.schedule.set(null);
        this.loading.set(true);
        const subscription = this.api
          .get<FlightSchedule>(`scenarios/${scenario.scenario.id}/flight-schedule`)
          .pipe(finalize(() => this.loading.set(false)))
          .subscribe({
            next: (value) => this.schedule.set(value),
            error: (error) => this.report(error),
          });
        onCleanup(() => subscription.unsubscribe());
      });
    });
  }

  reload() {
    if (!this.busy()) {
      this.message.set('');
      this.draft = null;
      this.confirmDelete.set(false);
      this.refresh.update((value) => value + 1);
    }
  }
  edit(flight?: ScenarioFlight) {
    const schedule = this.schedule();
    if (!schedule || this.blocked()) return;
    const last = Math.max(
      Date.parse(schedule.startsAtUtc),
      ...schedule.flights
        .filter((row) => row.status !== 'cancelled')
        .map((row) => Date.parse(addMinutes(row.departureAtUtc, row.delayMinutes ?? 0)))
        .filter(Number.isFinite),
    );
    const departure =
      flight?.departureAtUtc ??
      addMinutes(
        new Date(last).toISOString(),
        this.scenario().inputs.minimumDepartureSpacingMinutes ?? 60,
      );
    this.draft = {
      id: flight?.id ?? '',
      code: flight?.code ?? `ZBOR-${schedule.flights.length + 1}`,
      gateId: flight?.gateId ?? schedule.gates[0]?.id ?? '',
      departure,
      open: flight?.boardingOpensAtUtc ?? addMinutes(departure, -30),
      close: flight?.boardingClosesAtUtc ?? addMinutes(departure, -5),
      capacity: flight?.passengerCapacity ?? 120,
      delay: flight?.delayMinutes ?? 0,
      status: flight?.status ?? 'scheduled',
    };
    this.confirmDelete.set(false);
    this.message.set('');
  }
  save() {
    const schedule = this.schedule();
    const draft = this.draft;
    if (!schedule || !draft || this.blocked()) return;
    const end = addMinutes(schedule.startsAtUtc, schedule.durationMinutes);
    const error =
      !draft.code.trim() ||
      !draft.gateId ||
      !Number.isInteger(draft.capacity) ||
      draft.capacity < 1 ||
      draft.capacity > 2147483647 ||
      !Number.isInteger(draft.delay) ||
      draft.delay < 0 ||
      draft.delay > 10080
        ? 'Completează codul, poarta, locurile (cel puțin 1) și întârzierea (0–10080 minute).'
        : ![draft.open, draft.close, draft.departure].every((value) =>
              Number.isFinite(Date.parse(value)),
            ) ||
            Date.parse(draft.open) >= Date.parse(draft.close) ||
            Date.parse(draft.close) >= Date.parse(draft.departure)
          ? 'Îmbarcarea trebuie să înceapă înainte să se închidă, iar ambele înainte de plecare.'
          : Date.parse(draft.departure) < Date.parse(schedule.startsAtUtc) ||
              Date.parse(addMinutes(draft.departure, draft.delay)) > Date.parse(end)
            ? `Plecarea, cu tot cu întârzierea, trebuie să fie în intervalul simulării (${this.time(schedule.startsAtUtc)}–${this.time(end)}).`
            : '';
    if (error) {
      this.message.set(error);
      return;
    }
    const flight: ScenarioFlight = {
      id: draft.id || crypto.randomUUID(),
      code: draft.code.trim(),
      gateId: draft.gateId,
      departureAtUtc: draft.departure,
      boardingOpensAtUtc: draft.open,
      boardingClosesAtUtc: draft.close,
      passengerCapacity: draft.capacity,
      delayMinutes: draft.delay,
      status: draft.status,
      isSynthetic: true,
    };
    const path = `scenarios/${schedule.scenarioId}/flights`;
    this.mutate(
      draft.id
        ? this.api.put<ScenarioDetails>(`${path}/${draft.id}`, {
            revision: schedule.revision,
            flight,
          })
        : this.api.post<ScenarioDetails>(path, { revision: schedule.revision, flight }),
    );
  }
  remove(flight: { id: string }) {
    const schedule = this.schedule();
    if (!schedule || this.blocked()) return;
    this.mutate(
      this.api.delete<ScenarioDetails>(
        `scenarios/${schedule.scenarioId}/flights/${flight.id}?revision=${schedule.revision}`,
      ),
    );
  }
  gateName(id: string) {
    return this.schedule()?.gates.find((gate) => gate.id === id)?.code ?? id;
  }
  statusName(status?: string) {
    return this.statuses.find((item) => item.value === status)?.label ?? 'Programat';
  }
  effective(time: string | null | undefined, delay = 0) {
    return time ? new Date(Date.parse(time) + delay * 60000).toISOString() : null;
  }
  /** Local HH:mm in the scenario's zone, delay included. */
  time(utc: string | null | undefined, delay = 0) {
    return clockTime(this.effective(utc, delay), this.scenario().inputs.timeZone);
  }
  /** Full local date for hover, since HH:mm alone hides a day change. */
  fullTime(utc: string | null | undefined, delay = 0) {
    return absoluteTimeLabel(this.effective(utc, delay), this.scenario().inputs.timeZone);
  }
  blocked() {
    return this.disabled() || this.loading() || this.busy();
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
          this.confirmDelete.set(false);
          this.message.set('Orarul a fost salvat. Următoarea simulare îl va folosi.');
        },
        error: (error) => this.report(error),
      });
  }
  private report(error: unknown) {
    this.message.set(error instanceof Error ? error.message : 'Orarul nu a putut fi actualizat.');
  }
}

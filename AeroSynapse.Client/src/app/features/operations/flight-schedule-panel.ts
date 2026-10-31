import { DatePipe } from '@angular/common';
import { Component, DestroyRef, effect, inject, input, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { finalize, Observable } from 'rxjs';
import { ApiClient } from '../../core/http/api-client';
import { FlightSchedule, ScenarioDetails, ScenarioFlight } from './operations.models';

@Component({
  selector: 'app-flight-schedule-panel',
  imports: [FormsModule, DatePipe],
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
  readonly statuses = [
    { value: 'scheduled', label: 'Programat' }, { value: 'delayed', label: 'Întârziat' },
    { value: 'boarding', label: 'Îmbarcare' }, { value: 'closed', label: 'Îmbarcare închisă' },
    { value: 'departed', label: 'Plecat' }, { value: 'cancelled', label: 'Anulat' },
  ];
  draft: { id: string; code: string; gateId: string; departure: number; open: number; close: number;
    capacity: number; delay: number; status: string } | null = null;
  private scenarioId = '';

  constructor() {
    effect(onCleanup => {
      const scenario = this.scenario();
      this.refresh();
      untracked(() => {
        if (this.scenarioId !== scenario.scenario.id) this.draft = null;
        this.scenarioId = scenario.scenario.id;
        this.schedule.set(null);
        this.loading.set(true);
        const subscription = this.api.get<FlightSchedule>(`scenarios/${scenario.scenario.id}/flight-schedule`)
          .pipe(finalize(() => this.loading.set(false)))
          .subscribe({ next: value => this.schedule.set(value), error: error => this.report(error) });
        onCleanup(() => subscription.unsubscribe());
      });
    });
  }

  reload() { if (!this.busy()) { this.message.set(''); this.draft = null; this.refresh.update(value => value + 1); } }
  edit(flight?: ScenarioFlight) {
    const schedule = this.schedule();
    if (!schedule || this.blocked()) return;
    const departure = flight ? this.minute(flight.departureAtUtc) : Math.min(60, schedule.durationMinutes);
    this.draft = {
      id: flight?.id ?? '', code: flight?.code ?? `DEMO-${schedule.flights.length + 1}`,
      gateId: flight?.gateId ?? schedule.gates[0]?.id ?? '', departure,
      open: flight?.boardingOpensAtUtc ? this.minute(flight.boardingOpensAtUtc) : Math.max(0, departure - 30),
      close: flight?.boardingClosesAtUtc ? this.minute(flight.boardingClosesAtUtc) : departure - Math.min(5, departure / 2),
      capacity: flight?.passengerCapacity ?? 120, delay: flight?.delayMinutes ?? 0, status: flight?.status ?? 'scheduled',
    };
    this.message.set('');
  }
  save() {
    const schedule = this.schedule();
    const draft = this.draft;
    if (!schedule || !draft || this.blocked()) return;
    if (!draft.code.trim() || !draft.gateId || !Number.isInteger(draft.capacity) || draft.capacity < 1
      || draft.capacity > 2147483647 || !Number.isInteger(draft.delay) || draft.delay < 0 || draft.delay > 10080
      || ![draft.open, draft.close, draft.departure].every(Number.isFinite)
      || draft.open >= draft.close || draft.close >= draft.departure || draft.departure < 0
      || draft.departure + draft.delay > schedule.durationMinutes) {
      this.message.set('Verifică numărul, poarta și capacitatea. Deschidere < închidere < plecare; plecarea cu întârziere trebuie să fie în intervalul scenariului.');
      return;
    }
    const date = (minute: number) => new Date(Date.parse(schedule.startsAtUtc) + minute * 60000);
    if (![draft.open, draft.close, draft.departure].every(minute => Number.isFinite(date(minute).getTime()))) {
      this.message.set('Momentele introduse depășesc intervalul de date acceptat.'); return;
    }
    const at = (minute: number) => date(minute).toISOString();
    const flight: ScenarioFlight = { id: draft.id || crypto.randomUUID(), code: draft.code.trim(), gateId: draft.gateId,
      departureAtUtc: at(draft.departure), boardingOpensAtUtc: at(draft.open), boardingClosesAtUtc: at(draft.close),
      passengerCapacity: draft.capacity, delayMinutes: draft.delay, status: draft.status, isSynthetic: true };
    const path = `scenarios/${schedule.scenarioId}/flights`;
    this.mutate(draft.id ? this.api.put<ScenarioDetails>(`${path}/${draft.id}`, { revision: schedule.revision, flight })
      : this.api.post<ScenarioDetails>(path, { revision: schedule.revision, flight }));
  }
  remove(flight: ScenarioFlight) {
    const schedule = this.schedule();
    if (!schedule || this.blocked()) return;
    this.mutate(this.api.delete<ScenarioDetails>(`scenarios/${schedule.scenarioId}/flights/${flight.id}?revision=${schedule.revision}`));
  }
  gateName(id: string) { return this.schedule()?.gates.find(gate => gate.id === id)?.code ?? id; }
  statusName(status?: string) { return this.statuses.find(item => item.value === status)?.label ?? 'Programat'; }
  effective(time: string | null | undefined, delay = 0) { return time ? new Date(Date.parse(time) + delay * 60000).toISOString() : null; }
  minute(time: string) { return (Date.parse(time) - Date.parse(this.schedule()!.startsAtUtc)) / 60000; }
  blocked() { return this.disabled() || this.loading() || this.busy(); }
  private mutate(request: Observable<ScenarioDetails>) {
    this.busy.set(true); this.working.emit(true); this.message.set('');
    request.pipe(finalize(() => { this.busy.set(false); this.working.emit(false); }), takeUntilDestroyed(this.destroy))
      .subscribe({ next: value => { this.draft = null; this.changed.emit(value); this.message.set('Orarul demo a fost salvat. Următoarea rulare va captura această revizie.'); },
        error: error => this.report(error) });
  }
  private report(error: unknown) { this.message.set(error instanceof Error ? error.message : 'Orarul nu a putut fi actualizat.'); }
}

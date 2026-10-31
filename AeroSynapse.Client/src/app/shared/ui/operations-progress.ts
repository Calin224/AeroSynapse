import { Component, input } from '@angular/core';
import { OperationsRun, OptimizationSummary } from '../../core/http/operations-stream';

@Component({
  selector: 'app-operations-progress',
  template: `
    <section class="live-progress" aria-live="polite">
      <p class="connection" [attr.data-state]="connection()"><span class="dot"></span>{{ connectionLabel() }}</p>
      @if (run(); as current) {
        @if (current.simulationMinute !== null) { <p class="clock">Minut simulat: <strong>{{ current.simulationMinute }}</strong></p> }
        @if (current.snapshot; as snapshot) {
          <div class="table-wrap"><table><caption>Starea zonelor</caption><thead><tr><th>Zonă</th><th>În așteptare</th><th>Procesați</th></tr></thead><tbody>
          @for (zone of snapshot.zones; track zone.zoneId) {
            <tr><td>{{ zoneName(zone.zoneId) }}</td><td>{{ zone.queuedPassengers ?? '—' }}</td><td>{{ zone.processedPassengers ?? '—' }}</td></tr>
          }</tbody></table></div>
          @if (snapshot.totalZones > snapshot.zones.length) { <p class="note">Sunt afișate {{ snapshot.zones.length }} din {{ snapshot.totalZones }} zone.</p> }
        }
      }
      @if (optimization()) { <p class="note">Optimizarea a fost finalizată.</p> }
    </section>
  `,
  styles: `
    .live-progress { display: grid; gap: 12px; padding: 4px 0; }
    p { margin: 0; font-size: 13px; color: var(--text-muted); }
    .connection { display: inline-flex; align-items: center; gap: 8px; font-size: 12px; }
    .dot { width: 8px; height: 8px; border-radius: 50%; background: var(--text-faint); }
    [data-state='live'] .dot { background: var(--live); box-shadow: none; }
    [data-state='connecting'] .dot, [data-state='reconnecting'] .dot, [data-state='polling'] .dot { background: var(--warning); }
    .clock strong { color: var(--text); font-variant-numeric: tabular-nums; }
  `,
})
export class OperationsProgress {
  readonly run = input<OperationsRun | null>(null);
  readonly connection = input('idle');
  readonly optimization = input<OptimizationSummary | null>(null);
  readonly zoneNames = input<Record<string, string>>({});
  zoneName(id: string) { return this.zoneNames()[id] ?? `Zonă ${id.slice(-6)}`; }
  connectionLabel() { return ({ idle: 'Selectează un scenariu pentru urmărire.', connecting: 'Conectare la actualizări…',
    live: 'Actualizări active', reconnecting: 'Se reiau actualizările…', polling: 'Se verifică actualizările…' } as Record<string, string>)[this.connection()] ?? ''; }
}

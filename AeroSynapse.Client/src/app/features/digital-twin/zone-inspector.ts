import { DecimalPipe } from '@angular/common';
import { Component, computed, input, output } from '@angular/core';
import { TwinScene, TwinSelection } from './twin-model';
import { CONGESTION_BANDS, HeatmapFrame } from './heatmap-model';

@Component({
  selector: 'app-zone-inspector',
  imports: [DecimalPipe],
  template: `
    <section aria-label="Informații despre zona selectată" aria-live="polite">
      <header>
        <strong>{{ selection().label }}</strong>
        <button type="button" aria-label="Închide informațiile zonei" (click)="dismiss.emit()">
          ×
        </button>
      </header>
      @if (selection().exterior; as exterior) {
        <p class="source">
          {{
            exterior.positionSource === 'scenario-demo'
              ? 'Poziție din scenariul local demo'
              : 'Poziție din macheta fictivă'
          }}
          · {{ exterior.level === 'detail' ? 'LOD detaliat' : 'LOD simplificat' }}
        </p>
        <dl>
          <div>
            <dt>Resursă</dt>
            <dd>{{ resourceNames[selection().resource] ?? selection().resource }}</dd>
          </div>
          <div>
            <dt>Cod stabil</dt>
            <dd>{{ exterior.code }}</dd>
          </div>
          <div>
            <dt>Poziție X / Y / Z</dt>
            <dd>{{ formatMeters(exterior.positionMeters) }} m</dd>
          </div>
          <div>
            <dt>Dimensiuni X / Y / Z</dt>
            <dd>{{ formatMeters(exterior.dimensionsMeters) }} m</dd>
          </div>
          @if (exterior.headingDegrees !== undefined) {
            <div>
              <dt>Orientare demo</dt>
              <dd>{{ exterior.headingDegrees.toFixed(0) }}°</dd>
            </div>
          }
          @if (exterior.gateId) {
            <div>
              <dt>Legătură fictivă</dt>
              <dd>{{ exterior.gateCode ?? 'Poartă demo' }}</dd>
            </div>
          }
        </dl>
      } @else if (heatmap(); as frame) {
        <p class="source" [attr.data-run-id]="frame.runId" [attr.data-minute]="frame.minute">
          Minutul {{ frame.minute }} · runId {{ frame.runId }}
        </p>
        @if (heatmapZone(); as zone) {
          <span class="state" [style.background]="bands[zone.band].color" style="color:#102635">{{
            bands[zone.band].label
          }}</span>
          <dl>
            <div>
              <dt>Pasageri în coadă</dt>
              <dd>{{ zone.queuedPassengers }}</dd>
            </div>
            <div>
              <dt>Capacitate efectivă</dt>
              <dd>{{ zone.capacityPerMinute ?? '—' }} pasageri/min</dd>
            </div>
            <div>
              <dt>Personal în run</dt>
              <dd>{{ zone.staffCount ?? '—' }}</dd>
            </div>
            <div>
              <dt>Procesați cumulat</dt>
              <dd>{{ zone.processedPassengers }}</dd>
            </div>
          </dl>
        } @else {
          <p>Fără snapshot pentru această zonă la minutul {{ frame.minute }}.</p>
        }
      } @else if (checkpoint(); as cp) {
        <p class="source">Configurație capturată · {{ scene().scenarioId ?? 'scenariu' }}</p>
        <span class="state" [class.closed]="cp.status === 'closed'">{{ states[cp.status] }}</span>
        <dl>
          <div>
            <dt>Capacitate</dt>
            <dd>{{ cp.capacityPerMinute }} pasageri/min</dd>
          </div>
          <div>
            <dt>Personal alocat</dt>
            <dd>{{ cp.staff.length }} operatori</dd>
          </div>
          <div>
            <dt>Pasageri în coadă</dt>
            <dd>{{ cp.metrics.queuedPassengers ?? '—' }}</dd>
          </div>
          <div>
            <dt>Pasageri procesați</dt>
            <dd>{{ cp.metrics.processedPassengers ?? '—' }}</dd>
          </div>
          <div>
            <dt>Așteptare medie</dt>
            <dd>
              {{
                cp.metrics.averageWaitMinutes === null
                  ? '—'
                  : (cp.metrics.averageWaitMinutes | number: '1.0-1') + ' min'
              }}
            </dd>
          </div>
        </dl>
        <ul aria-label="Personal alocat">
          @for (person of cp.staff; track person.id) {
            <li>{{ person.name }} · {{ person.role }}</li>
          }
        </ul>
        @if (!cp.staff.length) {
          <p>Niciun operator alocat.</p>
        }
      } @else if (selection().zoneType === 'checkpoint') {
        <p>Datele acestui checkpoint nu sunt disponibile în scenariul curent.</p>
      } @else {
        <p>Resursă: {{ types[selection().zoneType] }} · {{ selection().resource }}</p>
      }
      <details>
        <summary>Identificatori zonă</summary>
        <code>zoneId: {{ selection().zoneId }}</code
        ><code>entityId: {{ selection().entityId }}</code>
        @if (selection().exterior?.gateId) {
          <code>gateId: {{ selection().exterior?.gateId }}</code>
        }
      </details>
    </section>
  `,
  styles: `
    :host {
      display: block;
    }
    section {
      padding: 14px;
      color: var(--text);
      background: #ffffffef;
      backdrop-filter: blur(12px);
      box-shadow: none;
      border: 1px solid var(--border);
      border-radius: var(--radius);
      font-size: 12px;
    }
    header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 12px;
    }
    strong {
      font-size: 13px;
    }
    button {
      border: 0;
      color: var(--text-muted);
      background: var(--surface-raised);
      border-radius: var(--radius);
      width: 28px;
      height: 28px;
      cursor: pointer;
      font-size: 20px;
    }
    .source {
      color: var(--text-faint);
      overflow-wrap: anywhere;
      font-size: 10.5px;
      margin: 10px 0;
    }
    .state {
      display: inline-block;
      color: var(--success);
      background: var(--accent-soft);
      border-radius: var(--radius);
      padding: 4px 8px;
    }
    .state.closed {
      background: var(--warning-soft);
      color: var(--warning);
    }
    dl {
      margin: 10px 0;
      display: grid;
      gap: 7px;
    }
    dl div {
      display: flex;
      justify-content: space-between;
      gap: 16px;
    }
    dt {
      color: var(--text-muted);
    }
    dd {
      margin: 0;
      font-weight: 600;
    }
    ul {
      padding-left: 15px;
      font-size: 11px;
      color: var(--text-muted);
    }
    li {
      margin: 5px 0;
    }
    details {
      border-top: 1px solid var(--border);
      padding-top: 8px;
    }
    summary {
      cursor: pointer;
      color: var(--text-muted);
    }
    code {
      display: block;
      font-size: 11px;
      overflow-wrap: anywhere;
      margin-top: 6px;
    }
  `,
})
export class ZoneInspector {
  readonly selection = input.required<TwinSelection>();
  readonly scene = input.required<TwinScene>();
  readonly dismiss = output<void>();
  readonly heatmap = input<HeatmapFrame | null>(null);
  protected readonly heatmapZone = computed(() =>
    this.heatmap()?.zones.find((zone) => zone.zoneId === this.selection().zoneId),
  );
  protected readonly bands = CONGESTION_BANDS;
  protected readonly checkpoint = computed(() =>
    this.scene().checkpoints?.find((checkpoint) => checkpoint.zoneId === this.selection().zoneId),
  );
  protected readonly resourceNames: Partial<Record<string, string>> = {
    stands: 'Stand aeronave',
    apron: 'Platformă',
    aircraft: 'Aeronavă',
    baggage: 'Bagaje',
    vehicles: 'Vehicul de sol',
    runway: 'Pistă',
    infrastructure: 'Infrastructură',
  };
  protected formatMeters(values: number[]): string {
    return values.map((value) => value.toFixed(1)).join(' / ');
  }
  protected readonly states = { open: 'Deschis', busy: 'Solicitat', closed: 'Închis' };
  protected readonly types = {
    checkpoint: 'Control securitate',
    checkIn: 'Ghișee check-in',
    gate: 'Poartă',
    terminal: 'Terminal',
    scene: 'Context aeroportuar',
  };
}

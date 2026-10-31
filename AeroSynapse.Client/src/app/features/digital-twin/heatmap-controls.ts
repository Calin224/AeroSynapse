import { Component, computed, input, output } from '@angular/core';
import {
  comparable,
  CONGESTION_BANDS,
  HeatmapDataset,
  HeatmapFrame,
  heatmapMinutes,
} from './heatmap-model';

@Component({
  selector: 'app-heatmap-controls',
  host: { '[class.read-only]': 'readOnly()' },
  template: `
    <section
      aria-label="Aglomerație calculată"
      [attr.data-run-id]="frame().runId"
      [attr.data-minute]="frame().minute"
    >
      @if (!readOnly()) {
        <header>
          <strong>Aglomerație · minutul {{ frame().minute }}</strong>
          <div aria-label="Comparație pe același layout">
            <button
              type="button"
              [attr.aria-pressed]="frame().variant === 'baseline'"
              (click)="variantChange.emit('baseline')"
            >
              Baseline
            </button>
            <button
              type="button"
              [disabled]="!compatible()"
              [attr.aria-pressed]="frame().variant === 'optimized'"
              (click)="variantChange.emit('optimized')"
            >
              Optimizat
            </button>
          </div>
        </header>
        <details class="snapshot-tools">
          <summary>Snapshoturi & sursă</summary>
          <div class="time-control">
            <button
              type="button"
              aria-label="Snapshot precedent"
              [disabled]="index() <= 0"
              (click)="minuteChange.emit(minutes()[index() - 1])"
            >
              ‹
            </button>
            <label
              >Cursor de timp
              <input
                type="range"
                aria-label="Minutul simulării"
                min="0"
                [max]="minutes().length ? minutes().length - 1 : 0"
                step="1"
                [value]="index()"
                [disabled]="minutes().length < 2"
                [attr.aria-valuetext]="'Minutul ' + frame().minute"
                (input)="changeMinute($event)"
              />
            </label>
            <button
              type="button"
              aria-label="Snapshot următor"
              [disabled]="index() >= minutes().length - 1"
              (click)="minuteChange.emit(minutes()[index() + 1])"
            >
              ›
            </button>
            <button
              type="button"
              [attr.aria-pressed]="followingLatest()"
              (click)="followLatest.emit()"
            >
              Ultimul minut
            </button>
          </div>
          <p class="reference">
            Minutul {{ frame().minute }} ·
            {{ frame().source === 'live-preview' ? 'progres observat' : 'rezultat calculat' }}
          </p>
        </details>
        <dl class="kpis" aria-label="KPI pentru minutul selectat">
          <div>
            <dt>Total în cozi</dt>
            <dd data-testid="heatmap-queue-kpi">
              {{ frame().kpis.queuedPassengers ?? '—' }} <small>pasageri</small>
            </dd>
          </div>
          <div>
            <dt>Zone critice</dt>
            <dd>{{ frame().kpis.criticalZones ?? '—' }}</dd>
          </div>
          <div>
            <dt>Capacitate efectivă</dt>
            <dd>{{ frame().kpis.capacityPerMinute ?? '—' }} <small>pasageri/min</small></dd>
          </div>
        </dl>
        <ng-content />
      }
      <ul class="legend" aria-label="Legendă accesibilă a aglomerației">
        @for (band of bands; track band.label) {
          <li>
            <span aria-hidden="true" [style.background]="band.color"></span><b>{{ band.label }}</b>
          </li>
        }
      </ul>
      <details [open]="valuesOpen()">
        <summary>Valori pe zone</summary>
        <div
          class="values-scroll"
          tabindex="0"
          role="region"
          aria-label="Valori numerice heatmap · derulare orizontală"
        >
          <table>
            <caption>
              Minutul {{ frame().minute }}
            </caption>
            <thead>
              <tr>
                <th scope="col">Zonă</th>
                <th>Coadă (pas.)</th>
                <th>Capacitate (pas./min)</th>
                <th>Stare</th>
                <th>Coadă / capacitate (min)</th>
                <th>Procesați cumulat</th>
                <th>Minut</th>
              </tr>
            </thead>
            <tbody>
              @for (zone of frame().zones; track zone.zoneId) {
                <tr
                  [attr.data-zone-id]="zone.zoneId"
                  [attr.data-run-id]="zone.runId"
                  [attr.data-minute]="zone.elapsedMinutes"
                >
                  <th scope="row">{{ zone.label }}</th>
                  <td>{{ zone.queuedPassengers }}</td>
                  <td>{{ zone.capacityPerMinute ?? '—' }}</td>
                  <td>{{ states[zone.band].label }}</td>
                  <td>{{ zone.backlogMinutes === null ? '—' : zone.backlogMinutes.toFixed(2) }}</td>
                  <td>{{ zone.processedPassengers }}</td>
                  <td>{{ zone.elapsedMinutes }}</td>
                </tr>
              }
            </tbody>
            <tbody>
              @for (zone of frame().missingZones; track zone.zoneId) {
                <tr
                  [attr.data-zone-id]="zone.zoneId"
                  [attr.data-run-id]="frame().runId"
                  [attr.data-minute]="frame().minute"
                >
                  <th scope="row">{{ zone.label }}</th>
                  <td>—</td>
                  <td>{{ zone.capacityPerMinute ?? '—' }}</td>
                  <td>Fără date</td>
                  <td>—</td>
                  <td>—</td>
                  <td>{{ frame().minute }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      </details>
      @if (!frame().zones.length) {
        <p role="status">
          Nu există snapshoturi la acest minut pentru run-ul selectat. Nu se interpolează date.
        </p>
      }
      @if (frame().missingZones.length) {
        <p role="status">
          {{ frame().missingZones.length }} zone fără snapshot. KPI-urile incomplete sunt marcate cu
          —.
        </p>
      }
      @if (dataset().optimized && !compatible()) {
        <p role="status">Comparația cere două run-uri finalizate pe același layout.</p>
      }
    </section>
  `,
  styles: `
    :host {
      display: block;
    }
    :host(.read-only) section {
      padding: 8px 12px;
      gap: 6px 16px;
    }
    /* Docked under the twin stage: flat, the container provides the surface. */
    section {
      color: var(--text);
      padding: 16px 18px 18px;
      font-size: 12px;
      display: grid;
      grid-template-columns: 1fr auto;
      gap: 10px 24px;
    }
    header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 20px;
      grid-column: 1/-1;
    }
    header strong {
      font-size: 13.5px;
      font-weight: 600;
    }
    header div {
      display: flex;
      gap: 3px;
      padding: 3px;
      border: 1px solid var(--border);
      background: var(--surface-sunken);
      border-radius: var(--radius);
    }
    button {
      padding: 5px 12px;
      border: 1px solid transparent;
      border-radius: var(--radius);
      background: transparent;
      color: var(--text-muted);
      cursor: pointer;
      font: inherit;
      font-size: 12px;
    }
    button[aria-pressed='true'] {
      background: var(--accent-soft);
      color: var(--accent);
    }
    button:disabled {
      opacity: 0.4;
      cursor: not-allowed;
    }
    .time-control {
      display: flex;
      gap: 8px;
      align-items: center;
      flex-wrap: wrap;
      margin-top: 12px;
    }
    label {
      display: flex;
      gap: 8px;
      align-items: center;
      flex: 1;
    }
    input {
      flex: 1;
      min-width: 80px;
      accent-color: var(--accent);
    }
    .reference {
      font-size: 11px;
      color: var(--text-faint);
      overflow-wrap: anywhere;
    }
    .kpis {
      display: flex;
      gap: 30px;
      margin: 0;
      grid-row: 2;
      grid-column: 1/-1;
    }
    .kpis div {
      border-left: 1px solid var(--border);
      padding-left: 18px;
    }
    .kpis div:first-child {
      border: 0;
      padding-left: 0;
    }
    dt {
      color: var(--text-faint);
      font-family: var(--font-mono);
      font-size: 10.5px;
      letter-spacing: 0.06em;
      text-transform: uppercase;
    }
    dd {
      margin: 4px 0 0;
      font-family: var(--font-mono);
      font-size: 24px;
      font-weight: 500;
      letter-spacing: -0.04em;
      font-variant-numeric: tabular-nums;
    }
    small {
      font-size: 11px;
      color: var(--text-faint);
      font-weight: 400;
      letter-spacing: 0;
    }
    /* Compact legend: colour and state name. */
    .legend {
      list-style: none;
      margin: 0;
      padding: 0;
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 6px 14px;
      font-size: 11.5px;
    }
    .legend li {
      display: flex;
      align-items: center;
      gap: 5px;
    }
    .legend span {
      width: 8px;
      height: 8px;
      border-radius: 50%;
    }
    .legend b {
      font-weight: 500;
      color: var(--text-muted);
    }
    summary {
      cursor: pointer;
      color: var(--text-muted);
      font-size: 11.5px;
    }
    details {
      align-self: center;
    }
    .snapshot-tools {
      grid-column: 2;
      grid-row: 4;
    }
    .snapshot-tools[open] {
      grid-row: auto;
      grid-column: 1/-1;
    }
    /* Beside the legend while closed; the full width once the table opens. */
    details:not(.snapshot-tools) {
      grid-column: 2;
      justify-self: end;
    }
    details:not(.snapshot-tools)[open] {
      grid-column: 1/-1;
      justify-self: stretch;
    }
    section > p {
      grid-column: 1/-1;
      margin: 0;
    }
    details[open] {
      min-width: 0;
    }
    .values-scroll {
      overflow: auto;
      max-height: 240px;
      margin-top: 10px;
    }
    .values-scroll:focus-visible {
      outline-offset: -2px;
    }
    table {
      width: 100%;
      border-collapse: collapse;
      font-size: 11px;
      margin-top: 12px;
    }
    th,
    td {
      text-align: left;
      padding: 8px;
      border-bottom: 1px solid var(--border);
    }
    caption {
      color: var(--text-faint);
      text-align: left;
    }
    button:focus-visible,
    input:focus-visible,
    summary:focus-visible {
      outline: 2px solid var(--accent);
      outline-offset: 2px;
    }
    @container twin (max-width: 650px) {
      section {
        padding: 12px;
        gap: 10px;
        grid-template-columns: 1fr;
      }
      .kpis {
        gap: 12px;
      }
      .kpis div {
        padding-left: 10px;
      }
      dd {
        font-size: 22px;
      }
      dd small {
        display: block;
      }
      .snapshot-tools,
      details:not(.snapshot-tools) {
        grid-row: auto;
        grid-column: 1;
        justify-self: start;
      }
      table {
        min-width: 680px;
      }
    }
  `,
})
export class HeatmapControls {
  readonly readOnly = input(false);
  readonly valuesOpen = input(false);
  readonly dataset = input.required<HeatmapDataset>();
  readonly frame = input.required<HeatmapFrame>();
  readonly minuteChange = output<number>();
  readonly variantChange = output<HeatmapFrame['variant']>();
  readonly followingLatest = input(false);
  readonly followLatest = output<void>();
  protected readonly minutes = computed(() => heatmapMinutes(this.dataset()));
  protected readonly index = computed(() => this.minutes().indexOf(this.frame().minute));
  protected readonly compatible = computed(() => comparable(this.dataset()));
  protected readonly bands = Object.values(CONGESTION_BANDS);
  protected readonly states = CONGESTION_BANDS;
  protected changeMinute(event: Event): void {
    const minute = this.minutes()[Number((event.target as HTMLInputElement).value)];
    if (minute !== undefined) this.minuteChange.emit(minute);
  }
}

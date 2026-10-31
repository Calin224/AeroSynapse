import { DecimalPipe } from '@angular/common';
import { Component, computed, input, output } from '@angular/core';
import { RunOverview } from '../control-tower/run-overview';
import { busiestMoment, HeatmapZone } from '../digital-twin/heatmap-model';

/** Plain-language outcome of the latest run, with one-click entry points into the 3D replay. */
@Component({
  selector: 'app-run-summary',
  imports: [DecimalPipe],
  template: `
    @let totals = overview().totals;
    <section class="summary" [attr.data-tone]="tone()" aria-labelledby="run-summary-title">
      <div>
        <h3 id="run-summary-title">{{ headline() }}</h3>
        <ul>
          <li>
            <strong>{{ totals.processed }} din {{ totals.population }}</strong> pasageri au trecut
            de controlul de securitate.
          </li>
          @if (totals.meanWaitMinutes !== null) {
            <li>
              Un pasager a așteptat în medie
              <strong>{{ totals.meanWaitMinutes | number: '1.0-1' }} min</strong>.
            </li>
          }
          @if (totals.queuedAtEnd) {
            <li>
              <strong>{{ totals.queuedAtEnd }}</strong> pasageri erau încă la coadă când s-a
              încheiat simularea.
            </li>
          }
        </ul>
      </div>
      <div class="actions">
        <button type="button" class="btn-primary" (click)="playFromStart.emit()">
          <i class="pi pi-play" aria-hidden="true"></i> Redă simularea de la început
        </button>
        @if (peak(); as moment) {
          <button type="button" (click)="showBusiest.emit(moment)">
            Vezi momentul cel mai aglomerat · minutul {{ moment.elapsedMinutes }}
          </button>
        }
      </div>
    </section>
  `,
  styles: `
    :host {
      display: block;
      container-type: inline-size;
    }
    .summary {
      display: grid;
      grid-template-columns: minmax(0, 1fr) auto;
      gap: 12px 24px;
      padding: 16px 20px;
      border-left: 4px solid var(--tone);
      background: color-mix(in srgb, var(--tone) 9%, transparent);
      --tone: #41d3a2;
    }
    [data-tone='warning'] {
      --tone: #ffc45b;
    }
    [data-tone='critical'] {
      --tone: #fa665c;
    }
    h3 {
      margin: 4px 0 8px;
      font-size: 18px;
    }
    ul {
      margin: 0;
      padding-left: 18px;
      color: var(--text-muted);
      line-height: 1.6;
    }
    .actions {
      display: grid;
      align-content: start;
      gap: 8px;
    }
    @container (max-width: 760px) {
      .summary {
        grid-template-columns: minmax(0, 1fr);
      }
    }
  `,
})
export class RunSummary {
  readonly overview = input.required<RunOverview>();
  readonly playFromStart = output<void>();
  readonly showBusiest = output<HeatmapZone>();
  protected readonly peak = computed(() => {
    const dataset = this.overview().scene.heatmap;
    return dataset ? busiestMoment(dataset) : null;
  });
  protected readonly tone = computed(() => {
    const band = this.peak()?.band;
    return band === 'critical' || band === 'warning' ? band : 'low';
  });
  protected readonly headline = computed(() => {
    const peak = this.peak();
    if (!peak) return 'Nu s-a format nicio coadă: toți pasagerii au trecut direct.';
    const where = `${peak.queuedPassengers} oameni la ${peak.label}, în minutul ${peak.elapsedMinutes}`;
    if (peak.band === 'critical') return `Coadă mare: ${where}.`;
    if (peak.band === 'warning') return `Coadă moderată: ${where}.`;
    return `Flux fluent. Cea mai lungă coadă: ${where}.`;
  });
}

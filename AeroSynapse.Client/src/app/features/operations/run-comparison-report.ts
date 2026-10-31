import { DecimalPipe } from '@angular/common';
import { Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ComparedMetric, RunComparison } from './comparison.models';

/** Staffing advice concerns the security controls; check-in desks are reported but staffed in the scenario. */
const staffable = (row: ComparedMetric) => (row.zoneType ?? 'checkpoint') === 'checkpoint';

@Component({
  selector: 'app-run-comparison-report',
  imports: [DecimalPipe, RouterLink],
  template: `
    @let comparison = report();
    @if (simple()) {
      <section aria-label="Ce s-a schimbat">
        <h2>Ce s-a schimbat?</h2>
        <p>
          {{
            comparable()
              ? 'Rezultate comparabile.'
              : 'Date insuficiente pentru calcularea diferențelor dintre cele două simulări.'
          }}
        </p>
        @if (inactiveZones().length) {
          <div class="insight" role="status">
            <strong>Unde nu s-a observat activitate</strong>
            <p>
              La {{ inactiveZones().join(', ') }} nu au fost procesați pasageri și nu s-a
              înregistrat coadă în niciuna dintre simulări. O schimbare de personal în aceste puncte
              nu poate fi evaluată din aceste rezultate.
            </p>
            @if (activeZones().length) {
              <p>Au fost procesați pasageri la {{ activeZones().join(', ') }}.</p>
            }
          </div>
        }
        <div
          class="table-wrap summary-table"
          role="region"
          aria-label="Rezultatele comparației"
          tabindex="0"
        >
          <table>
            <caption>
              Rezultatele pentru fiecare zonă · așteptarea se referă la serviciile terminate
            </caption>
            <thead>
              <tr>
                <th>Indicator / zonă</th>
                <th>Personalul actual</th>
                <th>Varianta nouă</th>
                <th>Diferență</th>
              </tr>
            </thead>
            <tbody>
              @for (row of keyMetrics(); track $index) {
                <tr>
                  <th scope="row">
                    {{ metricLabel(row.name)
                    }}<small>{{ zoneNames()[row.zoneId] || 'Zonă fără nume' }}</small>
                  </th>
                  <td data-label="Personalul actual">
                    {{
                      comparable() && finite(row.baseline) ? (row.baseline | number: '1.0-1') : '—'
                    }}
                  </td>
                  <td data-label="Varianta nouă">
                    {{
                      comparable() && finite(row.candidate)
                        ? (row.candidate | number: '1.0-1')
                        : '—'
                    }}
                  </td>
                  <td data-label="Diferență">
                    {{ delta(row) === null ? 'Indisponibilă' : (delta(row) | number: '1.0-1') }}
                    {{
                      row.unit === 'minutes'
                        ? 'min'
                        : row.unit === 'passengers'
                          ? 'pasageri'
                          : row.unit
                    }}
                  </td>
                </tr>
              } @empty {
                <tr>
                  <td colspan="4">Nu există încă indicatori validați pentru rezumat.</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      </section>
    }
    @if (!simple()) {
      <details open>
        <summary>Raport detaliat și verificarea comparabilității</summary>
        <section aria-label="Rezultate baseline și what-if">
          <h3>Baseline vs what-if · rezultate persistate</h3>
          <p>
            <a
              routerLink="/operational-report"
              [queryParams]="{
                baselineRunId: comparison.baseline.id,
                candidateRunId: comparison.candidate.id,
              }"
              >Raport operațional · grafice și KPI înainte / după</a
            >
          </p>
          <p [attr.role]="comparable() ? 'status' : 'alert'">
            {{
              comparable()
                ? 'Pereche comparabilă pentru o comparație descriptivă.'
                : 'Rulări necomparabile sau date insuficiente. Diferențele și procentele nu sunt calculate.'
            }}
          </p>
          @for (issue of comparison.comparability?.issues ?? []; track $index) {
            <p role="alert">
              {{ issue.message }} <small>({{ issue.code }})</small>
            </p>
          }
          <p>
            Intrări: {{ match(comparison.inputsMatch) }} · Versiune și build:
            {{ match(comparison.versionsMatch) }} · Rezultate complete:
            {{ match(comparison.resultsMatch) }}
          </p>
          <p>
            Populație inițială: {{ match(comparison.comparability?.populationMatches ?? null) }}.
          </p>
          @if (
            comparison.inputsMatch === true &&
            comparison.versionsMatch === true &&
            comparison.resultsMatch === false
          ) {
            <p role="alert">
              Aceleași intrări și același build au produs rezultate diferite. Verifică diagnosticul
              rulărilor.
            </p>
          }
          <div class="table-wrap">
            <table>
              <caption>
                Identitatea și versiunea capturată a fiecărei rulări
              </caption>
              <thead>
                <tr>
                  <th>Rol / runId</th>
                  <th>Status / revizie scenariu</th>
                  <th>Motor / build</th>
                </tr>
              </thead>
              <tbody>
                @for (item of [comparison.baseline, comparison.candidate]; track $index) {
                  <tr>
                    <td>
                      <strong>{{ $index === 0 ? 'Baseline' : 'What-if / candidat' }}</strong
                      ><br /><code>{{ item.id }}</code>
                    </td>
                    <td>
                      {{ item.status }} / {{ item.scenarioRevision ?? 'necunoscută'
                      }}<br />Scenariu:
                      <code>{{ item.scenarioId ?? 'istoric' }}</code>
                    </td>
                    <td>
                      {{ item.provenance.engineVersion ?? 'necunoscut' }}<br /><code>{{
                        item.provenance.engineBuild ?? 'necunoscut'
                      }}</code>
                    </td>
                  </tr>
                  @if (item.failureReason) {
                    <tr>
                      <td colspan="3">{{ item.failureReason }}</td>
                    </tr>
                  }
                }
              </tbody>
            </table>
          </div>

          <div class="table-wrap">
            <table>
              <caption>
                Ultima valoare salvată pe metrică și zonă. Δ = candidat − baseline; Δ% = 100 × Δ /
                baseline, doar cu baseline pozitiv și observații aliniate.
              </caption>
              <thead>
                <tr>
                  <th>Metrică / zonă</th>
                  <th>
                    Baseline<br /><code>{{ comparison.baseline.id }}</code>
                  </th>
                  <th>
                    What-if / candidat<br /><code>{{ comparison.candidate.id }}</code>
                  </th>
                  <th>Δ</th>
                  <th>Δ relativ (%)</th>
                  <th>Unitate / limită</th>
                </tr>
              </thead>
              <tbody>
                @for (row of comparison.metrics; track $index) {
                  <tr>
                    <td>
                      {{ row.name }}<br />{{ row.zoneType ?? 'checkpoint' }} ·
                      <code>{{ row.zoneId }}</code>
                    </td>
                    <td>
                      {{
                        comparison.baseline.resultsValid &&
                        comparison.baseline.status === 'completed' &&
                        finite(row.baseline)
                          ? (row.baseline | number: '1.0-6')
                          : '—'
                      }}<small>{{ row.baselineRecordedAtUtc }}</small>
                    </td>
                    <td>
                      {{
                        comparison.candidate.resultsValid &&
                        comparison.candidate.status === 'completed' &&
                        finite(row.candidate)
                          ? (row.candidate | number: '1.0-6')
                          : '—'
                      }}<small>{{ row.candidateRecordedAtUtc }}</small>
                    </td>
                    <td>{{ delta(row) === null ? '—' : (delta(row) | number: '1.0-6') }}</td>
                    <td>
                      {{ percent(row) === null ? '—' : (percent(row) | number: '1.0-3') + '%' }}
                    </td>
                    <td>
                      {{ row.unit }}<small>{{ reason(row) }}</small>
                    </td>
                  </tr>
                } @empty {
                  <tr>
                    <td colspan="6">Nu există metrici finale validate.</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
          @if (comparison.metricsTruncated) {
            <p>
              Sunt afișate primele 100 de metrici finale. Amprentele compară rezultatul complet.
            </p>
          }
        </section>
      </details>
    }
  `,
  styles: `
    :host {
      display: block;
      min-width: 0;
    }
    .table-wrap {
      overflow-x: auto;
    }
    table {
      width: 100%;
      border-collapse: collapse;
    }
    th,
    td {
      padding: 0.65rem;
      text-align: left;
      vertical-align: top;
      border-bottom: 1px solid var(--border-strong);
    }
    caption {
      text-align: left;
      margin: 1rem 0;
    }
    code {
      font-size: 0.75rem;
      overflow-wrap: anywhere;
    }
    summary {
      cursor: pointer;
      padding: 14px 0;
      min-height: 44px;
      color: var(--text-muted);
    }
    small {
      display: block;
      color: var(--text-muted);
      font-size: 0.75rem;
      margin-top: 0.3rem;
    }
    @media (max-width: 650px) {
      .summary-table table,
      .summary-table tbody,
      .summary-table tr,
      .summary-table th,
      .summary-table td {
        display: block;
      }
      .summary-table caption {
        display: block;
        width: 100%;
      }
      .summary-table th {
        font-family: var(--font-sans);
        letter-spacing: 0;
        text-transform: none;
      }
      .summary-table thead {
        position: absolute;
        width: 1px;
        height: 1px;
        overflow: hidden;
        clip-path: inset(50%);
      }
      .summary-table tr {
        padding: 12px 0;
        border-bottom: 1px solid var(--border-strong);
      }
      .summary-table th,
      .summary-table td {
        border: 0;
        padding: 4px 0;
      }
      .summary-table td {
        display: flex;
        justify-content: space-between;
        gap: 16px;
      }
      .summary-table td::before {
        content: attr(data-label);
        color: var(--text-muted);
      }
    }
    .insight {
      margin: 1rem 0;
      padding: 1rem;
      background: var(--surface-sunken);
      border-radius: var(--radius);
    }
    .insight p {
      margin: 0.5rem 0 0;
    }
  `,
})
export class RunComparisonReport {
  readonly report = input.required<RunComparison>();
  readonly simple = input(false);
  readonly zoneNames = input<Record<string, string>>({});
  metricLabel(name: string) {
    return (
      (
        {
          meanWaitMinutes: 'Așteptare medie',
          maximumQueue: 'Coada maximă',
          processedPassengers: 'Pasageri procesați',
          waitSampleCount: 'Servicii incluse în medie',
        } as Record<string, string>
      )[name] ?? name
    );
  }
  keyMetrics() {
    return this.report().metrics.filter((row) =>
      ['meanWaitMinutes', 'maximumQueue', 'processedPassengers', 'waitSampleCount'].includes(
        row.name,
      ),
    );
  }
  inactiveZones() {
    if (!this.comparable()) return [];
    const metrics = this.report().metrics;
    return metrics
      .filter(
        (row) =>
          staffable(row) &&
          row.name === 'processedPassengers' &&
          row.baseline === 0 &&
          row.candidate === 0 &&
          this.delta(row) === 0,
      )
      .filter((row) =>
        metrics.some(
          (queue) =>
            queue.zoneId === row.zoneId &&
            queue.name === 'maximumQueue' &&
            queue.baseline === 0 &&
            queue.candidate === 0 &&
            this.delta(queue) === 0,
        ),
      )
      .map((row) => this.zoneNames()[row.zoneId] || 'Zonă fără nume');
  }
  activeZones() {
    if (!this.comparable()) return [];
    return this.report()
      .metrics.filter(
        (row) =>
          staffable(row) &&
          row.name === 'processedPassengers' &&
          this.delta(row) !== null &&
          ((row.baseline ?? 0) > 0 || (row.candidate ?? 0) > 0),
      )
      .map((row) => this.zoneNames()[row.zoneId] || 'Zonă fără nume');
  }
  comparable() {
    const value = this.report();
    return (
      value.comparability?.comparable === true &&
      value.comparability.populationMatches === true &&
      !value.comparability.issues.some((issue) => issue.severity === 'blocking') &&
      value.baseline.id !== value.candidate.id &&
      value.baseline.resultsValid &&
      value.candidate.resultsValid &&
      value.baseline.status === 'completed' &&
      value.candidate.status === 'completed' &&
      value.versionsMatch === true
    );
  }
  finite(value: number | null | undefined) {
    return typeof value === 'number' && Number.isFinite(value);
  }
  delta(row: ComparedMetric): number | null {
    return this.comparable() &&
      this.finite(row.baseline) &&
      this.finite(row.candidate) &&
      this.finite(row.delta) &&
      row.baselineRecordedAtUtc === row.candidateRecordedAtUtc &&
      (!row.unavailableReason ||
        ['baseline_not_positive', 'percentage_out_of_range'].includes(row.unavailableReason))
      ? row.delta!
      : null;
  }
  percent(row: ComparedMetric): number | null {
    return this.delta(row) !== null &&
      !row.unavailableReason &&
      row.baseline! > 0 &&
      this.finite(row.percentChange)
      ? row.percentChange!
      : null;
  }
  reason(row: ComparedMetric): string {
    if (!this.comparable()) return 'Comparație indisponibilă';
    const reasons: Record<string, string> = {
      baseline_not_positive: 'Procent indisponibil: baseline zero sau negativ',
      metric_unavailable: 'Lipsește o valoare validă',
      observation_mismatch: 'Momente de măsurare diferite',
      difference_out_of_range: 'Diferența depășește domeniul numeric',
      percentage_out_of_range: 'Procentul depășește domeniul numeric',
      runs_not_comparable: 'Rulări necomparabile',
    };
    if (row.unavailableReason) return reasons[row.unavailableReason] ?? row.unavailableReason;
    if (this.percent(row) === null) return 'Procent indisponibil';
    return '';
  }
  match(value: boolean | null) {
    return value === null ? 'date insuficiente' : value ? 'identice' : 'diferite';
  }
}

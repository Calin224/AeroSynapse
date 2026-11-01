import { towerIncidents } from '../control-tower/tower.models';
import type { PreparedReplay } from '../replay/replay.models';
import { checkpointResults, queueChart, totals, verdict } from './report.models';

const decimal = new Intl.NumberFormat('ro-RO', {
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
});
const integer = new Intl.NumberFormat('ro-RO', { maximumFractionDigits: 0 });
const fixed = (value: number | null | undefined, unit = '') =>
  value == null ? 'Fără date' : `${decimal.format(value)}${unit}`;
const count = (value: number) => integer.format(value);
const escape = (value: unknown) =>
  String(value ?? '').replace(
    /[&<>"']/g,
    (char) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[char]!,
  );
const paragraph = (text: string) => `<p>${escape(text)}</p>`;
const table = (headers: string[], rows: string[][], attributes: string[] = []) =>
  `<table><thead><tr>${headers.map((head) => `<th scope="col">${escape(head)}</th>`).join('')}</tr></thead>
  <tbody>${rows
    .map(
      (row, index) =>
        `<tr ${attributes[index] ?? ''}>${row
          .map((cell, i) =>
            i ? `<td>${escape(cell)}</td>` : `<th scope="row">${escape(cell)}</th>`,
          )
          .join('')}</tr>`,
    )
    .join('')}</tbody></table>`;

/** Self-contained print document: only verified captured data, no requests or external assets. */
export function reportDocument(
  replay: PreparedReplay,
  other: PreparedReplay | null,
  comparisonNote = '',
  generatedAt = new Date(),
): string {
  const { run, captured } = replay.data;
  const { scenario, inputs } = captured.scenario;
  const layout = captured.layout.layout;
  const results = checkpointResults(replay);
  const total = totals(results);
  const otherTotal = other ? totals(checkpointResults(other)) : null;
  const chart = queueChart(replay, other);
  const date = (value: string | number) => {
    try {
      return new Intl.DateTimeFormat('ro-RO', {
        dateStyle: 'medium',
        timeStyle: 'short',
        timeZone: inputs.timeZone,
      }).format(new Date(value));
    } catch {
      return new Date(value).toISOString();
    }
  };
  const at = (minute: number) => date(Date.parse(inputs.startsAtUtc) + minute * 60000);
  const cpName = (id: string) => results.find((cp) => cp.id === id)?.label ?? id;
  const gateName = (id: string) => layout.gates.find((gate) => gate.id === id)?.code ?? id;
  const flightName = (id: string) => inputs.flights.find((flight) => flight.id === id)?.code ?? id;
  const state = verdict(total);
  const indicators = total
    ? [
        [
          'Au trecut de control',
          `${count(total.processed)} din ${count(total.passengers)}`,
          'Pasageri procesați până la finalul simulării.',
        ],
        [
          'Finalizare control',
          fixed(total.passengers ? (total.processed / total.passengers) * 100 : null, '%'),
          'Proporția pasagerilor care au terminat controlul.',
        ],
        [
          'Așteptare medie',
          fixed(total.meanWait, ' min'),
          'Media ponderată cu numărul probelor de așteptare înregistrate.',
        ],
        [
          'Cel mai mare P95',
          fixed(total.worst?.p95, ' min'),
          total.worst
            ? `La ${total.worst.label}. Este P95-ul unui control, nu P95-ul global.`
            : 'Lipsesc probele de așteptare.',
        ],
        [
          'Au ratat îmbarcarea',
          total.lateOf ? `${count(total.late)} din ${count(total.lateOf)}` : 'Fără date',
          'Numai pasagerii cu termen de îmbarcare înregistrat.',
        ],
        [
          'Ocuparea maximă a personalului',
          fixed(total.busiest === null ? null : total.busiest * 100, '%'),
          'Cea mai mare ocupare medie cumulată dintre controale.',
        ],
      ]
    : [];
  const queueSvg = `<svg viewBox="0 0 740 240" role="img" aria-label="Evoluția cozilor">
    ${chart.grid.map((line) => `<line x1="50" y1="${line.y}" x2="690" y2="${line.y}" stroke="#d9dce0"/><text x="44" y="${line.y + 4}" text-anchor="end">${escape(line.label)}</text>`).join('')}
    ${chart.ticks.map((tick) => `<text x="${tick.x}" y="224" text-anchor="middle">${escape(tick.label)}</text>`).join('')}
    ${chart.columns.map((column) => `${column.value === null ? `<rect x="${column.band.x}" y="30" width="${column.band.width}" height="170" fill="#e4e6e9"/>` : ''}${[column.mine, column.theirs].map((bar, i) => (bar ? `<rect x="${bar.x}" y="${bar.y}" width="${bar.width}" height="${bar.height}" fill="${i ? '#7b8795' : '#b60000'}"/>` : '')).join('')}`).join('')}
    <line x1="50" y1="200" x2="690" y2="200" stroke="#343b45"/>
    </svg>`;
  const metrics = [
    {
      label: 'Pasageri procesați',
      now: total?.processed,
      then: otherTotal?.processed,
      unit: '',
      show: count,
    },
    {
      label: 'Așteptare medie',
      now: total?.meanWait,
      then: otherTotal?.meanWait,
      unit: ' min',
      show: (n: number) => fixed(n, ' min'),
    },
    {
      label: 'Cel mai mare P95 dintre controale',
      now: total?.worst?.p95,
      then: otherTotal?.worst?.p95,
      unit: ' min',
      show: (n: number) => fixed(n, ' min'),
    },
    {
      label: 'Au ratat îmbarcarea',
      now: total?.lateOf ? total.late : null,
      then: otherTotal?.lateOf ? otherTotal.late : null,
      unit: '',
      show: count,
    },
  ];
  const comparison = other
    ? `<section data-comparison>
    <h2>4. Comparația rulărilor</h2>
    ${paragraph(`Rularea aleasă: ${run.id}. Rularea de comparat: ${other.data.run.id} (revizia ${other.data.run.scenarioRevision}).`)}
    ${paragraph('Condițiile de comparabilitate au fost confirmate de server. Diferențele de mai jos sunt rularea aleasă minus rularea de comparat; nu reprezintă automat efectul unei optimizări.')}
    ${table(
      ['Indicator', 'Rularea aleasă', 'De comparat', 'Diferență', 'Diferență (%)'],
      metrics.map((metric) => {
        const { now, then, show, unit } = metric;
        const delta = now == null || then == null ? null : now - then;
        return [
          metric.label,
          now == null ? 'Fără date' : show(now),
          then == null ? 'Fără date' : show(then),
          fixed(delta, unit),
          then == null || then === 0 || delta === null
            ? 'Nedefinită'
            : fixed((delta / then) * 100, '%'),
        ];
      }),
    )}
    ${paragraph(`Amprenta replay-ului comparat: ${other.envelope.contentHash}`)}
  </section>`
    : `<section><h2>4. Comparația rulărilor</h2>${paragraph(comparisonNote || 'Nu a fost selectată o a doua rulare. Raportul descrie doar rularea aleasă.')}</section>`;
  const shifts = inputs.staffShifts;
  const staffRows = shifts
    ? shifts.shifts.flatMap((shift) =>
        shift.segments.map((segment) => [
          cpName(segment.checkpointId),
          shift.staffMemberId,
          `${at(segment.startMinute)} - ${at(segment.endMinute)}`,
        ]),
      )
    : layout.checkpoints.map((cp) => [
        cpName(cp.id),
        count(inputs.staffAllocations.filter((staff) => staff.checkpointId === cp.id).length),
        fixed(cp.capacityPerMinute, ' pax/min/operator'),
      ]);
  const eventRows = towerIncidents(captured, inputs.durationMinutes).map((event) => {
    const source = inputs.events.find((item) => item.id === event.id)!;
    return [
      at(event.minute),
      event.label,
      [event.target, source.flightId ? flightName(source.flightId) : '']
        .filter(Boolean)
        .join(' / '),
      event.detail || 'Nespecificate',
    ];
  });
  const conclusion = !total
    ? 'Lipsesc rezultatele de final pentru cel puțin un punct de control. Totalurile nu pot fi calculate complet.'
    : `Din ${count(total.passengers)} pasageri, ${count(total.processed)} au trecut de control, iar ${count(total.passengers - total.processed)} nu au terminat controlul până la final.`;
  return `<!doctype html><html lang="ro"><head><meta charset="utf-8"><title>Raport operațional - ${escape(scenario.name)} - ${escape(run.id.slice(0, 8))}</title>
    <style>${PRINT_STYLES}</style></head><body>
    <header><p class="brand">AeroSynapse AI / Operațiuni</p><h1>Raport operațional</h1>
      <p class="subtitle">${escape(scenario.name)}</p>
      <p>${escape(`Generat la ${date(generatedAt.toISOString())} (${inputs.timeZone}). ${run.isSynthetic ? 'Date sintetice' : 'Date ale simulării'}.`)}</p></header>
    <nav aria-label="Cuprins"><strong>Cuprins</strong><p>1. Rezumat și indicatori / 2. Controale și cozi / 3. Configurația simulată / 4. Comparație / 5. Metodologie și surse</p></nav>
    <section><h2>1. Rezumat și indicatori</h2><div class="callout"><h3>${escape(state.headline)}</h3>${paragraph(conclusion)}</div>
      ${table(
        ['Context', 'Valoare'],
        [
          ['Scenariu', `${scenario.code} / revizia ${run.scenarioRevision}`],
          ['Interval simulat', `${at(0)} - ${at(inputs.durationMinutes)} (${inputs.timeZone})`],
          ['Durată', fixed(inputs.durationMinutes, ' min')],
          [
            'Arie analizată',
            `${count(results.length)} puncte de control / ${count(inputs.flights.length)} zboruri`,
          ],
        ],
      )}
      ${indicators.length ? table(['Indicator', 'Rezultat', 'Cum se citește'], indicators) : paragraph('Fără totaluri complete. Consultă rezultatele disponibile pe puncte de control.')}
    </section>
    <section><h2>2. Controale și evoluția cozilor</h2><h3>Rezultate la finalul simulării</h3>
      ${table(
        [
          'Punct de control',
          'Procesați / total',
          'Medie (min)',
          'P95 (min)',
          'Coadă maximă (pax)',
          'Personal ocupat',
        ],
        results.map((row) => {
          const kpi = row.kpi;
          return kpi
            ? [
                row.label,
                `${count(kpi.processedPassengers)} / ${count(kpi.populationCount)}`,
                fixed(kpi.meanWaitMinutes),
                fixed(kpi.p95WaitMinutes),
                count(kpi.maximumQueue),
                fixed(
                  kpi.cumulativeUtilization === null ? null : kpi.cumulativeUtilization * 100,
                  '%',
                ),
              ]
            : [row.label, ...Array<string>(5).fill('Fără date')];
        }),
        results.map((row) => `data-checkpoint="${escape(row.id)}"`),
      )}
      <h3>Starea pasagerilor la încheiere</h3>
      ${table(
        [
          'Punct de control',
          'În coadă',
          'În procesare',
          'Nesosiți',
          'Fără traseu',
          'Îmbarcare ratată / evaluați',
          'Probe așteptare',
        ],
        results.map(({ label, kpi }) =>
          kpi
            ? [
                label,
                count(kpi.queuedPassengers),
                count(kpi.processingPassengers),
                count(kpi.notArrivedPassengers),
                count(kpi.unreachablePassengers),
                kpi.boardingRiskPopulationCount
                  ? `${count(kpi.boardingAtRiskCount)} / ${count(kpi.boardingRiskPopulationCount)}`
                  : 'Fără date',
                count(kpi.waitSampleCount),
              ]
            : [label, ...Array<string>(6).fill('Fără date')],
        ),
      )}
      <div class="chart"><h3>Coada totală în timp</h3>${queueSvg}
      ${paragraph(`Fiecare coloană reprezintă maximul cozii totale dintr-un interval de ${fixed(chart.slot, ' min')}. Roșu: rularea aleasă.${other ? ' Gri închis: rularea de comparat.' : ''} Fundal gri deschis: captură lipsă.`)}
      ${paragraph(chart.peak ? `Cel mai mare total observat: ${count(chart.peak.value)} pasageri la ${chart.peak.time}. ${chart.missing} capturi lipsă.` : `Fără date despre coada totală. ${chart.missing} capturi lipsă.`)}</div>
      <h3>Evoluția pe intervale</h3>
      ${table(
        [
          'Ora locală',
          'Minute de la start',
          'Maxim observat (pax)',
          ...(other ? ['De comparat (pax)'] : []),
        ],
        chart.columns.map((row, index) => [
          row.label,
          `${row.from} - ${chart.columns[index + 1]?.from ?? inputs.durationMinutes}`,
          row.value === null ? 'Fără date' : count(row.value),
          ...(other ? [row.other === null ? 'Fără date' : count(row.other)] : []),
        ]),
        chart.columns.map(() => 'data-queue-interval'),
      )}
    </section>
    <section><h2>3. Configurația simulată</h2>
      ${paragraph('Configurația de mai jos provine din captura rulării. Modificările ulterioare ale scenariului nu sunt incluse. Alocările nominale nu descriu mutările dinamice efectuate în timpul simulării.')}
      <h3>Personal ${shifts ? 'pe ture' : 'alocat inițial'}</h3>
      ${staffRows.length ? table(shifts ? ['Punct de control', 'Identificator operator', 'Interval local'] : ['Punct de control', 'Operatori', 'Capacitate nominală'], staffRows) : paragraph('Nu există alocări de personal în captura rulării.')}
      ${shifts ? paragraph(`Pasul planului: ${fixed(shifts.slotMinutes, ' min')}. Timp de transfer configurat: ${fixed(shifts.transferMinutes, ' min')}.`) : ''}
      <h3>Programul zborurilor capturat</h3>
      ${
        inputs.flights.length
          ? table(
              ['Zbor', 'Poartă', 'Plecare programată', 'Capacitate', 'Întârziere (min)', 'Stare'],
              inputs.flights.map((flight) => [
                flight.code,
                gateName(flight.gateId),
                date(flight.departureAtUtc),
                count(flight.passengerCapacity),
                fixed(flight.delayMinutes ?? 0),
                (
                  {
                    scheduled: 'Programat',
                    delayed: 'Întârziat',
                    boarding: 'Îmbarcare',
                    closed: 'Îmbarcare închisă',
                    departed: 'Plecat',
                    cancelled: 'Anulat',
                  } as Record<string, string>
                )[flight.status ?? ''] ??
                  flight.status ??
                  'Nespecificată',
              ]),
            )
          : paragraph('Nu există zboruri în captura rulării.')
      }
      <h3>Evenimente programate</h3>
      ${eventRows.length ? table(['Ora locală', 'Eveniment', 'Țintă', 'Detalii configurate'], eventRows) : paragraph('Scenariul nu conține evenimente programate.')}
    </section>
    ${comparison}
    <section><h2>5. Metodologie și surse</h2>
      <ul><li>Timpii și procentele se afișează cu două zecimale. Calculele folosesc valorile originale, înainte de rotunjire. Numărul de pasageri rămâne întreg.</li>
      <li>Așteptarea medie este ponderată cu numărul probelor de la controale. P95 înseamnă că 95% din probele înregistrate la acel control au o așteptare cel mult egală cu valoarea afișată. P95-urile controalelor nu se adună.</li>
      <li>Cozile sunt observate la momentele capturate. Maximele simultane observate pot diferi de suma maximelor individuale ale controalelor.</li>
      <li>„Fără date” înseamnă că informația lipsește; nu înseamnă zero. Fără termene de îmbarcare nu se poate evalua îmbarcarea ratată.</li>
      <li>Concluzia de pe prima pagină folosește regulile de interpretare ale aplicației. Raportul descrie o simulare și nu certifică performanța unui aeroport real.</li></ul>
      ${table(
        ['Referință', 'Valoare'],
        [
          ['Rulare', run.id],
          ['Scenariu', run.scenarioId],
          ['Revizie capturată', String(run.scenarioRevision)],
          ['Seed', String(run.seed)],
          ['Captură', date(captured.capturedAtUtc)],
          ['Interval capturi', fixed(replay.data.intervalMinutes, ' min')],
          ['Motor', run.provenance.engineVersion ?? captured.engine ?? 'Fără date'],
          ['Build motor', run.provenance.engineBuild ?? 'Fără date'],
          ['Amprentă intrări', run.provenance.inputHash ?? 'Fără date'],
          ['Amprentă rezultate', run.provenance.resultHash ?? 'Fără date'],
          ['Amprentă replay SHA-256', replay.envelope.contentHash],
        ],
      )}
    </section></body></html>`;
}

/** Use the browser's PDF printer, keeping report data on the user's device. */
export function printReport(html: string): Promise<void> {
  return new Promise((resolve, reject) => {
    const frame = document.createElement('iframe');
    frame.title = 'Raport operațional pentru export PDF';
    frame.setAttribute('aria-hidden', 'true');
    frame.style.cssText = 'position:fixed;left:-10000px;top:0;width:210mm;height:297mm;border:0';
    frame.onload = () => {
      const target = frame.contentWindow;
      if (!target) {
        frame.remove();
        reject(new Error('Print window unavailable.'));
        return;
      }
      target.addEventListener('afterprint', () => frame.remove(), { once: true });
      try {
        target.focus();
        target.print();
        resolve();
      } catch (error) {
        frame.remove();
        reject(error);
      }
    };
    frame.srcdoc = html;
    document.body.append(frame);
  });
}

const PRINT_STYLES = `
@page { size: A4; margin: 18mm 14mm 18mm;
  @top-right { content: 'AeroSynapse AI / Raport operațional'; font: 8pt Arial; color: #565e68; }
  @bottom-left { content: 'Rezultate din simulare'; font: 8pt Arial; color: #565e68; }
  @bottom-right { content: 'Pagina ' counter(page) ' / ' counter(pages); font: 8pt Arial; color: #565e68; }
}
* { box-sizing: border-box; }
html { color: #20252b; background: white; font: 10pt/1.4 Arial, sans-serif; }
body { margin: 0; }
header { border-top: 5px solid #b60000; padding-top: 12pt; }
.brand { color: #9b0000; font-weight: bold; font-size: 9pt; letter-spacing: .06em; }
h1 { font-size: 25pt; line-height: 1.15; margin: 10pt 0; }
.subtitle { font-size: 16pt; font-weight: bold; }
h2 { font-size: 17pt; border-bottom: 1px solid #a6acb4; padding-bottom: 5pt; margin: 20pt 0 10pt; }
h3 { font-size: 11pt; margin: 14pt 0 7pt; }
p { margin: 6pt 0; }
nav, .callout { background: #f2f3f5; border-left: 3px solid #b60000; padding: 8pt 10pt; margin: 10pt 0; }
.callout h3 { margin-top: 0; }
table { width: 100%; border-collapse: collapse; table-layout: fixed; font-size: 8.5pt; margin: 10pt 0 16pt; }
th, td { padding: 5pt 6pt; border-bottom: 1px solid #d8dce1; vertical-align: top; text-align: left; overflow-wrap: anywhere; font-variant-numeric: tabular-nums; }
thead th { background: #e9ecf0; color: #252b33; font-weight: bold; }
tbody th { font-weight: normal; }
tbody tr:nth-child(even) { background: #f7f8fa; }
thead { display: table-header-group; }
tr, nav, .callout, .chart { break-inside: avoid; }
h2, h3 { break-after: avoid; }
p, li { orphans: 3; widows: 3; }
li { margin-bottom: 5pt; }
svg { display: block; width: 100%; height: auto; }
svg text { font: 11px Arial; fill: #343b45; }
@media print {  * { print-color-adjust: exact; -webkit-print-color-adjust: exact; } }
`;

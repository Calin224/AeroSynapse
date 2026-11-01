import { ids, replayData } from '../replay/replay.testing';
import { candidateData, comparisonFixture, prepared, bottleneckLayout } from './report.testing';
import {
  ReportRun,
  checkpointResults,
  comparisonProblem,
  kpiCards,
  people,
  queueChart,
  queueSeries,
  summary,
  totals,
  verdict,
} from './report.models';
import { makeDraft, basicErrors } from '../scenario-studio/studio.models';

describe('Operational report figures and wording', () => {
  beforeEach(async () =>
    vi.stubGlobal(
      'crypto',
      (await vi.importActual<{ webcrypto: Crypto }>('node:crypto')).webcrypto,
    ),
  );
  afterEach(() => vi.unstubAllGlobals());
  const reportOf = async (data = replayData()) => {
    const replay = await prepared(data),
      results = checkpointResults(replay);
    return { replay, results, totals: totals(results) };
  };

  it.each([
    [1, '1 pasager'],
    [5, '5 pasageri'],
    [19, '19 pasageri'],
    [20, '20 de pasageri'],
    [101, '101 pasageri'],
    [120, '120 de pasageri'],
    [1200, '1.200 de pasageri'],
  ])('writes %s as "%s"', (count, text) => expect(people(count)).toBe(text));

  it('pools every checkpoint at the end of the run and summarises it in plain words', async () => {
    const { totals: run } = await reportOf();
    expect(run).toEqual({
      passengers: 20,
      processed: 12,
      meanWait: 1.25,
      worst: { label: 'CP1 · Security', p95: 2 },
      late: 0,
      lateOf: 0,
      busiest: 0.5,
    });
    expect(summary(run, 2)).toEqual([
      'Din 20 de pasageri, 12 au trecut de control (60%); nu avem date despre întârzieri la îmbarcare.',
      'Așteptarea medie la control a fost de 1,3 min.',
      'Cel mai aglomerat punct de control a fost CP1 · Security: acolo, 95% dintre pasageri au așteptat cel mult 2 min.',
    ]);
  });

  it('weights the mean by finished passengers and names late boarders and the worst checkpoint', async () => {
    const data = replayData();
    for (const row of data.samples.filter((sample) => sample.zoneId === ids.otherCp)) {
      row.kpi!.waitSampleCount = row.kpi!.processedPassengers;
      row.kpi!.meanWaitMinutes = row.minute ? 4.25 : null;
      row.kpi!.p95WaitMinutes = row.minute ? 9 : null;
      row.kpi!.boardingRiskPopulationCount = 10;
      row.kpi!.boardingAtRiskCount = 3;
      row.kpi!.boardingLateRisk = 0.3;
    }
    const { totals: run } = await reportOf(data);
    expect(run).toMatchObject({
      meanWait: 2.75,
      worst: { label: 'CP2 · Border', p95: 9 },
      late: 3,
      lateOf: 10,
    });
    expect(summary(run, 2)).toEqual([
      'Din 20 de pasageri, 12 au trecut de control (60%) și 3 au ratat îmbarcarea.',
      'Așteptarea medie la control a fost de 2,8 min.',
      'Cel mai aglomerat punct de control a fost CP2 · Border: acolo, 95% dintre pasageri au așteptat cel mult 9 min.',
    ]);
    expect(kpiCards(run!).map((card) => card.value)).toEqual([
      '12 din 20',
      '2,8 min',
      '9 min',
      '3 din 10',
    ]);
  });

  it('drops the checkpoint name with a single checkpoint and says when nobody was late', () => {
    const run = {
      passengers: 5,
      processed: 5,
      meanWait: 0.5,
      worst: { label: 'CP1 · Security', p95: 1 },
      late: 0,
      lateOf: 5,
      busiest: null,
    };
    expect(summary(run, 1)).toEqual([
      'Din 5 pasageri, 5 au trecut de control (100%) și niciunul nu a ratat îmbarcarea.',
      'Așteptarea medie la control a fost de 30 s.',
      '95% dintre pasageri au așteptat cel mult 1 min.',
    ]);
  });

  it('leads with one headline whose tone matches how the run went', () => {
    const run = {
      passengers: 100,
      processed: 100,
      meanWait: 0.004,
      worst: { label: 'CP1 · Security', p95: 0.1 },
      late: 0,
      lateOf: 100,
      busiest: null,
    };
    expect(verdict(run)).toEqual({
      tone: 'good',
      headline: 'Operațiune fluentă: toți pasagerii au trecut de control la timp.',
    });
    expect(verdict({ ...run, late: 3 })).toEqual({
      tone: 'warning',
      headline: '3 pasageri au ratat îmbarcarea.',
    });
    expect(verdict({ ...run, late: 20 }).tone).toBe('critical');
    expect(verdict({ ...run, worst: { label: 'CP1 · Security', p95: 12 } }).headline).toBe(
      'Cozi lungi la CP1 · Security.',
    );
    expect(verdict(null).tone).toBe('unknown');
    expect(summary(run, 1)[1]).toBe('Așteptarea medie la control a fost de sub 1 s.');
  });
  it('shows no wait figure when staff sat idle, and says why instead', () => {
    const run = {
      passengers: 900,
      processed: 900,
      meanWait: 0.004,
      worst: { label: 'CP3 · Security', p95: 0.1 },
      late: 26,
      lateOf: 900,
      busiest: 0.117,
    };
    expect(verdict(run)).toEqual({
      tone: 'unknown',
      headline: 'Personalul a fost mult peste cerere, așa că simularea nu arată un vârf real.',
    });
    expect(summary(run, 3)).toEqual([
      'Din 900 de pasageri, 900 au trecut de control (100%) și 26 au ratat îmbarcarea, deși aproape nimeni nu a stat la coadă.',
      'Nu s-a format coadă: operatorii au fost ocupați cel mult 12% din timp, deci în scenariu sunt mult mai puțini pasageri decât poate controla personalul.',
      'Nu afișăm timpi de așteptare: la o încărcare atât de mică nu spun nimic despre un vârf real de trafic.',
    ]);
    const cards = kpiCards(run);
    expect(cards.map((card) => card.value)).toEqual([
      '900 din 900',
      'Fără coadă',
      'Fără coadă',
      '26 din 900',
    ]);
    expect(cards.map((card) => card.value).join(' ')).not.toMatch(/\bs\b|min/);
    // A short real peak in a quiet day averages low too, but its waits are shown, not hidden.
    expect(verdict({ ...run, worst: { label: 'CP1 · Security', p95: 2.6 } }).tone).not.toBe(
      'unknown',
    );
  });
  it('states missing data instead of inventing figures', async () => {
    const data = replayData();
    data.samples = data.samples.filter((row) => !(row.minute === 6 && row.zoneId === ids.otherCp));
    const { replay, results, totals: run } = await reportOf(data);
    expect(results.map((row) => row.kpi === null)).toEqual([false, true]);
    expect(run).toBeNull();
    expect(summary(run, 2)).toEqual([
      'Lipsesc rezultatele de final pentru cel puțin un punct de control, așa că nu putem rezuma rularea.',
    ]);
    expect(queueSeries(replay).map((point) => point.value)).toEqual([20, 16, 12, null]);
    expect(queueChart(replay, null).missing).toBe(1);
    expect(
      summary(
        {
          passengers: 8,
          processed: 0,
          meanWait: null,
          worst: null,
          late: 0,
          lateOf: 0,
          busiest: null,
        },
        1,
      )[1],
    ).toBe('Niciun pasager nu a terminat controlul, deci nu avem timpi de așteptare.');
    expect(
      summary(
        {
          passengers: 0,
          processed: 0,
          meanWait: null,
          worst: null,
          late: 0,
          lateOf: 0,
          busiest: null,
        },
        1,
      ),
    ).toEqual(['Simularea nu a avut pasageri.']);
    const cards = kpiCards({
      passengers: 8,
      processed: 0,
      meanWait: null,
      worst: null,
      late: 0,
      lateOf: 0,
      busiest: null,
    });
    expect(cards.map((card) => card.value)).toEqual([
      '0 din 8',
      'Fără date',
      'Fără date',
      'Fără date',
    ]);
  });

  it('refuses a mean wait when a checkpoint finished passengers but has no recorded mean', async () => {
    const { results } = await reportOf();
    const broken = results.map((row) => ({ ...row, kpi: { ...row.kpi!, meanWaitMinutes: null } }));
    expect(totals(broken)?.meanWait).toBeNull();
    expect(summary(totals(broken), 2)[1]).toBe(
      'Lipsesc timpii de așteptare pentru această rulare.',
    );
  });

  it('draws the longest queue per clock slot, with no column where a snapshot is missing', async () => {
    const data = replayData();
    data.samples = data.samples.filter((row) => row.minute !== 2);
    const chart = queueChart(await prepared(data), await prepared(candidateData()));
    expect(chart.slot).toBe(2);
    expect(chart.ticks.map((tick) => tick.label)).toEqual([
      '13:00',
      '13:01',
      '13:02',
      '13:03',
      '13:04',
      '13:05',
      '13:06',
    ]);
    expect(chart.grid.map((line) => line.label)).toEqual(['0', '10', '20']);
    expect(chart.peak).toEqual({ value: 20, time: '13:00' });
    // The closing snapshot (8 at 13:06) joins the last slot instead of opening its own.
    expect(chart.columns.map((column) => [column.label, column.value, column.other])).toEqual([
      ['13:00–13:02', 20, 10],
      ['13:02–13:04', null, 8],
      ['13:04–13:06', 12, 6],
    ]);
    expect(chart.columns[1].tip).toBe(
      '13:02–13:04 · fără date · rularea de comparat: cel mult 8 pasageri',
    );
    expect(chart.columns[1].mine).toBeNull();
    expect(chart.columns[1].theirs?.height).toBeGreaterThan(0);
    expect(chart.mark).toMatchObject({ value: 20, y: 24 });
  });

  it('starts slots on round clock times when the run does not', async () => {
    // The prepared replay is frozen; a copy lets the clock start one minute past the hour.
    const replay = structuredClone(await prepared());
    replay.data.captured.scenario.inputs.startsAtUtc = '2026-10-16T10:01:00Z';
    const chart = queueChart(replay, null);
    expect(chart.columns.map((column) => column.label)).toEqual([
      '13:01–13:02',
      '13:02–13:04',
      '13:04–13:06',
      '13:06–13:07',
    ]);
  });

  it('compares figures in words only for the comparison run', async () => {
    const run = (await reportOf()).totals!,
      other = (await reportOf(candidateData())).totals!;
    expect(kpiCards(run).every((card) => card.compared === '')).toBe(true);
    expect(kpiCards(run, other).map((card) => card.compared)).toEqual([
      'Rularea de comparat: 12 (la fel)',
      'Rularea de comparat: 38 s (acum cu 38 s mai mult)',
      'Rularea de comparat: 2 min (la fel)',
      'Rularea de comparat: fără date',
    ]);
  });

  it('allows a comparison only when the server confirms the same conditions for this pair', async () => {
    const other: ReportRun = {
      id: ids.otherRun,
      status: 'completed',
      problem: '',
      replay: await prepared(candidateData()),
    };
    expect(comparisonProblem(ids.run, other, comparisonFixture())).toBe('');
    expect(comparisonProblem(ids.run, other, null)).toBe(
      'Nu am putut verifica dacă cele două rulări se pot compara.',
    );
    const wrongPair = comparisonFixture();
    wrongPair.candidate.id = ids.mark;
    expect(comparisonProblem(ids.run, other, wrongPair)).toContain('Nu am putut verifica');
    const different = comparisonFixture();
    different.comparability!.comparable = false;
    different.comparability!.issues = [
      { code: 'seed_mismatch', message: 'Seed-uri diferite.', severity: 'blocking' },
    ];
    expect(comparisonProblem(ids.run, other, different)).toBe(
      'Rulările au fost simulate în condiții diferite, așa că nu le comparăm. Seed-uri diferite.',
    );
    const unknownBuild = comparisonFixture();
    unknownBuild.versionsMatch = null;
    expect(comparisonProblem(ids.run, other, unknownBuild)).not.toBe('');
    expect(
      comparisonProblem(
        ids.run,
        { ...other, replay: null, problem: 'Rularea a eșuat.' },
        comparisonFixture(),
      ),
    ).toBe('Rularea a eșuat.');
  });
});
describe('Bottleneck demo preset', () => {
  it('preserves layout resources and builds five flights, 600 passengers and a single staffed bottleneck', () => {
    const layout = bottleneckLayout(),
      original = JSON.stringify(layout),
      draft = makeDraft(layout, 'bottleneck', new Date('2026-10-17T08:00:00Z'));
    expect(draft.name).toBe('Bottleneck · DEMO');
    expect(draft.inputs.seed).toBe(42);
    expect(draft.inputs.durationMinutes).toBe(360);
    expect(draft.inputs.populations.reduce((sum, row) => sum + row.count, 0)).toBe(600);
    expect(draft.inputs.flights).toHaveLength(5);
    expect(draft.inputs.staffAllocations).toHaveLength(1);
    expect(
      draft.inputs.passengerGeneration!.batches.every(
        (row, index) =>
          row.checkpointId === ids.cp &&
          Math.abs(row.service.mean / layout.layout.checkpoints[0].capacityPerMinute - 1 / 3) <
            1e-12 &&
          row.arrival.maximum - row.arrival.minimum <= 35 &&
          row.arrival.maximum ===
            (Date.parse(draft.inputs.flights[index].departureAtUtc) -
              Date.parse(draft.inputs.startsAtUtc)) /
              60000 -
              10,
      ),
    ).toBe(true);
    expect(basicErrors(draft)).toEqual({});
    expect(JSON.stringify(layout)).toBe(original);
  });
  it('rejects insufficient staffing or a nonsynthetic layout instead of faking a valid demo', () => {
    const layout = bottleneckLayout();
    layout.layout.staff.pop();
    expect(() => makeDraft(layout, 'bottleneck')).toThrow('Bottleneck');
    const other = bottleneckLayout();
    other.layout.scene.isSynthetic = false;
    expect(() => makeDraft(other, 'bottleneck')).toThrow('Bottleneck');
  });
});

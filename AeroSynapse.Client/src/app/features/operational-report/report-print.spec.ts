import { ids, replayData } from '../replay/replay.testing';
import { prepared, candidateData } from './report.testing';
import { reportDocument } from './report-print';

describe('Detailed operational PDF document', () => {
  beforeEach(async () => {
    vi.stubGlobal(
      'crypto',
      (await vi.importActual<{ webcrypto: Crypto }>('node:crypto')).webcrypto,
    );
  });
  afterEach(() => vi.unstubAllGlobals());
  const read = (html: string) => new DOMParser().parseFromString(html, 'text/html');

  it('includes captured context, detailed results, queues and readable definitions with two decimals', async () => {
    const replay = await prepared();
    const doc = read(reportDocument(replay, null, '', new Date('2026-11-01T12:00:00Z')));
    expect(doc.title).toContain('Replay demo');
    expect(doc.body.textContent).toContain(ids.run);
    expect(doc.body.textContent).toContain('1,25 min');
    expect(doc.body.textContent).toContain('2,00 min');
    expect(doc.body.textContent).toContain('50,00%');
    expect(doc.body.textContent).toContain('Date sintetice');
    expect(doc.querySelectorAll('[data-checkpoint]')).toHaveLength(2);
    expect(doc.querySelectorAll('[data-queue-interval]')).toHaveLength(3);
    expect(doc.querySelector('svg')).not.toBeNull();
    expect(doc.body.textContent).toContain('P95');
    expect(doc.body.textContent).toContain(replay.envelope.contentHash);
    expect(doc.querySelector('style')?.textContent).toContain('A4');
    expect(doc.querySelector('style')?.textContent).toContain('counter(page)');
  });

  it('escapes scenario, checkpoint and event text instead of executing report content', async () => {
    const data = replayData();
    const unsafe = '<img src=x onerror="alert(1)"><script>alert(1)</script>';
    data.captured.scenario.scenario.name = unsafe;
    data.captured.layout.layout.checkpoints[0].name = unsafe;
    data.captured.scenario.inputs.events[0].type = unsafe;
    const doc = read(reportDocument(await prepared(data), null));
    expect(doc.body.textContent).toContain(unsafe);
    expect(doc.querySelector('script, img, [onerror]')).toBeNull();
    expect(doc.querySelectorAll('[data-checkpoint]')).toHaveLength(2);
  });

  it('includes only verified comparison results and preserves a rejected comparison explanation', async () => {
    const replay = await prepared();
    const compared = await prepared(candidateData());
    const doc = read(reportDocument(replay, compared));
    expect(doc.querySelector('[data-comparison]')?.textContent).toContain(ids.otherRun);
    expect(doc.querySelector('[data-comparison]')?.textContent).toContain('0,63 min');
    expect(doc.querySelector('[data-comparison]')?.textContent).toContain('0,00');
    const rejected = read(reportDocument(replay, null, 'Seed-uri diferite.'));
    expect(rejected.body.textContent).toContain('Seed-uri diferite.');
    expect(rejected.querySelector('[data-comparison]')).toBeNull();
    expect(rejected.body.textContent).not.toContain(ids.otherRun);
  });

  it('shows missing snapshots and final KPIs explicitly rather than replacing them with zero', async () => {
    const data = replayData();
    data.samples = data.samples.filter((row) => !(row.zoneId === ids.cp && row.minute === 2));
    data.samples.find((row) => row.zoneId === ids.cp && row.minute === 6)!.kpi = null;
    const doc = read(reportDocument(await prepared(data), null));
    expect(doc.body.textContent).toContain('Lipsesc rezultatele de final');
    expect(doc.querySelector(`[data-checkpoint="${ids.cp}"]`)?.textContent).toContain('Fără date');
    expect(doc.body.textContent).toContain('1 capturi lipsă');
    expect(doc.querySelectorAll('[data-queue-interval]')[1].textContent).toContain('Fără date');
  });
});

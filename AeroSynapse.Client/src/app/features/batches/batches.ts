import { ActivatedRoute, RouterLink } from '@angular/router';
import { TERMINAL_MAP_ZONES } from '../digital-twin/terminal-zones';
import { SimulationJourney } from '../../shared/ui/simulation-journey';
import { DatePipe, DecimalPipe } from '@angular/common';
import {
  Component,
  DestroyRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  EMPTY,
  Subject,
  catchError,
  exhaustMap,
  finalize,
  map,
  mergeMap,
  switchMap,
  takeWhile,
  tap,
  timer,
} from 'rxjs';
import { ApiClient } from '../../core/http/api-client';
import { SimulationContext } from '../../core/simulation-context';
import { createDataResource } from '../../shared/data-resource';
import { DataState } from '../../shared/ui/data-state';
import { ScenarioSummary } from '../operations/operations.models';
import { BatchDetails, BatchSummary } from './batches.models';
import {
  BATCH_METRICS,
  RUN_METRIC_LIMIT,
  RunMetricPage,
  RunValues,
  DayOutcome,
  Stability,
  duel,
  finalValues,
  formatValue,
  spreadLanes,
  stability,
} from './batch-insight';
import { BatchSpread } from './batch-spread';
import { CountUp } from '../../shared/ui/count-up';
import { prefersReducedMotion } from '../../shared/ui/motion';
import { OutcomeTone, outcomeTone, percentChange } from '../../shared/ui/outcome-cards';

const STABILITY: Record<Stability, { tone: string; badge: string; headline: string }> = {
  single: {
    tone: 'neutral',
    badge: 'O singură zi',
    headline: 'O singură zi simulată: încă nu știm cât variază rezultatul.',
  },
  stable: {
    tone: 'better',
    badge: 'Rezultat stabil',
    headline: 'Rezultat stabil: zilele simulate seamănă mult între ele.',
  },
  moderate: {
    tone: 'neutral',
    badge: 'Variază moderat',
    headline: 'Rezultatul variază moderat de la o zi la alta.',
  },
  wide: {
    tone: 'warning',
    badge: 'Variază mult',
    headline: 'Atenție: rezultatul variază mult de la o zi la alta.',
  },
};

@Component({
  selector: 'app-batches',
  imports: [
    FormsModule,
    DatePipe,
    DecimalPipe,
    DataState,
    SimulationJourney,
    RouterLink,
    BatchSpread,
    CountUp,
  ],
  templateUrl: './batches.html',
  styleUrl: './batches.css',
})
export class Batches {
  private readonly api = inject(ApiClient);
  private readonly simulation = inject(SimulationContext);
  private readonly destroy = inject(DestroyRef);
  private readonly injector = inject(Injector);
  /** Set when the viewer launched or opened a test, so the results scroll into view once shown. */
  private reveal = false;
  private readonly watch = new Subject<string>();
  private readonly initialParams = inject(ActivatedRoute, { optional: true })?.snapshot
    .queryParamMap;
  private initialized = false;
  baselineId = '';
  readonly scenarios = createDataResource(
    () =>
      this.api
        .get<ScenarioSummary[]>('scenarios')
        .pipe(tap((rows) => this.initializeSelection(rows))),
    (rows) => rows.length === 0,
  );
  readonly history = createDataResource(
    () => this.api.get<BatchSummary[]>('simulation-batches'),
    (rows) => rows.length === 0,
  );
  readonly detail = signal<BatchDetails | null>(null);
  readonly busy = signal(false);
  readonly message = signal('');
  readonly pollingError = signal('');
  readonly counts = computed(() => {
    const counts: Record<string, number | undefined> = {};
    for (const row of this.detail()?.replications ?? [])
      counts[row.status] = (counts[row.status] ?? 0) + 1;
    return counts;
  });
  /** Failed and stopped simulations in one sentence, or '' when every simulation ran. */
  readonly setbacks = computed(() => {
    const failed = (this.counts()['failed'] ?? 0) + (this.counts()['timed_out'] ?? 0),
      stopped = this.counts()['cancelled'] ?? 0;
    const parts = [
      failed === 1 ? 'O simulare nu a reușit' : failed ? `${failed} simulări nu au reușit` : '',
      stopped === 1 ? 'una a fost oprită' : stopped ? `${stopped} au fost oprite` : '',
    ].filter(Boolean);
    if (!parts.length) return '';
    const sentence = parts.join(' și ');
    return sentence[0].toUpperCase() + sentence.slice(1) + '.';
  });
  readonly progress = computed(() => {
    const rows = this.detail()?.replications ?? [];
    return {
      done: rows.filter((row) => !['pending', 'running'].includes(row.status)).length,
      total: rows.length,
    };
  });
  /** Final values per run, fetched once per valid run so every simulated day can become a dot. */
  readonly runValues = signal<ReadonlyMap<string, RunValues>>(new Map());
  private readonly requestedRuns = new Set<string>();
  private readonly runQueue = new Subject<string>();
  readonly metricName = signal(BATCH_METRICS[0].name);
  readonly zoneChoice = signal('');
  /** Zones where something happened; idle zones only report zeros. */
  readonly zones = computed(() => {
    const rows = this.detail()?.metrics ?? [];
    return [...new Set(rows.map((row) => row.zoneId))].filter((zone) =>
      rows.some((row) => row.zoneId === zone && (row.values.maximum ?? 0) !== 0),
    );
  });
  /** The chosen zone, or else the one where the starting scenario waits longest. */
  readonly zoneId = computed(() => {
    const zones = this.zones();
    if (zones.includes(this.zoneChoice())) return this.zoneChoice();
    const report = this.detail();
    const start = report?.variants.find((row) => row.position === 0)?.id;
    const wait = (zone: string) =>
      report?.metrics.find(
        (row) => row.variantId === start && row.zoneId === zone && row.name === 'meanWaitMinutes',
      )?.values.median ?? -1;
    return [...zones].sort((a, b) => wait(b) - wait(a))[0] ?? '';
  });
  readonly metricOptions = computed(() =>
    BATCH_METRICS.filter((metric) =>
      this.detail()?.metrics.some(
        (row) => row.zoneId === this.zoneId() && row.name === metric.name && row.values.count > 0,
      ),
    ),
  );
  readonly metric = computed(
    () =>
      this.metricOptions().find((row) => row.name === this.metricName()) ??
      this.metricOptions()[0] ??
      null,
  );
  readonly unit = computed(
    () =>
      this.detail()?.metrics.find(
        (row) => row.zoneId === this.zoneId() && row.name === this.metric()?.name,
      )?.unit ?? '',
  );
  readonly lanes = computed(() => {
    const report = this.detail(),
      metric = this.metric();
    return report && metric
      ? spreadLanes(report, this.runValues(), this.zoneId(), metric.name, this.unit())
      : [];
  });
  /** Valid runs whose individual values could not be read, so they have no dot. */
  readonly hiddenDays = computed(
    () =>
      this.detail()?.replications.filter(
        (row) => row.resultsValid && this.runValues().get(row.runId) === null,
      ).length ?? 0,
  );
  readonly duels = computed(() => {
    const report = this.detail(),
      metric = this.metric();
    if (!report || !metric) return [];
    return report.variants
      .filter((variant) => variant.position > 0)
      .map((variant) => {
        const days = duel(report, this.runValues(), variant.id, this.zoneId(), metric, this.unit());
        const count = (outcome: string) => days.filter((day) => day.outcome === outcome).length;
        return {
          ...variant,
          days,
          wins: count('better'),
          losses: count('worse'),
          paired: days.length - count('missing'),
        };
      });
  });
  readonly verdict = computed(() => {
    const lanes = this.lanes(),
      metric = this.metric(),
      unit = this.unit();
    const start = lanes[0]?.stats;
    if (!metric || !start || start.count === 0 || start.median === null) return null;
    const format = (value: number | null) => formatValue(unit, value);
    const split = (text: string) => {
      const [figure, ...rest] = text.split(' ');
      return { figure, unit: rest.join(' ') };
    };
    const eyebrow = `Verdict · ${metric.label} · ${this.zoneLabel(this.zoneId())}`;
    // Negative means better, whichever direction the metric improves in.
    const gain = (percent: number | null) =>
      percent === null ? null : metric.lowerIsBetter ? percent : -percent;
    const best = lanes
      .slice(1)
      .filter((lane) => lane.stats?.count && lane.stats.median !== null)
      .map((lane) => ({ lane, percent: percentChange(start.median, lane.stats!.median) }))
      .sort((a, b) => (gain(a.percent) ?? Infinity) - (gain(b.percent) ?? Infinity))[0];
    if (best) {
      const median = best.lane.stats!.median!;
      const tone: OutcomeTone =
        best.percent === null
          ? median === start.median
            ? 'neutral'
            : median < start.median === metric.lowerIsBetter
              ? 'better'
              : 'worse'
          : outcomeTone(gain(best.percent));
      const days = this.duels().find((row) => row.id === best.lane.variantId);
      return {
        tone,
        eyebrow,
        headline:
          tone === 'neutral'
            ? `„${best.lane.name}” nu schimbă vizibil ${metric.noun}: în jur de ${format(median)}.`
            : `„${best.lane.name}” ${median > start.median ? 'crește' : 'reduce'} ${metric.noun} de la ${format(start.median)} la ${format(median)}.`,
        detail: days?.paired
          ? `Mai bună în ${days.wins} din ${days.paired} zile cu exact aceleași sosiri de pasageri.`
          : 'Comparația zi cu zi apare după ce ambele variante termină aceleași zile.',
        badge: STABILITY[stability(best.lane.stats)].badge,
        sign: median === start.median ? '' : median < start.median ? '−' : '+',
        ...(best.percent === null
          ? split(format(Math.abs(median - start.median)))
          : { figure: String(Math.abs(Math.round(best.percent))), unit: '%' }),
        caption: 'față de scenariul de pornire, într-o zi obișnuită',
      };
    }
    const level = STABILITY[stability(start)];
    const [good, bad] = metric.lowerIsBetter
      ? [start.minimum, start.maximum]
      : [start.maximum, start.minimum];
    return {
      tone: level.tone,
      eyebrow,
      headline: level.headline,
      detail:
        start.count < 2
          ? 'Rulează cel puțin 10 repetări ca să vezi un interval realist.'
          : `Interval realist: între ${format(start.p05)} și ${format(start.p95)} în 9 zile din 10. ` +
            `Cea mai bună zi: ${format(good)}; cea mai grea: ${format(bad)}.` +
            (start.count < 10
              ? ` Sunt doar ${start.count} zile simulate, așa că intervalul e orientativ.`
              : ''),
      badge: level.badge,
      sign: '',
      ...split(format(start.median)),
      caption: 'într-o zi obișnuită',
    };
  });
  name = 'Test repetat';
  seedText = '42, 43, 44';
  timeLimitSeconds = 60;
  private command: { key: string; id: string } | null = null;

  constructor() {
    this.watch
      .pipe(
        switchMap((id) =>
          timer(0, 1000).pipe(
            exhaustMap(() =>
              this.api.get<BatchDetails>(`simulation-batches/${id}`).pipe(
                catchError(() => {
                  this.pollingError.set(
                    'Reconectare la batch… Datele afișate sunt ultima stare confirmată.',
                  );
                  return EMPTY;
                }),
              ),
            ),
            takeWhile(
              (value) =>
                ['pending', 'running'].includes(value.batch.status) ||
                value.replications.some((row) => row.remoteCancellationPending),
              true,
            ),
          ),
        ),
        takeUntilDestroyed(this.destroy),
      )
      .subscribe((value) => {
        this.pollingError.set('');
        this.detail.set(value);
        this.revealResults();
      });
    this.runQueue
      .pipe(
        mergeMap(
          (id) =>
            this.api.get<RunMetricPage>(`runs/${id}/metrics?limit=${RUN_METRIC_LIMIT}`).pipe(
              map((page) => [id, finalValues(page)] as const),
              catchError(() => {
                this.requestedRuns.delete(id); // Retried with the next refresh.
                return EMPTY;
              }),
            ),
          4,
        ),
        takeUntilDestroyed(this.destroy),
      )
      .subscribe(([id, values]) =>
        this.runValues.update((known) => new Map(known).set(id, values)),
      );
    effect(() => {
      for (const row of this.detail()?.replications ?? [])
        if (row.resultsValid && !this.requestedRuns.has(row.runId)) {
          this.requestedRuns.add(row.runId);
          this.runQueue.next(row.runId);
        }
    });
  }
  private initializeSelection(rows: ScenarioSummary[]) {
    if (this.initialized || !rows.length) return;
    this.initialized = true;
    const scenarioId = this.initialParams?.get('scenarioId');
    if (scenarioId && rows.some((row) => row.id === scenarioId)) {
      this.chooseBaseline(scenarioId);
      this.simulation.remember(scenarioId);
    }
  }
  repeatCount() {
    return this.seedText
      .trim()
      .split(/[\s,;]+/)
      .filter(Boolean).length;
  }
  readonly presets = [{ count: 3 }, { count: 5 }, { count: 10 }, { count: 25 }];
  isPreset() {
    return this.presets.some((preset) => preset.count === this.repeatCount());
  }
  /** One square per simulated day, capped at the 25 the API accepts. */
  dayPreview() {
    return Array.from({ length: Math.min(this.repeatCount(), 25) }, (_, index) => index);
  }
  outcomeLabel(outcome: DayOutcome) {
    return {
      better: 'varianta a fost mai bună',
      worse: 'varianta a fost mai slabă',
      same: 'la egalitate',
      missing: 'nu se poate compara',
    }[outcome];
  }
  setRepetitions(count: number) {
    if (!Number.isInteger(count) || count < 1 || count > 25 || this.busy()) return;
    this.seedText = Array.from({ length: count }, (_, index) => 42 + index).join(', ');
  }
  zoneLabel(id: string) {
    const known = TERMINAL_MAP_ZONES.find((zone) => zone.zoneId === id);
    const zones = [...new Set(this.detail()?.metrics.map((row) => row.zoneId) ?? [])];
    return known?.label ?? `Zona ${zones.indexOf(id) + 1}`;
  }
  create() {
    if (this.busy()) return;
    const state = this.scenarios.state();
    if (state.status !== 'ready') return;
    const seeds = this.seedText
      .trim()
      .split(/[\s,;]+/)
      .filter(Boolean)
      .map(Number);
    if (
      seeds.length < 1 ||
      seeds.length > 25 ||
      seeds.some((seed) => !Number.isInteger(seed) || seed < 0 || seed > 2147483647) ||
      new Set(seeds).size !== seeds.length ||
      !this.name.trim() ||
      this.name.length > 160 ||
      !state.data.some((row) => row.id === this.baselineId) ||
      !Number.isInteger(this.timeLimitSeconds) ||
      this.timeLimitSeconds < 1 ||
      this.timeLimitSeconds > 3600
    ) {
      this.message.set(
        'Alege un scenariu. Verifică numele, cele 1–25 de repetări și limita de calcul din setările avansate. Seed-urile trebuie să fie numere întregi distincte între 0 și 2147483647.',
      );
      return;
    }
    const reference = (id: string) => ({
      scenarioId: id,
      revision: state.data.find((row) => row.id === id)!.revision,
    });
    const value = {
      name: this.name.trim(),
      baseline: reference(this.baselineId),
      candidates: [],
      seeds,
      timeLimitSeconds: this.timeLimitSeconds,
    };
    this.send('simulation-batches', value);
  }
  chooseBaseline(id: string) {
    this.baselineId = id;
    const state = this.scenarios.state();
    if (state.status === 'ready' && state.data.some((row) => row.id === id))
      this.simulation.remember(id);
  }
  select(id: string, reveal = false) {
    if (this.busy()) return;
    this.reveal = reveal;
    this.detail.set(null);
    this.pollingError.set('');
    this.watch.next(id);
  }
  cancel() {
    const value = this.detail();
    if (this.busy() || !value || !['pending', 'running'].includes(value.batch.status)) return;
    this.busy.set(true);
    this.message.set('');
    this.api
      .post<BatchDetails>(`simulation-batches/${value.batch.id}/cancel`)
      .pipe(
        finalize(() => this.busy.set(false)),
        takeUntilDestroyed(this.destroy),
      )
      .subscribe({
        next: (result) => {
          this.detail.set(result);
          this.watch.next(result.batch.id);
          this.history.reload();
        },
        error: (error) => this.report(error),
      });
  }
  replay() {
    const value = this.detail();
    if (this.busy() || !value) return;
    if (
      !Number.isInteger(this.timeLimitSeconds) ||
      this.timeLimitSeconds < 1 ||
      this.timeLimitSeconds > 3600
    ) {
      this.message.set('Limita trebuie să fie 1–3600 secunde.');
      return;
    }
    this.send(`simulation-batches/${value.batch.id}/replay`, {
      name: `${value.batch.name.slice(0, 145)} · reluare`,
      timeLimitSeconds: this.timeLimitSeconds,
    });
  }
  private send(path: string, value: object) {
    const key = JSON.stringify([path, value]);
    if (this.command?.key !== key) this.command = { key, id: crypto.randomUUID() };
    this.busy.set(true);
    this.message.set('');
    this.api
      .post<BatchDetails>(path, { ...value, commandId: this.command!.id })
      .pipe(
        finalize(() => this.busy.set(false)),
        takeUntilDestroyed(this.destroy),
      )
      .subscribe({
        next: (result) => {
          this.command = null;
          this.reveal = true;
          this.detail.set(result);
          this.revealResults();
          this.watch.next(result.batch.id);
          this.history.reload();
        },
        error: (error) => this.report(error),
      });
  }
  private revealResults() {
    if (!this.reveal) return;
    this.reveal = false;
    afterNextRender(
      () =>
        document.getElementById('results-title')?.scrollIntoView({
          behavior: prefersReducedMotion() ? 'auto' : 'smooth',
          block: 'start',
        }),
      { injector: this.injector },
    );
  }
  private report(error: unknown) {
    this.message.set(
      error instanceof Error ? error.message : 'Operația batch nu a putut fi finalizată.',
    );
  }
  variant(id: string) {
    const value = this.detail()?.variants.find((row) => row.id === id);
    return value
      ? `${value.position === 0 ? 'Scenariul de pornire' : 'Varianta ' + value.position}: ${value.name}`
      : id;
  }
  status(value: string) {
    return (
      (
        {
          pending: 'În așteptare',
          running: 'În execuție',
          completed: 'Finalizat',
          completed_with_failures: 'Finalizat cu eșecuri',
          failed: 'Eșuat',
          cancelled: 'Anulat',
          timed_out: 'Limită de timp depășită',
        } as Record<string, string>
      )[value] ?? value
    );
  }
}

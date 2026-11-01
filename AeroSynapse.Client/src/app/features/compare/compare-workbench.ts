import { SimulationContext } from '../../core/simulation-context';
import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  EMPTY,
  Subject,
  Subscription,
  catchError,
  exhaustMap,
  finalize,
  map,
  switchMap,
  takeWhile,
  timer,
  timeout,
} from 'rxjs';
import { ApiError } from '../../core/http/api-error.interceptor';
import { OPERATION_REQUEST_TIMEOUT_MS, operationError } from '../../core/http/operation-request';
import { validRunId } from '../operational-report/report.models';
import { ApiClient } from '../../core/http/api-client';
import { createDataResource } from '../../shared/data-resource';
import { DataState } from '../../shared/ui/data-state';
import { BatchDetails, BatchSummary } from '../batches/batches.models';
import { ComparedMetric, ComparedRun, RunComparison } from '../operations/comparison.models';
import { ScenarioDetails, ScenarioSummary } from '../operations/operations.models';
import { RunComparisonReport } from '../operations/run-comparison-report';
import { StudioLayout } from '../scenario-studio/studio.models';

export type Tone = 'better' | 'worse' | 'same' | 'unknown';
export const TONE_WORD: Record<Tone, string> = {
  better: 'Mai bine',
  worse: 'Mai rău',
  same: 'La fel',
  unknown: 'Fără concluzie',
};
/** What the new variant changed at one post, so the result can name it. */
export interface StaffChange {
  zoneId: string;
  before: number | null;
  after: number;
  /** Singular and plural, e.g. persoană / persoane. */
  unit: [string, string];
}
const numbers = new Intl.NumberFormat('ro-RO', { maximumFractionDigits: 2 });
const fmt = (value: number) => numbers.format(value);
const signed = (value: number) => (value < 0 ? '−' : '+') + fmt(Math.abs(value));
const pct = (value: number) => signed(Math.round(value * 10) / 10) + '%';
/** Romanian counts: 1 pasager, 5 pasageri, 20 de pasageri. */
const count = (n: number, one: string, many: string) =>
  `${fmt(n)} ${n === 1 ? one : n % 100 >= 20 || (n >= 100 && n % 100 === 0) ? 'de ' + many : many}`;
const finite = (value: unknown): value is number =>
  typeof value === 'number' && Number.isFinite(value);
const valid = (run: ComparedRun) => run.resultsValid && run.status === 'completed';
/** Same rules as the detailed report: no conclusion from runs that cannot be compared. */
function comparable(report: RunComparison) {
  const check = report.comparability;
  return (
    check?.comparable === true &&
    check.populationMatches === true &&
    !check.issues.some((issue) => issue.severity === 'blocking') &&
    report.baseline.id !== report.candidate.id &&
    report.versionsMatch === true &&
    valid(report.baseline) &&
    valid(report.candidate)
  );
}
/** A difference only when both values exist and were measured at the same moment. */
function delta(row: ComparedMetric | undefined) {
  return row &&
    finite(row.baseline) &&
    finite(row.candidate) &&
    finite(row.delta) &&
    row.baselineRecordedAtUtc === row.candidateRecordedAtUtc &&
    (!row.unavailableReason ||
      ['baseline_not_positive', 'percentage_out_of_range'].includes(row.unavailableReason))
    ? row.delta
    : null;
}
function percent(row: ComparedMetric | undefined) {
  return delta(row) !== null &&
    !row!.unavailableReason &&
    row!.baseline! > 0 &&
    finite(row!.percentChange)
    ? row!.percentChange
    : null;
}
// Smallest difference an operator would notice: 0.003 → 0.008 min is +150% but still "no queue".
const NOTICEABLE: Record<string, number> = { meanWaitMinutes: 0.5, maximumQueue: 2 };
function tone(row: ComparedMetric | undefined, lowerIsBetter = true): Tone {
  const change = delta(row),
    relative = percent(row);
  if (change === null) return 'unknown';
  // Same "no change" rule as the shared outcome cards (under half a percent), plus the noticeable floor.
  if (
    Math.abs(change) < (NOTICEABLE[row!.name] ?? 0) ||
    (relative !== null ? Math.abs(relative) < 0.5 : change === 0)
  )
    return 'same';
  return change < 0 === lowerIsBetter ? 'better' : 'worse';
}
const amount = (value: number, unit: string) =>
  unit === 'pasageri' ? count(value, 'pasager', 'pasageri') : `${fmt(value)} ${unit}`;
const KPIS = [
  { name: 'meanWaitMinutes', label: 'Așteptare medie', unit: 'min', lowerIsBetter: true },
  { name: 'maximumQueue', label: 'Cea mai lungă coadă', unit: 'pasageri', lowerIsBetter: true },
  {
    name: 'processedPassengers',
    label: 'Pasageri serviți',
    unit: 'pasageri',
    lowerIsBetter: false,
  },
];

/** Plain-language result of a staffing comparison at the changed post. Never states more than the data shows. */
export function explain(
  report: RunComparison,
  zoneId: string | undefined,
  zone: string,
  staff: StaffChange | null,
) {
  if (!comparable(report)) {
    const failed = !valid(report.baseline)
      ? 'personalul actual'
      : !valid(report.candidate)
        ? 'varianta nouă'
        : null;
    return {
      tone: 'unknown' as Tone,
      verdict: failed
        ? `Nu putem trage o concluzie: simularea pentru ${failed} nu are rezultate valide.`
        : 'Nu putem trage o concluzie: cele două simulări nu pot fi comparate corect.',
      cards: [],
      notes: [],
      controlled: false,
    };
  }
  const at = (name: string) =>
    report.metrics.find((row) => row.zoneId === zoneId && row.name === name);
  const wait = at('meanWaitMinutes'),
    queue = at('maximumQueue'),
    served = at('processedPassengers'),
    utilization = at('cumulativeUtilization');
  const change = delta(wait),
    relative = percent(wait);
  const share = relative === null ? '' : ` (${pct(relative)})`;
  let verdictTone = tone(wait),
    verdict: string;
  if (served?.baseline === 0 && served.candidate === 0) {
    verdictTone = 'unknown';
    verdict = `La ${zone} nu a trecut niciun pasager în nicio simulare, deci schimbarea nu poate fi evaluată.`;
  } else if (change === null)
    verdict = `Nu putem compara așteptarea medie la ${zone}: lipsește o valoare validă într-una dintre simulări.`;
  else if (verdictTone === 'same')
    verdict = `Varianta nouă nu schimbă practic așteptarea medie la ${zone}: ${fmt(wait!.candidate!)} min, față de ${fmt(wait!.baseline!)} min acum.`;
  else
    verdict = `Varianta nouă ${change < 0 ? 'reduce' : 'crește'} așteptarea medie la ${zone} cu ${fmt(Math.abs(change))} min${share} față de personalul actual.`;

  const cards = KPIS.flatMap((kpi) => {
    const row = at(kpi.name);
    if (!row || !finite(row.baseline) || !finite(row.candidate)) return [];
    const difference = delta(row),
      rate = percent(row);
    return [
      {
        label: kpi.label,
        before: amount(row.baseline, kpi.unit),
        after: amount(row.candidate, kpi.unit),
        change:
          difference === null
            ? 'Diferență indisponibilă'
            : fmt(Math.abs(difference)) === '0'
              ? 'fără diferență'
              : `${signed(difference)} ${kpi.unit}${rate === null ? '' : ` (${pct(rate)})`}`,
        tone: tone(row, kpi.lowerIsBetter),
      },
    ];
  });

  const notes: string[] = [];
  const peak = delta(queue);
  if (peak !== null) {
    const [a, b] = [queue!.baseline!, queue!.candidate!];
    notes.push(
      !a && !b
        ? `La ${zone} nu s-a format coadă în nicio variantă.`
        : peak === 0
          ? `Cea mai lungă coadă la ${zone} rămâne la ${count(a, 'pasager', 'pasageri')}.`
          : `Cea mai lungă coadă la ${zone} ${peak < 0 ? 'scade' : 'crește'} de la ${fmt(a)} la ${count(b, 'pasager', 'pasageri')}.`,
    );
  }
  // Passengers at risk of missing boarding are distinct per control, so the airport total is their sum.
  const risk = report.metrics.filter((row) => row.name === 'boardingAtRiskCount');
  if (risk.length && !report.metricsTruncated && risk.every((row) => delta(row) !== null)) {
    const a = risk.reduce((sum, row) => sum + row.baseline!, 0),
      b = risk.reduce((sum, row) => sum + row.candidate!, 0);
    notes.push(
      a === b
        ? a
          ? `În tot aeroportul, același număr de pasageri riscă să întârzie la poartă: ${fmt(a)}.`
          : 'În tot aeroportul, niciun pasager nu riscă să întârzie la poartă.'
        : `În tot aeroportul, pasagerii care riscă să întârzie la poartă ${b < a ? 'scad' : 'cresc'} de la ${fmt(a)} la ${fmt(b)}.`,
    );
  }
  const busy = delta(utilization) === null ? null : utilization!;
  if (staff || busy) {
    const people =
      staff && staff.before !== null
        ? `${fmt(staff.before)} → ${count(staff.after, ...staff.unit)}`
        : '';
    const load = busy
      ? `ocupat ${fmt(Math.round(busy.baseline! * 100))}% → ${fmt(Math.round(busy.candidate! * 100))}% din timp`
      : '';
    if (people || load)
      notes.push(`Personal la ${zone}: ${[people, load].filter(Boolean).join(', ')}.`);
  }
  if (
    staff?.before != null &&
    staff.after < staff.before &&
    verdictTone === 'same' &&
    tone(queue) !== 'worse'
  )
    notes.push(
      `Cu ${count(staff.after, ...staff.unit)} în loc de ${fmt(staff.before)}, rezultatul simulat la ${zone} nu se înrăutățește.`,
    );
  const extra = delta(served);
  if (extra)
    notes.push(
      `Varianta nouă servește ${count(Math.abs(extra), 'pasager', 'pasageri')} ${extra > 0 ? 'în plus' : 'mai puțin'} la ${zone}. Așteptarea medie include doar pasagerii serviți.`,
    );
  return { tone: verdictTone, verdict, cards, notes, controlled: true };
}

@Component({
  selector: 'app-compare-workbench',
  imports: [FormsModule, RouterLink, DataState, RunComparisonReport],
  templateUrl: './compare-workbench.html',
  styleUrl: './compare-workbench.css',
})
export class CompareWorkbench {
  private readonly simulation = inject(SimulationContext);
  private readonly api = inject(ApiClient);
  private readonly destroy = inject(DestroyRef);
  private readonly watch = new Subject<string | null>();
  private comparisonRequest?: Subscription;
  private comparedBatch: string | null = null;
  private command: {
    key: string;
    id: string;
    scenarioId: string;
    body: {
      commandId: string;
      revision: number;
      name: string;
      checkpointId: string;
      staffCount: number;
      timeLimitSeconds: number;
    };
  } | null = null;
  readonly pendingCommand = signal(false);
  readonly scenarios = createDataResource(
    () => this.api.get<ScenarioSummary[]>('scenarios').pipe(timeout(OPERATION_REQUEST_TIMEOUT_MS)),
    (rows) => !rows.length,
  );
  readonly source = signal<{ scenario: ScenarioDetails; layout: StudioLayout } | null>(null);
  readonly detail = signal<BatchDetails | null>(null);
  readonly report = signal<RunComparison | null>(null);
  readonly change = signal<StaffChange | null>(null);
  readonly toneWord = TONE_WORD;
  readonly result = computed(() => {
    const report = this.report();
    if (!report) return null;
    // shortcut: without a recorded change (a pair opened directly) the first zone with a wait stands in.
    const zoneId =
      this.change()?.zoneId ?? report.metrics.find((row) => row.name === 'meanWaitMinutes')?.zoneId;
    return explain(
      report,
      zoneId,
      (zoneId && this.zoneNames()[zoneId]) || 'postul ales',
      this.change(),
    );
  });
  readonly busy = signal(false);
  readonly comparing = signal(false);
  readonly message = signal('');
  readonly pollingError = signal('');
  scenarioId = '';
  checkpointId = '';
  name = 'Comparație de personal';
  staffCount = 0;
  timeLimitSeconds = 60;

  constructor() {
    this.destroy.onDestroy(() => this.comparisonRequest?.unsubscribe());
    this.watch
      .pipe(
        switchMap((id) =>
          id === null
            ? EMPTY
            : timer(0, 1000).pipe(
                exhaustMap(() =>
                  this.api.get<BatchDetails>(`simulation-batches/${id}`).pipe(
                    timeout(OPERATION_REQUEST_TIMEOUT_MS),
                    map((value) => {
                      if (!this.belongs(value, id))
                        throw new Error(
                          'Manifestul primit nu aparține perechii și scenariului selectat.',
                        );
                      return value;
                    }),
                    catchError((error) => {
                      this.pollingError.set(
                        operationError(error) +
                          ' Se afișează ultima stare confirmată; reconectare automată…',
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
        if (
          !['pending', 'running'].includes(value.batch.status) &&
          this.paired(value) &&
          this.comparedBatch !== value.batch.id
        )
          this.compare();
      });
    const scenarioId = inject(ActivatedRoute).snapshot.queryParamMap.get('scenarioId');
    if (scenarioId) {
      this.scenarioId = scenarioId;
      this.select();
    }
  }
  select() {
    if (this.busy() || this.pendingCommand()) return;
    this.watch.next(null);
    this.comparisonRequest?.unsubscribe();
    this.comparing.set(false);
    this.source.set(null);
    this.detail.set(null);
    this.report.set(null);
    this.change.set(null);
    this.message.set('');
    this.pollingError.set('');
    this.command = null;
    this.comparedBatch = null;
    if (!this.scenarioId) return;
    const scenarioId = this.scenarioId;
    this.busy.set(true);
    this.api
      .get<ScenarioDetails>(`scenarios/${scenarioId}`)
      .pipe(
        switchMap((scenario) =>
          this.api
            .get<StudioLayout>(
              `scenes/${scenario.scenario.sceneId}/layout-versions/${scenario.scenario.layoutVersionId}`,
            )
            .pipe(map((layout) => ({ layout, scenario }))),
        ),
        timeout(OPERATION_REQUEST_TIMEOUT_MS),
        finalize(() => this.busy.set(false)),
        takeUntilDestroyed(this.destroy),
      )
      .subscribe({
        next: (value) => {
          if (
            value.scenario.scenario.id !== scenarioId ||
            value.layout.version.id !== value.scenario.scenario.layoutVersionId ||
            value.layout.layout.scene.id !== value.scenario.scenario.sceneId
          ) {
            this.message.set('Scenariul și layout-ul primite nu corespund selecției.');
            return;
          }
          this.source.set(value);
          this.simulation.remember(scenarioId);
          this.checkpointId =
            value.scenario.inputs.staffAllocations[0]?.checkpointId ??
            value.layout.layout.checkpoints[0]?.id ??
            '';
          this.staffCount =
            this.currentCount() < this.availableCount()
              ? this.currentCount() + 1
              : this.currentCount();
          this.restoreCommand();
        },
        error: (error) => this.error(error),
      });
  }
  status(value: string) {
    return (
      (
        {
          pending: 'În așteptare',
          planned: 'În așteptare',
          running: 'Se calculează',
          completed: 'Finalizat',
          completed_with_failures: 'Finalizat parțial',
          failed: 'Calcul nereușit',
          cancelled: 'Oprit',
          timed_out: 'Timp de calcul depășit',
        } as Record<string, string>
      )[value] ?? value
    );
  }
  zoneNames() {
    const layout = this.source()?.layout.layout;
    // Check-in desks are compared beside the controls when both runs simulated them.
    return Object.fromEntries(
      [...(layout?.checkpoints ?? []), ...(layout?.checkInAreas ?? [])].map((zone) => [
        zone.id,
        zone.name,
      ]),
    );
  }
  checkpointChanged() {
    this.staffCount =
      this.currentCount() < this.availableCount() ? this.currentCount() + 1 : this.currentCount();
  }
  /** The scenario's check-in desks, when it simulates them; they are a post like the controls. */
  checkInPost() {
    const source = this.source();
    const id = source?.scenario.inputs.checkIn?.areaId;
    return source?.layout.layout.checkInAreas?.find((area) => area.id === id) ?? null;
  }
  isCheckIn(id = this.checkpointId) {
    return !!id && this.checkInPost()?.id === id;
  }
  /** Names of the people at the chosen post, as the scenario's team calls them. */
  team() {
    const source = this.source();
    if (!source) return [];
    const operators = source.scenario.inputs.operators ?? [];
    if (this.isCheckIn())
      return operators.filter((person) => person.role === 'checkIn').map((person) => person.name);
    const names = new Map(operators.map((person) => [person.id, person.name]));
    const slots = new Map(
      source.layout.layout.staff.map((member) => [member.id, member.displayName]),
    );
    return source.scenario.inputs.staffAllocations
      .filter((row) => row.checkpointId === this.checkpointId)
      .map((row) => names.get(row.staffMemberId) ?? slots.get(row.staffMemberId) ?? 'Operator');
  }
  currentCount(id = this.checkpointId) {
    if (this.isCheckIn(id)) return this.source()?.scenario.inputs.checkIn?.staffCount ?? 0;
    return (
      this.source()?.scenario.inputs.staffAllocations.filter((row) => row.checkpointId === id)
        .length ?? 0
    );
  }
  availableCount() {
    const source = this.source();
    if (!source) return 0;
    if (this.isCheckIn()) return this.checkInPost()!.deskCount;
    const checkpoint = source.layout.layout.checkpoints.find((row) => row.id === this.checkpointId);
    const allocated = new Set(
      source.scenario.inputs.staffAllocations.map((row) => row.staffMemberId),
    );
    // A scenario with its own team adds only its unassigned security operators, not the layout's spare slots.
    const operators = source.scenario.inputs.operators;
    const team =
      operators &&
      new Set(operators.filter((person) => person.role === 'security').map((person) => person.id));
    return (
      this.currentCount() +
      source.layout.layout.staff.filter(
        (row) =>
          row.terminalId === checkpoint?.terminalId &&
          !allocated.has(row.id) &&
          (!team || team.has(row.id)),
      ).length
    );
  }
  create() {
    const source = this.source();
    if (!source || this.busy()) return;
    if (
      !(
        this.isCheckIn() ||
        source.layout.layout.checkpoints.some((row) => row.id === this.checkpointId)
      ) ||
      !Number.isInteger(this.staffCount) ||
      this.staffCount < (this.isCheckIn() ? 1 : 0) ||
      this.staffCount > this.availableCount() ||
      !this.name.trim() ||
      this.name.length > 160 ||
      !Number.isInteger(this.timeLimitSeconds) ||
      this.timeLimitSeconds < 1 ||
      this.timeLimitSeconds > 3600
    ) {
      this.message.set(
        `Alege un post și ${this.isCheckIn() ? 1 : 0}–${this.availableCount()} persoane, un nume de 1–160 caractere și o limită de 1–3600 secunde.`,
      );
      return;
    }
    const payload = {
      revision: source.scenario.scenario.revision,
      name: this.name.trim(),
      checkpointId: this.checkpointId,
      staffCount: this.staffCount,
      timeLimitSeconds: this.timeLimitSeconds,
    };
    const key = JSON.stringify({ scenarioId: source.scenario.scenario.id, ...payload });
    if (this.command && this.command.key !== key) {
      this.message.set(
        'O comandă are rezultatul neconfirmat. Reverifică aceeași comandă înainte să schimbi parametrii.',
      );
      return;
    }
    if (!this.command) {
      const id = crypto.randomUUID();
      this.command = {
        key,
        id,
        scenarioId: source.scenario.scenario.id,
        body: { commandId: id, ...payload },
      };
      this.persistCommand();
    }
    this.retry();
  }
  retry() {
    const command = this.command;
    if (!command || this.busy()) return;
    this.watch.next(null);
    this.comparisonRequest?.unsubscribe();
    this.report.set(null);
    this.detail.set(null);
    this.comparing.set(false);
    this.comparedBatch = null;
    this.busy.set(true);
    this.message.set('');
    this.api
      .post<BatchDetails>(`scenarios/${command.scenarioId}/what-if`, command.body)
      .pipe(
        timeout(OPERATION_REQUEST_TIMEOUT_MS),
        finalize(() => this.busy.set(false)),
        takeUntilDestroyed(this.destroy),
      )
      .subscribe({
        next: (value) => {
          if (
            !this.belongs(value, command.id) ||
            value.variants.find((row) => row.position === 0)?.scenarioRevision !==
              command.body.revision
          ) {
            this.message.set(
              'Confirmarea nu corespunde comenzii what-if. Reverifică aceeași comandă.',
            );
            return;
          }
          this.clearCommand();
          this.scenarios.reload();
          this.open(value.batch.id, value);
          const { checkpointId, staffCount, revision } = command.body;
          this.change.set({
            zoneId: checkpointId,
            // A command restored after a reload may predate the loaded scenario, whose count would be wrong.
            before:
              this.source()?.scenario.scenario.revision === revision
                ? this.currentCount(checkpointId)
                : null,
            after: staffCount,
            unit: this.isCheckIn(checkpointId)
              ? ['ghișeu deschis', 'ghișee deschise']
              : ['persoană', 'persoane'],
          });
        },
        error: (error) => {
          if (error instanceof ApiError && [400, 404, 409, 422].includes(error.status))
            this.clearCommand();
          this.error(error);
        },
      });
  }
  open(id: string, initial: BatchDetails | null = null) {
    if ((this.busy() && initial === null) || this.pendingCommand()) return;
    if (initial && !this.belongs(initial, id)) {
      this.message.set('Perechea nu aparține scenariului selectat.');
      return;
    }
    this.comparisonRequest?.unsubscribe();
    this.comparing.set(false);
    this.report.set(null);
    this.change.set(null);
    this.detail.set(initial);
    this.comparedBatch = null;
    this.pollingError.set('');
    this.watch.next(id);
  }
  private belongs(value: BatchDetails, id: string) {
    if (
      !value ||
      value.batch?.id !== id ||
      !Array.isArray(value.variants) ||
      !Array.isArray(value.replications) ||
      !Array.isArray(value.seeds) ||
      value.variants.some((row) => !row || typeof row.id !== 'string') ||
      value.replications.some((row) => !row || typeof row.runId !== 'string')
    )
      return false;
    const baseline = value.variants.filter((row) => row.position === 0);
    return (
      value.batch.id === id &&
      baseline.length === 1 &&
      baseline[0].scenarioId === this.source()?.scenario.scenario.id
    );
  }
  paired(value: BatchDetails) {
    if (
      value.seeds.length !== 1 ||
      value.variants.length !== 2 ||
      value.replications.length !== 2 ||
      new Set(value.variants.map((row) => row.id)).size !== 2 ||
      new Set(value.replications.map((row) => row.runId)).size !== 2 ||
      ![0, 1].every((position) => value.variants.some((row) => row.position === position))
    )
      return false;
    return value.variants.every((variant) => {
      const matches = value.replications.filter((row) => row.variantId === variant.id);
      return (
        matches.length === 1 && matches[0].seedIndex === 0 && matches[0].seed === value.seeds[0]
      );
    });
  }
  roles() {
    const value = this.detail();
    if (!value || !this.paired(value)) return [];
    return [...value.variants]
      .sort((a, b) => a.position - b.position)
      .map((variant) => ({
        variant,
        role: variant.position === 0 ? 'Personalul actual' : 'Varianta nouă',
        replica: value.replications.find((row) => row.variantId === variant.id)!,
      }));
  }
  compare() {
    const value = this.detail(),
      roles = this.roles();
    if (
      !value ||
      roles.length !== 2 ||
      roles.some((row) => ['planned', 'pending', 'running'].includes(row.replica.runStatus))
    )
      return;
    const baseline = roles[0].replica.runId,
      candidate = roles[1].replica.runId,
      batchId = value.batch.id;
    this.comparisonRequest?.unsubscribe();
    this.comparing.set(true);
    this.report.set(null);
    this.comparedBatch = batchId;
    this.comparisonRequest = this.api
      .get<RunComparison>(`runs/${baseline}/compare/${candidate}`)
      .pipe(
        timeout(OPERATION_REQUEST_TIMEOUT_MS),
        finalize(() => this.comparing.set(false)),
        takeUntilDestroyed(this.destroy),
      )
      .subscribe({
        next: (report) => {
          if (
            this.detail()?.batch.id !== batchId ||
            report.baseline.id !== baseline ||
            report.candidate.id !== candidate
          ) {
            this.message.set('Raportul nu corespunde perechii selectate. Reîncarcă comparația.');
            return;
          }
          this.report.set(report);
          if (report.baseline.scenarioId === this.source()?.scenario.scenario.id)
            this.simulation.remember(report.baseline.scenarioId!, baseline);
        },
        error: (error) => this.error(error),
      });
  }
  cancel() {
    const value = this.detail();
    if (!value || this.busy() || !['pending', 'running'].includes(value.batch.status)) return;
    this.busy.set(true);
    this.api
      .post<BatchSummary>(`simulation-batches/${value.batch.id}/cancel`)
      .pipe(
        timeout(OPERATION_REQUEST_TIMEOUT_MS),
        finalize(() => this.busy.set(false)),
        takeUntilDestroyed(this.destroy),
      )
      .subscribe({
        next: () => this.watch.next(value.batch.id),
        error: (error) => this.error(error),
      });
  }
  private storageKey() {
    return `aerosynapse.what-if-command.v1.${this.source()?.scenario.scenario.id}`;
  }
  private persistCommand() {
    this.pendingCommand.set(true);
    try {
      localStorage.setItem(this.storageKey(), JSON.stringify(this.command));
    } catch {
      /* Optional persistence. */
    }
  }
  private clearCommand() {
    this.command = null;
    this.pendingCommand.set(false);
    try {
      localStorage.removeItem(this.storageKey());
    } catch {
      /* Optional persistence. */
    }
  }
  private restoreCommand() {
    try {
      const value = JSON.parse(localStorage.getItem(this.storageKey()) ?? 'null');
      if (
        !value ||
        !validRunId(value.id) ||
        value.body?.commandId !== value.id ||
        value.scenarioId !== this.source()?.scenario.scenario.id ||
        !Number.isSafeInteger(value.body.revision) ||
        value.body.revision < 1
      )
        return;
      const { commandId, ...parameters } = value.body;
      if (JSON.stringify({ scenarioId: value.scenarioId, ...parameters }) !== value.key) return;
      this.command = value;
      this.pendingCommand.set(true);
      this.message.set(
        'Comandă what-if restaurată. Reverifică aceeași comandă pentru a confirma perechea creată.',
      );
    } catch {
      /* Unavailable or corrupt storage must not block the page. */
    }
  }
  private error(error: unknown) {
    this.message.set(operationError(error));
  }
}

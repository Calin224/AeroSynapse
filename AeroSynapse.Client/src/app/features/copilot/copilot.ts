import { DecimalPipe } from '@angular/common';
import {
  afterNextRender,
  Component,
  computed,
  DestroyRef,
  ElementRef,
  inject,
  Injector,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { Observable, Subscription } from 'rxjs';
import { ApiClient } from '../../core/http/api-client';
import { ApiError } from '../../core/http/api-error.interceptor';
import { SimulationContext } from '../../core/simulation-context';
import { PickedRun, RunPicker } from '../../shared/ui/run-picker';
import { eventLabel } from '../operations/events.models';
import { RunDetails } from '../operations/operations.models';
import { CopilotEvidence } from './copilot-evidence';
import { QueueForecast, ToolName, ToolResult, validToolResult } from './copilot-tools.models';
import {
  CopilotAnswer,
  CopilotContext,
  CopilotScope,
  figure,
  modelLabel,
  uuid,
  validAnswer,
} from './copilot.models';

/** One question and its reply; a turn keeps the zone and minute it was asked about. */
interface Turn {
  id: number;
  prompt: string;
  scope: CopilotScope;
  zone: string;
  state: 'pending' | 'done' | 'failed';
  answer?: CopilotAnswer;
  tool?: ToolResult;
  error?: string;
}

const FORECAST_MINUTES = 15;
/** Whole-run questions a first-time operator can ask with one click; the backend suggests the same ones. */
const STARTERS = [
  'Rezumă simularea',
  'Unde a fost cea mai mare coadă și de ce?',
  'Câți pasageri au pierdut zborul?',
  'Ce ar trebui să schimb?',
];

@Component({
  selector: 'app-copilot',
  imports: [DecimalPipe, FormsModule, RunPicker, CopilotEvidence],
  templateUrl: './copilot.html',
  styleUrl: './copilot.css',
})
export class Copilot {
  private readonly api = inject(ApiClient);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly simulation = inject(SimulationContext);
  private readonly host: HTMLElement = inject(ElementRef).nativeElement;
  private readonly injector = inject(Injector);
  private contextRequest?: Subscription;
  private turnRequest?: Subscription;
  private generation = 0;
  private turnCount = 0;
  readonly context = signal<CopilotContext | null>(null);
  readonly turns = signal<Turn[]>([]);
  readonly loading = signal(false);
  readonly message = signal('');
  readonly choosing = signal(false);
  readonly busy = computed(() => this.turns().some((turn) => turn.state === 'pending'));
  readonly modelLabel = modelLabel;
  readonly figure = figure;
  readonly eventLabel = eventLabel;
  readonly forecastMinutes = FORECAST_MINUTES;
  readonly starters = STARTERS;
  scenarioId = '';
  runId = '';
  zoneId = '';
  minute = 0;
  question = '';

  constructor() {
    // The URL names the run; the picker only rewrites the URL.
    this.route.queryParamMap
      .pipe(takeUntilDestroyed())
      .subscribe((query) =>
        this.open(
          query.get('scenarioId') ?? '',
          query.get('runId') ?? '',
          query.get('zoneId') ?? '',
          query.get('minute'),
        ),
      );
    inject(DestroyRef).onDestroy(() => this.reset());
  }

  private reset() {
    ++this.generation;
    this.contextRequest?.unsubscribe();
    this.turnRequest?.unsubscribe();
    this.turns.set([]);
    this.loading.set(false);
    this.message.set('');
  }

  open(
    scenarioId: string,
    runId: string,
    requestedZone = '',
    requestedMinute: string | null = null,
  ) {
    this.reset();
    this.scenarioId = scenarioId;
    this.runId = runId;
    this.context.set(null);
    if (!runId) return;
    if (!uuid(scenarioId) || !uuid(runId)) {
      this.message.set('Linkul trebuie să conțină identificatori UUID valizi.');
      return;
    }
    const generation = this.generation;
    this.loading.set(true);
    this.contextRequest = this.api.get<CopilotContext>(`${this.path()}/context`).subscribe({
      next: (context) => {
        if (generation !== this.generation) return;
        this.loading.set(false);
        if (
          context.contractVersion !== '1.0' ||
          context.scenarioId !== scenarioId ||
          context.runId !== runId ||
          !Array.isArray(context.zones)
        ) {
          this.message.set('Datele primite nu corespund rulării alese. Reîncarcă pagina.');
          return;
        }
        const end = context.durationMinutes ?? 0,
          minute = Number(requestedMinute),
          known = (id?: string) => context.zones.some((zone) => zone.id === id),
          peak = context.busiest,
          busiest =
            peak && known(peak.zoneId) && Number.isInteger(peak.minute) && peak.minute >= 0 && peak.minute <= end
              ? peak
              : null;
        this.context.set(context);
        this.simulation.remember(scenarioId, runId);
        // Without a link, open where the queue was longest: the end of a run usually has none.
        this.zoneId = known(requestedZone)
          ? requestedZone
          : (busiest?.zoneId ?? context.zones[0]?.id ?? '');
        this.minute =
          requestedMinute !== null && Number.isInteger(minute) && minute >= 0 && minute <= end
            ? minute
            : busiest && this.zoneId === busiest.zoneId
              ? busiest.minute
              : end;
        if (requestedZone && requestedZone !== this.zoneId)
          this.message.set(
            `Zona din link nu aparține rulării, așa că am ales ${busiest ? 'zona cu cea mai mare coadă' : 'prima zonă'}.`,
          );
      },
      error: () => {
        if (generation !== this.generation) return;
        this.loading.set(false);
        this.message.set('Rularea nu poate fi încărcată. Alege altă rulare sau reîncearcă.');
      },
    });
  }

  pick(run: PickedRun) {
    this.choosing.set(false);
    if (run.runId === this.runId) return;
    if (run.scenarioId) {
      this.navigate(run.scenarioId, run.runId);
      return;
    }
    // A pasted runId names no scenario; the run itself does.
    this.contextRequest?.unsubscribe();
    this.contextRequest = this.api.get<RunDetails>(`runs/${run.runId}`).subscribe({
      next: (details) =>
        details.scenarioId
          ? this.navigate(details.scenarioId, run.runId)
          : this.message.set('Rularea introdusă nu are un scenariu asociat.'),
      error: () => this.message.set('Rularea introdusă nu a fost găsită.'),
    });
  }

  private navigate(scenarioId: string, runId: string) {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { scenarioId, runId },
      replaceUrl: true,
    });
  }

  ask(text?: string) {
    const question = (text ?? this.question).trim();
    if (!question || question.length > 1000) {
      this.message.set('Scrie o întrebare de cel mult 1000 de caractere.');
      return;
    }
    const turn = this.start(question);
    if (!turn) return;
    if (text === undefined) this.question = '';
    const { zoneId, minute } = turn.scope;
    this.track(
      turn,
      this.api.post<CopilotAnswer>(this.path(), { zoneId, minute, question }),
      (answer) => validAnswer(answer, turn.scope) && { answer },
    );
  }

  forecast() {
    if (!this.canForecast()) return;
    const turn = this.start(`Prognoza cozii pentru următoarele ${FORECAST_MINUTES} minute`);
    if (turn) this.tool(turn, 'forecast_queues', { horizonMinutes: FORECAST_MINUTES });
  }

  events() {
    const turn = this.start('Ce evenimente au afectat zona?');
    if (turn) this.tool(turn, 'inspect_zone', { question: 'Rezumă situația zonei.' });
  }

  zoneLabel() {
    return this.context()?.zones.find((zone) => zone.id === this.zoneId)?.code ?? '—';
  }

  /** True while the zone and minute are the run's busiest moment. */
  atBusiest() {
    const busiest = this.context()?.busiest;
    return !!busiest && busiest.zoneId === this.zoneId && busiest.minute === this.minute;
  }

  /** Last minute from which the whole forecast horizon still fits inside the run. */
  forecastLimit() {
    return (this.context()?.durationMinutes ?? -1) - FORECAST_MINUTES;
  }

  canForecast() {
    return this.minute <= this.forecastLimit();
  }

  enter(event: Event) {
    const key = event as KeyboardEvent;
    if (key.shiftKey || key.isComposing) return;
    key.preventDefault();
    this.ask();
  }

  /** Citation markers such as [queuedPassengers] are shown as figures, not inline. */
  plain(text: string) {
    return text.replace(/\s*\[[A-Za-z][\w.]*\]/g, '');
  }

  /** The answer as an operator reads it: each cited "value unit [id]" rounded the way people count. */
  say(answer: CopilotAnswer) {
    const escape = (text: string) => text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    return this.plain(
      answer.citations.reduce(
        (text, c) =>
          text.replace(
            new RegExp(`\\S+ ${escape(c.unit)} \\[${escape(c.id)}\\]`),
            figure(c.value, c.unit),
          ),
        answer.answer,
      ),
    );
  }

  /** Where the forecast queue starts, ends and peaks; the peak is worth naming only above both. */
  describe(forecast: QueueForecast) {
    const points = forecast.zone.points,
      now = forecast.zone.initialQueuePassengers,
      end = points[points.length - 1].queuePassengers,
      peak = points.reduce((top, point) =>
        point.queuePassengers > top.queuePassengers ? point : top,
      );
    return {
      now,
      end,
      peak,
      verb: end - now >= 0.5 ? 'crește la' : now - end >= 0.5 ? 'scade la' : 'rămâne la',
      showPeak: peak.queuePassengers - Math.max(now, end) >= 0.5,
    };
  }

  bar(value: number, max: number) {
    return max > 0 ? Math.max(2, (value / max) * 100) : 2;
  }

  explanation(turn: Turn) {
    return turn.answer?.explanation ?? turn.tool?.explanation;
  }

  private start(prompt: string): Turn | null {
    const context = this.context();
    if (!context || this.busy()) return null;
    const zone = context.zones.find((item) => item.id === this.zoneId);
    if (
      !zone ||
      !Number.isInteger(this.minute) ||
      this.minute < 0 ||
      this.minute > (context.durationMinutes ?? -1)
    ) {
      this.message.set('Alege o zonă și un minut din rulare.');
      return null;
    }
    const turn: Turn = {
      id: ++this.turnCount,
      prompt,
      scope: {
        scenarioId: context.scenarioId,
        runId: context.runId,
        zoneId: zone.id,
        minute: this.minute,
      },
      zone: `${zone.code} · ${zone.name}`,
      state: 'pending',
    };
    this.message.set('');
    this.turns.update((turns) => [...turns, turn]);
    this.reveal(turn.id);
    return turn;
  }

  private tool(turn: Turn, name: ToolName, args: object) {
    const { zoneId, minute } = turn.scope;
    const intent = name === 'forecast_queues' ? 'forecast' : 'diagnose';
    this.track(
      turn,
      this.api.post<ToolResult>(`${this.path()}/tools`, {
        intent,
        zoneId,
        minute,
        call: { name, arguments: args },
      }),
      (tool) => validToolResult(tool, turn.scope, name) && { tool },
    );
  }

  private track<T>(
    turn: Turn,
    request: Observable<T>,
    accept: (value: T) => Partial<Turn> | false,
  ) {
    const generation = this.generation;
    const settle = (patch: Partial<Turn>) => {
      if (generation !== this.generation) return;
      this.turns.update((turns) =>
        turns.map((item) => (item.id === turn.id ? { ...item, ...patch } : item)),
      );
      this.reveal(turn.id);
    };
    this.turnRequest = request.subscribe({
      next: (value) => {
        const patch = accept(value);
        settle(
          patch
            ? { ...patch, state: 'done' }
            : {
                state: 'failed',
                error: 'Răspunsul nu are surse valide pentru zona și minutul alese.',
              },
        );
      },
      error: (error: unknown) =>
        settle({
          state: 'failed',
          error:
            error instanceof ApiError
              ? error.message
              : 'Copilot nu poate răspunde acum. Încearcă din nou.',
        }),
    });
  }

  /** Keeps the newest exchange in view without taking focus from the composer. */
  private reveal(id: number) {
    afterNextRender(
      () => this.host.querySelector(`[data-turn="${id}"]`)?.scrollIntoView?.({ block: 'nearest' }),
      { injector: this.injector },
    );
  }

  private path() {
    return `scenarios/${this.scenarioId}/runs/${this.runId}/copilot`;
  }
}

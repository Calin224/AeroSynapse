import { Component, DestroyRef, inject, signal } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Subscription } from 'rxjs';
import { ApiClient } from '../../core/http/api-client';
import { SimulationContext } from '../../core/simulation-context';
import { PreparedReplay, ReplayEnvelope, prepareReplay } from './replay.models';
import { ReplayPlayer } from './replay-player';
import { RunPicker } from '../../shared/ui/run-picker';

@Component({
  selector: 'app-replay-page',
  imports: [ReplayPlayer, RunPicker],
  templateUrl: './replay-page.html',
  styleUrl: './replay-page.css',
})
export class ReplayPage {
  private readonly api = inject(ApiClient);
  private readonly simulation = inject(SimulationContext);
  private readonly destroy = inject(DestroyRef);
  private pending?: Subscription;
  private generation = 0;
  readonly prepared = signal<PreparedReplay | null>(null);
  readonly busy = signal(false);
  readonly message = signal('');
  runId = '';

  constructor() {
    inject(ActivatedRoute)
      .queryParamMap.pipe(takeUntilDestroyed())
      .subscribe((query) => {
        const id = query.get('runId');
        if (id) this.open(id);
      });
    this.destroy.onDestroy(() => {
      ++this.generation;
      this.pending?.unsubscribe();
    });
  }

  open(id: string) {
    this.pending?.unsubscribe();
    const generation = ++this.generation;
    this.prepared.set(null);
    this.message.set('');
    this.busy.set(false);
    this.runId = id;
    if (!/^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/i.test(id)) {
      this.message.set('Identificator de rulare invalid.');
      return;
    }
    this.busy.set(true);
    this.pending = this.api.get<ReplayEnvelope>(`runs/${id}/replay`).subscribe({
      next: (envelope) => void this.accept(envelope, id, generation),
      error: (error) => this.fail(error, generation),
    });
  }

  private async accept(envelope: ReplayEnvelope, id: string, generation: number) {
    try {
      const replay = await prepareReplay(envelope, id);
      if (!this.current(generation)) return;
      this.prepared.set(replay);
      this.simulation.remember(replay.data.captured.scenario.scenario.id, replay.data.run.id);
      this.busy.set(false);
    } catch (error) {
      this.fail(error, generation);
    }
  }

  private fail(error: unknown, generation: number) {
    if (!this.current(generation)) return;
    this.busy.set(false);
    this.message.set(error instanceof Error ? error.message : 'Replay-ul nu a putut fi încărcat.');
  }

  private current(generation: number) {
    return generation === this.generation && !this.destroy.destroyed;
  }
}

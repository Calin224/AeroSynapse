import { DOCUMENT } from '@angular/common';
import { computed, inject, Injectable, signal } from '@angular/core';
import { Router, UrlTree } from '@angular/router';
import { SessionState } from './auth/session-state';

interface SimulationSelection {
  scenarioId: string;
  runId: string | null;
}

const uuid = (value: unknown): value is string =>
  typeof value === 'string' &&
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value);

/** One selection for simulation and analysis, scoped to this browser tab, user and tenant. */
@Injectable({ providedIn: 'root' })
export class SimulationContext {
  private readonly session = inject(SessionState);
  private readonly document = inject(DOCUMENT);
  private readonly saved = signal<{ key: string; value: SimulationSelection } | null>(null);
  private readonly key = computed(() => {
    const session = this.session.session();
    return session
      ? `aerosynapse.simulation-context.v1:${JSON.stringify([session.tenantId ?? 'demo', session.username])}`
      : null;
  });
  readonly current = computed<SimulationSelection | null>(() => {
    const key = this.key();
    if (!key) return null;
    if (this.saved()?.key === key) return this.saved()!.value;
    try {
      const value = JSON.parse(this.document.defaultView?.sessionStorage.getItem(key) ?? 'null');
      return value && uuid(value.scenarioId) && (value.runId === null || uuid(value.runId))
        ? { scenarioId: value.scenarioId, runId: value.runId }
        : null;
    } catch {
      return null;
    }
  });

  /** Call after accepting a scenario or run from the API, rather than trusting URL parameters. */
  remember(scenarioId: string, runId?: string | null): void {
    const key = this.key();
    if (!key || !uuid(scenarioId) || (runId != null && !uuid(runId))) return;
    const previous = this.current();
    const value = {
      scenarioId,
      runId:
        runId === undefined ? (previous?.scenarioId === scenarioId ? previous.runId : null) : runId,
    };
    this.saved.set({ key, value });
    try {
      this.document.defaultView?.sessionStorage.setItem(key, JSON.stringify(value));
    } catch {
      // Navigation still retains the in-memory selection when storage is unavailable.
    }
  }

  /** Fill only missing context on entry; keep explicit deep links and unrelated filters intact. */
  restoreUrl(url: string, router: Router): UrlTree | null {
    const selected = this.current();
    if (!selected) return null;
    const tree = router.parseUrl(url);
    const path = tree.root.children['primary']?.segments.map((segment) => segment.path).join('/');
    const scenarioPages = [
      'control-tower',
      'scenario',
      'operations',
      'compare',
      'batches',
      'copilot',
      'operator-workflow',
    ];
    const runPages = [
      'control-tower',
      'scenario',
      'operations',
      'copilot',
      'operator-workflow',
    ];
    const params = { ...tree.queryParams };
    if (path === 'replay' || path === 'operational-report') {
      const runKey = path === 'replay' ? 'runId' : 'baselineRunId';
      if (params[runKey] || params['scenarioId'] || !selected.runId) return null;
      params[runKey] = selected.runId;
    } else if (scenarioPages.includes(path ?? '')) {
      // A run-only deep link must resolve its own scenario, without mixing in a previous one.
      if (params['runId'] && !params['scenarioId']) return null;
      if (!params['scenarioId']) params['scenarioId'] = selected.scenarioId;
      if (
        runPages.includes(path!) &&
        !params['runId'] &&
        params['scenarioId'] === selected.scenarioId &&
        selected.runId
      )
        params['runId'] = selected.runId;
    } else {
      // In particular, opening Scenario Studio without an ID must still create a new scenario.
      return null;
    }
    if (JSON.stringify(params) === JSON.stringify(tree.queryParams)) return null;
    tree.queryParams = params;
    return tree;
  }
}

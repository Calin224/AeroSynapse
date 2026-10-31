import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { apiErrorInterceptor } from '../../core/http/api-error.interceptor';
import { ParetoPanel } from './pareto-panel';
import type { ParetoAnalysis } from './pareto.models';
import type { StaffingEvaluationDetails } from './staffing-evaluation.models';

function evaluation(id = 'eval-1') {
  const pareto: ParetoAnalysis = { status: 'tradeoffs_available',
    options: { weights: { time: 40, cost: 40, risk: 20 }, normalization: { timeMinutes: 10, costPoints: 1000, riskFraction: .1 },
      operationalBudget: 1000, staffMinuteCost: 1, maxMeanWaitMinutes: 20, maxP95WaitMinutes: 30, maxBoardingRisk: 1 },
    formula: 'S = (wT*T/Tref + wC*C/Cref + wR*R/Rref)/(wT+wC+wR)',
    planning: { formula: 'sum(round(1000000 * weighted proxies))', status: 'exhausted', wallTimeSeconds: .02,
      request: { durationMinutes: 60, options: {} as ParetoAnalysis['options'], constraints: { scenarioId: 'scenario-1', scenarioRevision: 1 } },
      solves: [{ profile: 'selected', weights: { time: 40, cost: 40, risk: 20 }, solverStatus: 'OPTIMAL', attempted: true,
        scaledObjective: 250000, bestBound: 250000, proxyTimeMinutes: 12, proxyRisk: .1, operationalCost: 120,
        assignments: [{ checkpointId: 'cp-1', staffCount: 2 }], wallTimeSeconds: .02 }] },
    points: [{ runId: id + '-b', position: 0, feasible: true, exclusions: [], onFrontier: true, dominatedBy: [],
        timeMinutes: 10, costPoints: 120, riskFraction: .2, normalizedTime: 1, normalizedCost: .12, normalizedRisk: 2, weightedScore: .848 },
      { runId: id + '-c', position: 1, feasible: true, exclusions: [], onFrontier: true, dominatedBy: [],
        timeMinutes: 5, costPoints: 200, riskFraction: .1, normalizedTime: .5, normalizedCost: .2, normalizedRisk: 1, weightedScore: .48 },
      { runId: id + '-x', position: 2, feasible: false, exclusions: ['operational_budget'], onFrontier: false, dominatedBy: [],
        timeMinutes: 1, costPoints: 2000, riskFraction: 0, normalizedTime: .1, normalizedCost: 2, normalizedRisk: 0, weightedScore: null }],
    frontierRunIds: [id + '-b', id + '-c'], preferredRunId: id + '-c',
    tradeoffs: [{ firstRunId: id + '-b', secondRunId: id + '-c', timeDeltaMinutes: -5, costDeltaPoints: 80, riskDeltaFraction: -.1 }],
    assumptions: ['Frontieră aproximativă cu o singură replicație.', 'Nicio alegere nu modifică automat scenariul.'] };
  pareto.planning.request.options = pareto.options;
  return { batch: { batch: { id } }, pareto } as StaffingEvaluationDetails;
}

describe('Approximate Pareto frontier', () => {
  let http: HttpTestingController;
  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(withInterceptors([apiErrorInterceptor])), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => { http.verify(); vi.unstubAllGlobals(); vi.restoreAllMocks(); vi.useRealTimers(); });
  function component() {
    const fixture = TestBed.createComponent(ParetoPanel); fixture.componentRef.setInput('evaluation', evaluation());
    fixture.componentRef.setInput('active', true); fixture.detectChanges(); return fixture;
  }
  it('renders explicit tradeoffs, raw units, normalization, solver proof and excludes infeasible points from the plot', () => {
    const fixture = component(); const text = fixture.nativeElement.textContent;
    for (const phrase of ['frontieră Pareto', 'Tref 10 min', 'Cref 1000 puncte', 'Rref 0.1', 'Δ timp -5 min',
      'Δ cost 80 puncte', 'Δ risc -10 puncte procentuale', 'Buget operațional depășit', 'OPTIMAL', '250.000', 'scenariul']) expect(text).toContain(phrase);
    expect(fixture.nativeElement.querySelectorAll('circle').length).toBe(2);
    expect(text).toContain('eval-1-x'); http.expectNone(request => request.method === 'POST'); fixture.destroy();
  });
  it('compares saved frontiers through the API and overlays only compatible observations', () => {
    const fixture = component(); const view = fixture.componentInstance; view.compareId = 'eval-2'; view.compare();
    http.expectOne('/api/staffing-evaluations/eval-1/pareto/compare/eval-2').flush({ comparable: true, issues: [], left: evaluation().pareto,
      right: evaluation('eval-2').pareto, unionFrontierRunIds: ['eval-1-c', 'eval-2-c'], comparisons: [{ leftRunId: 'eval-1-c', rightRunId: 'eval-2-c', relation: 'equivalent' }] });
    fixture.detectChanges(); expect(fixture.nativeElement.querySelectorAll('circle').length).toBe(4);
    expect(fixture.nativeElement.textContent).toContain('frontieră comună: 2');
    view.compare(); http.expectOne('/api/staffing-evaluations/eval-1/pareto/compare/eval-2').flush({ comparable: false, issues: ['cost_model_differs'],
      left: evaluation().pareto, right: evaluation('eval-2').pareto, unionFrontierRunIds: [], comparisons: [] }); fixture.detectChanges();
    expect(fixture.nativeElement.querySelectorAll('circle').length).toBe(2); expect(fixture.nativeElement.textContent).toContain('Frontiere incompatibile'); fixture.destroy();
  });
  it('reports missing tradeoffs and no feasible solutions without fabricating alternatives', () => {
    const fixture = component(); const value = evaluation(); value.pareto!.status = 'no_tradeoff_observed'; value.pareto!.tradeoffs = [];
    fixture.componentRef.setInput('evaluation', value); fixture.detectChanges(); expect(fixture.nativeElement.textContent).toContain('Nu au fost observate două soluții');
    value.pareto = { ...value.pareto!, status: 'no_feasible_solution', points: [], frontierRunIds: [], preferredRunId: null };
    fixture.componentRef.setInput('evaluation', { ...value }); fixture.detectChanges(); expect(fixture.nativeElement.querySelectorAll('circle').length).toBe(0);
    expect(fixture.nativeElement.textContent).toContain('Nicio soluție fezabilă'); fixture.destroy();
  });
  it('cancels obsolete comparisons and clears the previous report when changing evaluation', () => {
    const fixture = component(); fixture.componentInstance.compareId = 'eval-2'; fixture.componentInstance.compare();
    const pending = http.expectOne('/api/staffing-evaluations/eval-1/pareto/compare/eval-2');
    fixture.componentRef.setInput('evaluation', evaluation('eval-3')); fixture.detectChanges();
    expect(pending.cancelled).toBe(true); expect(fixture.componentInstance.comparison()).toBeNull(); expect(fixture.componentInstance.busy()).toBe(false); fixture.destroy();
  });
});

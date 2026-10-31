import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RunComparison } from './comparison.models';
import { RunComparisonReport } from './run-comparison-report';

const report = (): RunComparison => {
  const provenance = {
    hashVersion: 'sha256-tree-v1',
    inputHash: 'a',
    resultHash: 'b',
    engineVersion: 'simpy-kpi-1.0',
    engineBuild: 'build',
    engineMilliseconds: 1,
    executionMilliseconds: 2,
    correlationId: 'trace',
  };
  return {
    baseline: {
      id: 'baseline-run',
      status: 'completed',
      resultsValid: true,
      scenarioRevision: 1,
      provenance,
      failureReason: null,
    },
    candidate: {
      id: 'candidate-run',
      status: 'completed',
      resultsValid: true,
      scenarioRevision: 1,
      provenance,
      failureReason: null,
    },
    inputsMatch: false,
    versionsMatch: true,
    resultsMatch: false,
    metricsTruncated: false,
    comparability: {
      comparable: true,
      populationMatches: true,
      baselinePopulationHash: 'p',
      candidatePopulationHash: 'p',
      issues: [],
    },
    metrics: [
      {
        zoneId: 'cp1',
        name: 'meanWaitMinutes',
        unit: 'minutes',
        baseline: 10,
        candidate: 5,
        delta: -5,
        percentChange: -50,
      },
    ],
  };
};
describe('Baseline and what-if report', () => {
  beforeEach(() => TestBed.configureTestingModule({ providers: [provideRouter([])] }));
  function component(value = report()) {
    const fixture = TestBed.createComponent(RunComparisonReport);
    fixture.componentRef.setInput('report', value);
    fixture.detectChanges();
    return fixture;
  }
  it('shows a plain-language summary without presenting mismatched measurements as a difference', () => {
    const value = report();
    value.metrics[0].baselineRecordedAtUtc = '2026-10-16T10:00:00Z';
    value.metrics[0].candidateRecordedAtUtc = '2026-10-16T10:01:00Z';
    const fixture = component(value);
    fixture.componentRef.setInput('simple', true);
    fixture.componentRef.setInput('zoneNames', { cp1: 'Control principal' });
    fixture.detectChanges();
    const summary = fixture.nativeElement.querySelector('section[aria-label="Ce s-a schimbat"]');
    expect(summary.textContent).toContain('Așteptare medie');
    expect(summary.textContent).toContain('Control principal');
    expect(summary.textContent).toContain('Indisponibilă');
    expect(summary.textContent).not.toContain('-5');
    expect(fixture.nativeElement.querySelector('details')).toBeNull();
    expect(fixture.nativeElement.textContent).not.toContain('Raport detaliat');
  });
  it('explains inactive controls and points to controls with processed passengers only when evidence is comparable', () => {
    const value = report();
    const metric = (zoneId: string, name: string, baseline: number, candidate: number) => ({
      zoneId,
      name,
      unit: 'passengers',
      baseline,
      candidate,
      delta: candidate - baseline,
    });
    value.metrics.push(metric('cp2', 'processedPassengers', 0, 0));
    value.metrics.push(metric('cp2', 'maximumQueue', 0, 0));
    value.metrics.push(metric('cp1', 'processedPassengers', 250, 250));
    value.metrics.push(metric('cp3', 'processedPassengers', 0, 0));
    // Check-in desks appear in the table, but staffing advice targets security controls only.
    value.metrics.push({
      ...metric('desks', 'processedPassengers', 240, 240),
      zoneType: 'checkIn',
    });
    const fixture = component(value);
    fixture.componentRef.setInput('simple', true);
    fixture.componentRef.setInput('zoneNames', {
      cp1: 'Control 1',
      cp2: 'Control 2',
      cp3: 'Control 3',
      desks: 'Check-in',
    });
    fixture.detectChanges();
    const insight = fixture.nativeElement.querySelector('.insight').textContent;
    expect(insight).toContain('Control 2');
    expect(insight).toContain('Control 1');
    expect(insight).not.toContain('Control 3');
    expect(insight).not.toContain('Check-in');
    expect(fixture.nativeElement.textContent).not.toContain('Zonă fără nume');
    value.comparability!.comparable = false;
    fixture.componentRef.setInput('report', { ...value });
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('.insight')).toBeNull();
  });
  it('labels both run IDs and displays a signed descriptive difference without declaring a winner', () => {
    const fixture = component(),
      text = fixture.nativeElement.textContent;
    expect(text).toContain('candidate-run');
    expect(text).toContain('-50%');
  });
  it.each([0, -1])(
    'never renders a percentage for baseline %s, even if an invalid percentage was supplied',
    (baseline) => {
      const value = report();
      value.metrics[0] = {
        ...value.metrics[0],
        baseline,
        delta: 5 - baseline,
        percentChange: 999,
        unavailableReason: 'baseline_not_positive',
      };
      const fixture = component(value),
        view = fixture.componentInstance;
      expect(view.percent(value.metrics[0])).toBeNull();
      expect(view.delta(value.metrics[0])).toBe(5 - baseline);
      expect(fixture.nativeElement.textContent).not.toContain('999%');
      expect(fixture.nativeElement.textContent).toContain('baseline zero sau negativ');
    },
  );
  it.each(['missing', 'time', 'nonfinite'])('hides derived values for %s observations', (kind) => {
    const value = report();
    if (kind === 'missing') value.metrics[0].candidate = null;
    if (kind === 'time') {
      value.metrics[0].baselineRecordedAtUtc = '2026-10-16T10:00:00Z';
      value.metrics[0].candidateRecordedAtUtc = '2026-10-16T10:01:00Z';
    }
    if (kind === 'nonfinite') value.metrics[0].candidate = Infinity;
    const view = component(value).componentInstance;
    expect(view.delta(value.metrics[0])).toBeNull();
    expect(view.percent(value.metrics[0])).toBeNull();
  });
  it.each(['population', 'build', 'same-run', 'unknown', 'failed'])(
    'flags %s incompatibility and keeps individual values separate',
    (kind) => {
      const value = report();
      if (kind === 'population')
        value.comparability = {
          ...value.comparability!,
          comparable: false,
          populationMatches: false,
          issues: [
            { code: 'population_mismatch', message: 'Populații diferite.', severity: 'blocking' },
          ],
        };
      if (kind === 'build') value.versionsMatch = false;
      if (kind === 'same-run') value.candidate.id = value.baseline.id;
      if (kind === 'unknown') value.comparability = null;
      if (kind === 'failed') {
        value.candidate.resultsValid = false;
        value.candidate.status = 'failed';
        value.candidate.failureReason = 'engine_timeout';
      }
      const fixture = component(value),
        view = fixture.componentInstance;
      expect(view.comparable()).toBe(false);
      expect(view.delta(value.metrics[0])).toBeNull();
      expect(view.percent(value.metrics[0])).toBeNull();
      expect(fixture.nativeElement.textContent).toContain('Rulări necomparabile');
      expect(fixture.nativeElement.textContent).not.toContain('-50%');
    },
  );
  it('keeps a valid 0% and shows differences in completed wait populations as a caveat', () => {
    const value = report();
    value.metrics[0].delta = 0;
    value.metrics[0].percentChange = 0;
    value.comparability!.issues = [
      {
        code: 'wait_population_differs',
        message: 'Numărul serviciilor terminate diferă.',
        severity: 'warning',
      },
    ];
    const fixture = component(value);
    expect(fixture.componentInstance.comparable()).toBe(true);
    expect(fixture.nativeElement.textContent).toContain('0%');
    expect(fixture.nativeElement.textContent).toContain('Numărul serviciilor terminate diferă.');
  });
});

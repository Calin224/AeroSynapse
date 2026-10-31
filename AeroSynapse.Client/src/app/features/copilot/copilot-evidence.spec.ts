import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { CopilotEvidence } from './copilot-evidence';
import { CopilotExplanation, validExplanation } from './copilot-explanation.models';

const run = '20000000-0000-0000-0000-000000000001';
const candidate = '20000000-0000-0000-0000-000000000002';
const scope = {
  scenarioId: '10000000-0000-0000-0000-000000000001',
  runId: run,
  zoneId: '30000000-0000-0000-0000-000000000001',
  minute: 60,
};
const evidence = (): CopilotExplanation => ({
  contractVersion: '1.0',
  disclosure: 'DEMO. Nu există conectare la sisteme aeroportuare reale.',
  summary: 'Nu există candidat superior validat.',
  sourceKind: 'tool_output',
  outputHash: 'a'.repeat(64),
  facts: [
    {
      id: 'mean',
      label: 'Așteptare medie',
      value: 0,
      unit: 'minute',
      runId: run,
      zoneId: scope.zoneId,
      sourcePath: '/evaluation/variants/0/metrics/meanWaitMinutes',
    },
  ],
  comparisons: [
    {
      metric: 'meanWaitMinutes',
      unit: 'minute',
      baselineRunId: run,
      candidateRunId: candidate,
      before: 0,
      after: 0,
      delta: 0,
      percent: null,
      percentUnavailableReason: 'nonpositive_baseline',
      beforeSource: '/evaluation/variants/0/metrics/meanWaitMinutes',
      afterSource: '/evaluation/variants/1/metrics/meanWaitMinutes',
    },
  ],
  runs: [
    { runId: run, role: 'Baseline' },
    { runId: candidate, role: 'Candidat' },
  ],
  outcomes: [
    {
      runId: candidate,
      status: 'unchanged',
      reasons: ['KPI-urile nu arată o îmbunătățire observată.'],
    },
  ],
  limitations: ['Comparația nu demonstrează semnificație statistică.'],
});
describe('Explainable Copilot sources', () => {
  it('links every cited run, explains missing percentages and shows non-improvement', async () => {
    TestBed.configureTestingModule({ imports: [CopilotEvidence], providers: [provideRouter([])] });
    const fixture = TestBed.createComponent(CopilotEvidence);
    fixture.componentRef.setInput('explanation', evidence());
    await fixture.whenStable();
    const element: HTMLElement = fixture.nativeElement;
    expect(element.textContent).toContain('Baseline zero sau negativ');
    expect(element.textContent).toContain('nu arată o îmbunătățire');
    expect(element.textContent).not.toContain('0%');
    expect(element.querySelector(`a[href="/replay?runId=${run}"]`)).not.toBeNull();
    expect(element.querySelector(`a[href="/replay?runId=${candidate}"]`)).not.toBeNull();
    expect(element.querySelector('a[href*="candidateRunId="]')).not.toBeNull();
    expect(element.textContent).toContain('/evaluation/variants/0/metrics/meanWaitMinutes');
  });
  it('rejects a percentage over a zero baseline, foreign run references and nonfinite values', () => {
    expect(validExplanation(evidence(), [run, candidate])).toBe(true);
    const zero = evidence();
    zero.comparisons[0].percent = 0;
    expect(validExplanation(zero)).toBe(false);
    expect(validExplanation(evidence(), [run])).toBe(false);
    const invalid = evidence();
    invalid.facts[0].value = NaN;
    expect(validExplanation(invalid)).toBe(false);
  });
});

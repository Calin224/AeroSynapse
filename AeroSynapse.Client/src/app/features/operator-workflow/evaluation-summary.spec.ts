import { TestBed } from '@angular/core/testing';
import { EvaluationSummaryView } from './evaluation-summary';
import { evaluationSummary } from './workflow.models';
import { ids, workflowEvaluation } from './workflow.testing';

describe('Evaluation summary', () => {
  it('shows the verdict, each checkpoint now and proposed, and every candidate', () => {
    const fixture = TestBed.createComponent(EvaluationSummaryView);
    const result = evaluationSummary(workflowEvaluation())!;
    fixture.componentRef.setInput('result', result);
    fixture.detectChanges();
    const element: HTMLElement = fixture.nativeElement;
    expect(element.querySelector('.verdict')?.getAttribute('data-tone')).toBe('better');
    expect(element.querySelector('.verdict h3')?.textContent).toContain(result.headline);
    expect(element.querySelector('.figure')?.textContent?.trim()).toBe('−50%');
    expect(element.querySelectorAll('.plan-row')).toHaveLength(result.moves.length);
    expect(element.querySelectorAll('app-outcome-cards')).toHaveLength(1);
    const candidates = element.querySelectorAll('.candidates > li');
    expect(candidates).toHaveLength(1);
    expect(candidates[0].classList).toContain('recommended');
    expect(candidates[0].querySelector('.badge')?.textContent).toBe('Recomandată');
    expect(result.candidates[0].runId).toBe(ids.otherRun);
  });
});

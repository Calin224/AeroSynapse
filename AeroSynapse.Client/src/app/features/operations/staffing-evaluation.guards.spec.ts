import { approvalProblem, evaluationProblem } from './staffing-evaluation.guards';
import {
  ids,
  towerFixture,
  workflowEvaluation,
  workflowReview,
} from '../operator-workflow/workflow.testing';

describe('Workflow evaluation and approval invariants', () => {
  it('accepts a validated pair while preserving the candidate scenario identity', () => {
    const value = workflowEvaluation();
    expect(value.variants[1].scenarioId).not.toBe(ids.scenario);
    expect(evaluationProblem(value, towerFixture().scenario)).toBe('');
    expect(approvalProblem(value, towerFixture().scenario, workflowReview())).toBe('');
  });
  it('rejects foreign evaluation IDs, source scenarios, revisions and seeds', () => {
    const value = workflowEvaluation(),
      scenario = towerFixture().scenario;
    expect(evaluationProblem(value, scenario, ids.otherRun)).toBeTruthy();
    value.planning.request.constraints.scenarioId = ids.mark;
    expect(evaluationProblem(value, scenario)).toBeTruthy();
    value.planning.request.constraints.scenarioId = ids.scenario;
    value.planning.request.constraints.scenarioRevision++;
    expect(evaluationProblem(value, scenario)).toBeTruthy();
    value.planning.request.constraints.scenarioRevision--;
    value.batch.seeds = [1];
    expect(evaluationProblem(value, scenario)).toBeTruthy();
  });
  it.each(['pending', 'running', 'cancelled', 'timed_out', 'failed'])(
    'blocks approval of %s evaluation results',
    (status) => {
      const value = workflowEvaluation();
      value.batch.batch.status = status;
      expect(approvalProblem(value, towerFixture().scenario)).toBeTruthy();
    },
  );
  it('blocks invalid, incomplete, cancelled or mismatched baseline/candidate results', () => {
    const scenario = towerFixture().scenario;
    for (const change of [
      (v: ReturnType<typeof workflowEvaluation>) => (v.batch.replications[1].resultsValid = false),
      (v: ReturnType<typeof workflowEvaluation>) =>
        (v.batch.replications[0].remoteCancellationPending = true),
      (v: ReturnType<typeof workflowEvaluation>) => (v.variants[1].metrics = null),
      (v: ReturnType<typeof workflowEvaluation>) => (v.variants[1].assessment.status = 'rejected'),
      (v: ReturnType<typeof workflowEvaluation>) => (v.batch.variants[1].scenarioId = ids.scenario),
      (v: ReturnType<typeof workflowEvaluation>) => (v.recommendedRunId = null),
      (v: ReturnType<typeof workflowEvaluation>) => (v.batch.metricsTruncated = true),
    ]) {
      const value = workflowEvaluation();
      change(value);
      expect(approvalProblem(value, scenario)).toBeTruthy();
    }
    const review = workflowReview();
    review.candidateRunId = ids.run;
    expect(approvalProblem(workflowEvaluation(), scenario, review)).toContain('perechii');
  });
  it('blocks approval for a non-demo or archived source', () => {
    const scenario = towerFixture().scenario;
    scenario.scenario.isSynthetic = false;
    expect(approvalProblem(workflowEvaluation(), scenario)).toBeTruthy();
    scenario.scenario.isSynthetic = true;
    scenario.scenario.deletedAtUtc = '2026-10-20T00:00:00Z';
    expect(approvalProblem(workflowEvaluation(), scenario)).toBeTruthy();
  });
});

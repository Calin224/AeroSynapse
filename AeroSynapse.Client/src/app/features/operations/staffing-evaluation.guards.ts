import type { ScenarioDetails } from './operations.models';
import type { RecommendationReview } from './recommendation-review.models';
import type { StaffingEvaluationDetails } from './staffing-evaluation.models';

/** A candidate has its own copied scenario. Only the baseline belongs to the source. */
export function evaluationIdentityProblem(
  value: StaffingEvaluationDetails,
  scenario: ScenarioDetails,
  expectedId?: string,
): string {
  const constraints = value.planning.request.constraints;
  const baseline = value.variants.filter((row) => row.position === 0);
  if (
    (expectedId && value.batch.batch.id !== expectedId) ||
    constraints.scenarioId !== scenario.scenario.id ||
    baseline.length !== 1 ||
    baseline[0].scenarioId !== scenario.scenario.id
  ) {
    return 'Evaluarea nu aparține scenariului sau comenzii selectate.';
  }
  return '';
}

export function evaluationProblem(
  value: StaffingEvaluationDetails,
  scenario: ScenarioDetails,
  expectedId?: string,
): string {
  const identity = evaluationIdentityProblem(value, scenario, expectedId);
  if (identity) return identity;
  const constraints = value.planning.request.constraints;
  if (
    constraints.scenarioRevision !== scenario.scenario.revision ||
    value.batch.seeds.length !== 1 ||
    value.batch.seeds[0] !== scenario.inputs.seed
  ) {
    return 'Evaluarea folosește altă revizie sau alt seed. Simulează și evaluează configurația curentă.';
  }
  return '';
}

export function approvalProblem(
  value: StaffingEvaluationDetails,
  scenario: ScenarioDetails,
  review?: RecommendationReview,
): string {
  const problem = evaluationProblem(value, scenario);
  if (problem) return problem;
  if (!scenario.scenario.isSynthetic || scenario.scenario.deletedAtUtc)
    return 'Aprobarea este permisă numai pentru un scenariu DEMO activ.';
  if (
    value.batch.batch.status !== 'completed' ||
    !value.batch.isSynthetic ||
    value.batch.batch.cancellationRequested ||
    value.batch.metricsTruncated ||
    !value.recommendedRunId
  ) {
    return 'Evaluarea nu este finalizată și validată cu un candidat superior. Aprobarea este blocată.';
  }
  const baseline = value.variants.find((row) => row.position === 0)!;
  const candidate = value.variants.find(
    (row) => row.runId === value.recommendedRunId && row.position > 0,
  );
  if (
    !candidate ||
    !['improved', 'tradeoff'].includes(candidate.assessment.status) ||
    baseline.runId === candidate.runId
  ) {
    return 'Candidatul nu este eligibil pentru aprobare.';
  }
  for (const variant of [baseline, candidate]) {
    const replication = value.batch.replications.find(
      (row) => row.runId === variant.runId && row.variantId === variant.variantId,
    );
    const captured = value.batch.variants.find(
      (row) => row.id === variant.variantId && row.scenarioId === variant.scenarioId,
    );
    if (
      variant.runStatus !== 'completed' ||
      !variant.metrics ||
      !captured ||
      !replication?.resultsValid ||
      captured.layoutVersionId !== scenario.scenario.layoutVersionId ||
      (variant.position === 0 && captured.scenarioRevision !== scenario.scenario.revision) ||
      replication.seed !== scenario.inputs.seed ||
      replication.status !== 'completed' ||
      replication.runStatus !== 'completed' ||
      replication.remoteCancellationPending
    ) {
      return 'Perechea baseline / candidat nu are rezultate complete și valide.';
    }
  }
  if (
    review &&
    (review.evaluationId !== value.batch.batch.id ||
      review.scenarioId !== scenario.scenario.id ||
      review.scenarioRevision !== scenario.scenario.revision ||
      review.baselineRunId !== baseline.runId ||
      review.candidateRunId !== candidate.runId)
  ) {
    return 'Recomandarea nu corespunde perechii de rulări comparate.';
  }
  return '';
}

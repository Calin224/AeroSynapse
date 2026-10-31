import { towerFixture, ids } from '../control-tower/tower.testing';
import type {
  StaffingEvaluationDetails,
  StaffingEvaluatedVariant,
} from '../operations/staffing-evaluation.models';
import type { RecommendationReview } from '../operations/recommendation-review.models';

export { towerFixture, ids };
export const evaluationId = '80000000-0000-0000-0000-000000000001';
export const baselineRunId = '90000000-0000-0000-0000-000000000001';
export function workflowFixture() {
  const data = towerFixture();
  const gate = 'a0000000-0000-0000-0000-000000000001',
    flight = 'a0000000-0000-0000-0000-000000000002',
    route = 'a0000000-0000-0000-0000-000000000003';
  data.layout.layout.gates.push({
    id: gate,
    code: 'G-1',
    name: 'Test gate',
    capacity: 20,
    terminalId: ids.scene,
  });
  data.layout.layout.routes.push({
    id: route,
    code: 'R-1',
    fromCheckpointId: ids.cp,
    toGateId: gate,
    terminalId: ids.scene,
  });
  data.scenario.inputs.flights.push({
    id: flight,
    code: 'TEST 1',
    gateId: gate,
    passengerCapacity: 20,
    status: 'scheduled',
    departureAtUtc: '2026-10-16T10:06:00Z',
    isSynthetic: true,
  });
  data.scenario.inputs.flows.push({
    id: ids.otherCp,
    populationId: ids.event,
    flightId: flight,
    routeId: route,
    passengerCount: 20,
    startsAtUtc: data.scenario.inputs.startsAtUtc,
    endsAtUtc: '2026-10-16T10:06:00Z',
  });
  const captured = JSON.parse(data.run.inputSnapshotJson!);
  captured.scenario = data.scenario;
  captured.layout = data.layout;
  data.run.inputSnapshotJson = JSON.stringify(captured);
  return data;
}
export function workflowEvaluation(id = evaluationId): StaffingEvaluationDetails {
  const metrics = {
    populationCount: 20,
    processedCount: 20,
    unprocessedCount: 0,
    waitSampleCount: 20,
    meanWaitMinutes: 2,
    p95WaitMinutes: 4,
    boardingRisk: 0,
    boardingAtRiskCount: 0,
    boardingRiskPopulationCount: 20,
    checkpoints: [],
  };
  const variants: StaffingEvaluatedVariant[] = [
    {
      variantId: ids.cp,
      scenarioId: ids.scenario,
      runId: baselineRunId,
      position: 0,
      runStatus: 'completed',
      configuration: [],
      reallocatedPeople: 0,
      reallocationCost: 0,
      metrics,
      assessment: { status: 'baseline', reasons: [] },
    },
    {
      variantId: ids.otherCp,
      scenarioId: ids.mark,
      runId: ids.otherRun,
      position: 1,
      runStatus: 'completed',
      configuration: [],
      reallocatedPeople: 1,
      reallocationCost: 20,
      metrics: { ...metrics, meanWaitMinutes: 1, p95WaitMinutes: 2 },
      assessment: { status: 'improved', reasons: [] },
    },
  ];
  return {
    batch: {
      contractVersion: '1.0',
      batch: {
        id,
        name: 'Test calculated evaluation',
        status: 'completed',
        revision: 3,
        createdAtUtc: '2026-10-16T10:00:00Z',
        deadlineAtUtc: '2026-10-16T10:02:00Z',
        startedAtUtc: null,
        finishedAtUtc: null,
        cancellationRequested: false,
        timeLimitSeconds: 120,
        replayOfBatchId: null,
        failureReason: null,
        engineBuild: 'test',
      },
      seeds: [42],
      isSynthetic: true,
      metricsTruncated: false,
      variants: variants.map((row) => ({
        id: row.variantId,
        position: row.position,
        scenarioId: row.scenarioId,
        scenarioRevision: 1,
        name: row.position ? 'Candidate' : 'Baseline',
        layoutVersionId: ids.layout,
        engineVersion: 'simpy-kpi-1.0',
      })),
      replications: variants.map((row) => ({
        id: row.runId,
        variantId: row.variantId,
        seedIndex: 0,
        seed: 42,
        runId: row.runId,
        status: 'completed',
        runStatus: 'completed',
        resultsValid: true,
        failureReason: null,
        remoteCancellationPending: false,
        provenance: {
          hashVersion: '1.0',
          inputHash: 'test-input',
          resultHash: 'test-result',
          engineVersion: 'simpy-kpi-1.0',
          engineBuild: 'test',
          engineMilliseconds: 1,
          executionMilliseconds: 1,
          correlationId: ids.mark,
        },
      })),
      metrics: [],
      comparisons: [],
    },
    planning: {
      status: 'complete',
      candidates: [{}],
      request: {
        candidateCount: 1,
        constraints: { scenarioId: ids.scenario, scenarioRevision: 1, timeLimitSeconds: 5 },
      },
    },
    policy: {
      maxMeanWaitIncreaseMinutes: 0,
      maxP95IncreaseMinutes: 0,
      maxBoardingRiskIncrease: 0,
      maxUnprocessedIncrease: 0,
      maxReallocationCost: 100,
    },
    ranking: 'risk, unprocessed, waiting, P95, cost',
    recommendedRunId: ids.otherRun,
    variants,
  };
}
export function workflowReview(id = evaluationId): RecommendationReview {
  const evaluation = workflowEvaluation(id);
  return {
    id: ids.mark,
    evaluationId: id,
    scenarioId: ids.scenario,
    scenarioRevision: 1,
    baselineRunId,
    candidateRunId: ids.otherRun,
    status: 'pending',
    createdAtUtc: '2026-10-16T10:00:00Z',
    audit: [],
    explanation: {
      resources: [],
      checkpoints: [],
      estimates: [{ metric: 'Mean wait', unit: 'min', before: 2, after: 1, delta: -1 }],
      assumptions: ['Test pair: same inputs except staffing'],
      benefit: 'Calculated improvement',
      risks: ['Synthetic simulation'],
      ranking: evaluation.ranking,
      reallocatedPeople: 1,
      reallocationCost: 20,
      acceptancePolicy: evaluation.policy,
      constraints: { availableStaff: 1, staffPools: [], checkpoints: [] },
    },
  };
}

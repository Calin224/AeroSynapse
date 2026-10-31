import type { ScenarioDetails } from './operations.models';
import type { StaffingAcceptancePolicy } from './staffing-evaluation.models';

export interface RecommendationReview {
  id: string; evaluationId: string; scenarioId: string; scenarioRevision: number;
  baselineRunId: string; candidateRunId: string; status: 'pending' | 'approved' | 'rejected'; createdAtUtc: string;
  explanation: {
    resources: { staffMemberId: string; name: string; beforeCheckpointId: string | null; beforeCheckpoint: string;
      afterCheckpointId: string | null; afterCheckpoint: string }[];
    checkpoints: { checkpointId: string; name: string; before: number; after: number }[];
    /** People per 15-minute slot at each checkpoint, today and with the recommended shift plan. */
    schedule?: { checkpointId: string; name: string; before: number[]; after: number[] }[] | null;
    estimates: { metric: string; unit: string; before: number; after: number; delta: number }[];
    assumptions: string[]; benefit: string; risks: string[]; ranking: string; reallocatedPeople: number; reallocationCost: number;
    acceptancePolicy: StaffingAcceptancePolicy;
    constraints: { availableStaff: number; staffPools: { terminalId: string; availableStaff: number }[];
      checkpoints: { checkpointId: string; minimumStaff: number; maximumStaff: number; physicalLimit: number;
        requiredOpen: boolean; closed: boolean; changeCost: number }[] };
  };
  audit: { id: string; commandId: string; action: string; actor: string; reason: string; recordedAtUtc: string;
    scenarioRevisionBefore: number; scenarioRevisionAfter: number; beforeJson: string; afterJson: string }[];
}
export interface RecommendationOutcome { status: string; message: string; recommendation: RecommendationReview | null }
export interface RecommendationDecisionResult { recommendation: RecommendationReview; scenario: ScenarioDetails | null }

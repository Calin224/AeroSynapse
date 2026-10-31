import type { BatchDetails } from '../batches/batches.models';
import type { ParetoAnalysis } from './pareto.models';

export interface StaffingAcceptancePolicy {
  maxMeanWaitIncreaseMinutes: number;
  maxP95IncreaseMinutes: number;
  maxBoardingRiskIncrease: number;
  maxUnprocessedIncrease: number;
  maxReallocationCost: number;
  /** A slower control is a tradeoff, not a rejection; absent on evaluations saved before it existed. */
  checkpointTradeoffs?: boolean;
}
export interface StaffingObservation {
  checkpointId: string;
  populationCount: number;
  processedCount: number;
  waitSampleCount: number;
  meanWaitMinutes: number | null;
  p95WaitMinutes: number | null;
  boardingRiskPopulationCount: number;
  boardingAtRiskCount: number;
}
/** One person's shift: where they work, in minutes from the scenario start. */
export interface StaffShift {
  staffMemberId: string;
  segments: { checkpointId: string; startMinute: number; endMinute: number }[];
}
/** People assigned at each checkpoint at the start of every slot, and the shifts behind them. */
export interface StaffingSchedule {
  slotMinutes: number;
  checkpoints: {
    checkpointId: string;
    name: string;
    staff: number[];
    arrivals?: number[] | null;
  }[];
  shifts?: StaffShift[] | null;
}
/** Shift rules in 15-minute slots, as the CP-SAT shift planner takes them. */
export interface ShiftRules {
  minShiftSlots: number;
  maxShiftSlots: number;
  breakAfterSlots: number;
  transferMinutes: number;
  maxMovesPerHour: number;
  queueWeight: number;
  staffSlotCost: number;
  moveCost: number;
  /** Operators are planned at this share of their capacity, so random arrivals do not queue long. */
  targetUtilizationPercent: number;
}
export interface StaffingEvaluatedVariant {
  variantId: string;
  scenarioId: string;
  runId: string;
  position: number;
  runStatus: string;
  configuration: { checkpointId: string; name: string; staffCount: number }[];
  reallocatedPeople: number;
  reallocationCost: number;
  metrics: {
    populationCount: number;
    processedCount: number;
    unprocessedCount: number;
    waitSampleCount: number;
    meanWaitMinutes: number | null;
    p95WaitMinutes: number | null;
    boardingRisk: number | null;
    boardingAtRiskCount: number;
    boardingRiskPopulationCount: number;
    checkpoints: StaffingObservation[];
  } | null;
  assessment: { status: string; reasons: string[] };
  schedule?: StaffingSchedule | null;
}
export interface StaffingEvaluationDetails {
  pareto?: ParetoAnalysis | null;
  batch: BatchDetails;
  planning: {
    status: 'complete' | 'exhausted' | 'timeout' | 'invalid_model';
    candidates: unknown[];
    request: {
      candidateCount: number;
      constraints: { scenarioId: string; scenarioRevision: number; timeLimitSeconds: number };
    };
  };
  policy: StaffingAcceptancePolicy;
  ranking: string;
  recommendedRunId: string | null;
  variants: StaffingEvaluatedVariant[];
}

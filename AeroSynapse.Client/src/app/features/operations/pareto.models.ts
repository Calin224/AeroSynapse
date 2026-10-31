export interface ObjectiveWeights { time: number; cost: number; risk: number }
export interface ParetoOptions {
  weights: ObjectiveWeights; normalization: { timeMinutes: number; costPoints: number; riskFraction: number };
  operationalBudget: number; staffMinuteCost: number; maxMeanWaitMinutes: number; maxP95WaitMinutes: number; maxBoardingRisk: number;
}
export interface ParetoPoint {
  runId: string; position: number; feasible: boolean; exclusions: string[]; onFrontier: boolean; dominatedBy: string[];
  timeMinutes: number | null; costPoints: number; riskFraction: number | null;
  normalizedTime: number | null; normalizedCost: number; normalizedRisk: number | null; weightedScore: number | null;
}
export interface ParetoAnalysis {
  status: string; options: ParetoOptions; formula: string;
  planning: { formula: string; status: string; wallTimeSeconds: number;
    request: { durationMinutes: number; options: ParetoOptions; constraints: { scenarioId: string; scenarioRevision: number } };
    solves: { profile: string; weights: ObjectiveWeights; solverStatus: string; attempted: boolean; scaledObjective: number | null;
      bestBound: number | null; proxyTimeMinutes: number | null; proxyRisk: number | null; operationalCost: number | null;
      assignments: { checkpointId: string; staffCount: number }[]; wallTimeSeconds: number }[] };
  points: ParetoPoint[]; frontierRunIds: string[]; preferredRunId: string | null; assumptions: string[];
  tradeoffs: { firstRunId: string; secondRunId: string; timeDeltaMinutes: number; costDeltaPoints: number; riskDeltaFraction: number }[];
}
export interface ParetoComparison {
  comparable: boolean; issues: string[]; left: ParetoAnalysis; right: ParetoAnalysis; unionFrontierRunIds: string[];
  comparisons: { leftRunId: string; rightRunId: string; relation: string }[];
}
export function validParetoOptions(value: ParetoOptions): boolean {
  const finite = (x: number, lo: number, hi: number) => Number.isFinite(x) && x >= lo && x <= hi;
  const integer = (x: number, lo: number, hi: number) => Number.isInteger(x) && finite(x, lo, hi);
  return Object.values(value.weights).every(x => integer(x, 0, 1000)) && Object.values(value.weights).some(x => x > 0)
    && finite(value.normalization.timeMinutes, .01, 10080) && finite(value.normalization.costPoints, 1, 1e12)
    && finite(value.normalization.riskFraction, .001, 1) && integer(value.operationalBudget, 0, 1e14)
    && value.operationalBudget / value.normalization.costPoints <= 1e6 && integer(value.staffMinuteCost, 1, 1e6)
    && finite(value.maxMeanWaitMinutes, 0, 10080) && finite(value.maxP95WaitMinutes, 0, 10080) && finite(value.maxBoardingRisk, 0, 1);
}

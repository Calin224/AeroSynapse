export interface RunProvenance {
  hashVersion: string;
  inputHash: string | null;
  resultHash: string | null;
  engineVersion: string | null;
  engineBuild: string | null;
  engineMilliseconds: number | null;
  executionMilliseconds: number | null;
  correlationId: string;
}
export interface ComparedRun {
  id: string;
  status: string;
  resultsValid: boolean;
  scenarioId?: string | null;
  scenarioRevision: number | null;
  provenance: RunProvenance;
  failureReason: string | null;
}
export interface ComparedMetric {
  zoneId: string;
  zoneType?: string;
  name: string;
  unit: string;
  baseline: number | null;
  candidate: number | null;
  delta: number | null;
  percentChange?: number | null;
  unavailableReason?: string | null;
  baselineRecordedAtUtc?: string | null;
  candidateRecordedAtUtc?: string | null;
}
export interface RunComparison {
  baseline: ComparedRun;
  candidate: ComparedRun;
  inputsMatch: boolean | null;
  versionsMatch: boolean | null;
  resultsMatch: boolean | null;
  metrics: ComparedMetric[];
  metricsTruncated: boolean;
  comparability?: {
    comparable: boolean;
    populationMatches: boolean | null;
    baselinePopulationHash: string | null;
    candidatePopulationHash: string | null;
    issues: { code: string; message: string; severity: string }[];
  } | null;
}

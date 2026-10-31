import type { RunProvenance } from '../operations/comparison.models';
export interface BatchSummary {
  id: string;
  name: string;
  status: string;
  revision: number;
  createdAtUtc: string;
  deadlineAtUtc: string;
  startedAtUtc: string | null;
  finishedAtUtc: string | null;
  cancellationRequested: boolean;
  timeLimitSeconds: number;
  replayOfBatchId: string | null;
  failureReason: string | null;
  engineBuild: string | null;
}
export interface Statistics {
  count: number;
  mean: number | null;
  standardDeviation: number | null;
  minimum: number | null;
  maximum: number | null;
  median: number | null;
  p05: number | null;
  p95: number | null;
}
export interface BatchDetails {
  contractVersion: string;
  batch: BatchSummary;
  seeds: number[];
  isSynthetic: boolean;
  metricsTruncated: boolean;
  variants: {
    id: string;
    position: number;
    scenarioId: string;
    scenarioRevision: number;
    name: string;
    layoutVersionId: string;
    engineVersion: string;
  }[];
  replications: {
    id: string;
    variantId: string;
    seedIndex: number;
    seed: number;
    runId: string;
    status: string;
    runStatus: string;
    resultsValid: boolean;
    failureReason: string | null;
    provenance: RunProvenance;
    remoteCancellationPending: boolean;
  }[];
  metrics: {
    variantId: string;
    zoneId: string;
    name: string;
    unit: string;
    values: Statistics;
    missingOrUnsuccessfulCount: number;
  }[];
  comparisons: {
    candidateVariantId: string;
    zoneId: string;
    name: string;
    unit: string;
    differences: Statistics;
    includedSeeds: number[];
    excludedSeeds: { seed: number; reason: string }[];
  }[];
}

import { uuid } from './copilot.models';

export interface CopilotExplanation {
  contractVersion: string;
  disclosure: string;
  summary: string;
  sourceKind: string;
  outputHash: string;
  facts: {
    id: string;
    label: string;
    value: number | null;
    unit: string;
    runId: string;
    zoneId: string | null;
    sourcePath: string;
  }[];
  comparisons: {
    metric: string;
    unit: string;
    baselineRunId: string;
    candidateRunId: string;
    before: number | null;
    after: number | null;
    delta: number | null;
    percent: number | null;
    percentUnavailableReason: string | null;
    beforeSource: string;
    afterSource: string;
  }[];
  outcomes: { runId: string; status: string; reasons: string[] }[];
  runs: { runId: string; role: string }[];
  limitations: string[];
}
const finite = (value: number | null) => value === null || Number.isFinite(value);
export function validExplanation(
  value: CopilotExplanation | undefined,
  allowedRuns?: string[],
): boolean {
  if (!value) return true; // Compatible with older persisted/offline responses.
  const run = (id: string) =>
    uuid(id) &&
    (!allowedRuns || allowedRuns.includes(id)) &&
    value.runs.some((r) => r.runId === id);
  return (
    value.contractVersion === '1.0' &&
    typeof value.summary === 'string' &&
    typeof value.disclosure === 'string' &&
    /^[a-f0-9]{64}$/.test(value.outputHash) &&
    Array.isArray(value.runs) &&
    value.runs.length > 0 &&
    value.runs.every(
      (r) =>
        !!r &&
        uuid(r.runId) &&
        typeof r.role === 'string' &&
        (!allowedRuns || allowedRuns.includes(r.runId)),
    ) &&
    Array.isArray(value.facts) &&
    value.facts.every(
      (f) =>
        !!f &&
        finite(f.value) &&
        run(f.runId) &&
        typeof f.sourcePath === 'string' &&
        f.sourcePath.startsWith('/'),
    ) &&
    Array.isArray(value.comparisons) &&
    value.comparisons.every(
      (c) =>
        !!c &&
        run(c.baselineRunId) &&
        run(c.candidateRunId) &&
        c.baselineRunId !== c.candidateRunId &&
        [c.before, c.after, c.delta, c.percent].every(finite) &&
        (c.delta === null ||
          (c.before !== null &&
            c.after !== null &&
            Math.abs(c.delta - (c.after - c.before)) < 1e-8)) &&
        (c.percent === null ||
          (c.before !== null &&
            c.before > 0 &&
            c.delta !== null &&
            Math.abs(c.percent - (c.delta / c.before) * 100) < 1e-8)),
    ) &&
    Array.isArray(value.outcomes) &&
    value.outcomes.every(
      (o) =>
        !!o &&
        run(o.runId) &&
        Array.isArray(o.reasons) &&
        o.reasons.every((r) => typeof r === 'string'),
    ) &&
    Array.isArray(value.limitations) &&
    value.limitations.every((l) => typeof l === 'string')
  );
}

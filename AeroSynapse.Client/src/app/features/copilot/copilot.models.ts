import { CopilotExplanation, validExplanation } from './copilot-explanation.models';

export interface CopilotScope {
  scenarioId: string;
  runId: string;
  zoneId: string;
  minute: number;
}
export interface CopilotContext {
  contractVersion: string;
  scenarioId: string;
  runId: string;
  status: string;
  resultsValid: boolean;
  isSynthetic: boolean;
  scenarioRevision: number | null;
  layoutVersionId: string | null;
  durationMinutes: number | null;
  snapshotIntervalMinutes: number | null;
  zones: { id: string; code: string; name: string }[];
  unavailableReason: string | null;
}
export interface CopilotCitation extends CopilotScope {
  id: string;
  label: string;
  value: number;
  unit: string;
  recordedAtUtc: string;
}
export interface CopilotAnswer {
  contractVersion: string;
  scope: CopilotScope;
  status: 'answered' | 'insufficient_data';
  reasonCode: string;
  answer: string;
  modelStatus: string;
  sourceHash: string | null;
  citations: CopilotCitation[];
  explanation?: CopilotExplanation;
  queueAdvice?: {
    options: { id: string; kind: 'checkpoint_open' | 'queue_redistribution'; text: string; fromCheckpointId: string; toCheckpointId: string; passengerCount: number }[];
    facts: { id: string; label: string; value: number; unit: string; runId: string; zoneId: string; sourcePath: string }[];
    limitation: string;
  };
}
export const uuid = (value: string) => /^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/i.test(value);
export function sameScope(a: CopilotScope, b: CopilotScope): boolean {
  return (
    a.scenarioId === b.scenarioId &&
    a.runId === b.runId &&
    a.zoneId === b.zoneId &&
    a.minute === b.minute
  );
}
export function validAnswer(value: CopilotAnswer, expected: CopilotScope): boolean {
  return (
    value?.contractVersion === '1.0' &&
    !!value.scope &&
    sameScope(value.scope, expected) &&
    ['answered', 'insufficient_data'].includes(value.status) &&
    typeof value.answer === 'string' &&
    validExplanation(value.explanation, [expected.runId]) &&
    (!value.queueAdvice || (
      Array.isArray(value.queueAdvice.options) && value.queueAdvice.options.every(o => !!o && typeof o.id === 'string'
        && ['checkpoint_open', 'queue_redistribution'].includes(o.kind) && typeof o.text === 'string'
        && o.fromCheckpointId === expected.zoneId && uuid(o.toCheckpointId) && o.toCheckpointId !== o.fromCheckpointId
        && Number.isInteger(o.passengerCount) && o.passengerCount > 0 && o.passengerCount <= 10000)
      && Array.isArray(value.queueAdvice.facts) && value.queueAdvice.facts.every(f => !!f && f.runId === expected.runId
        && uuid(f.zoneId) && Number.isFinite(f.value) && typeof f.label === 'string' && typeof f.sourcePath === 'string')
      && typeof value.queueAdvice.limitation === 'string')) &&
    Array.isArray(value.citations) &&
    (value.status !== 'answered' || value.citations.length > 0) &&
    (value.citations.length === 0 ||
      (typeof value.sourceHash === 'string' && /^[0-9a-f]{64}$/.test(value.sourceHash))) &&
    value.citations.every(
      (c) =>
        !!c &&
        sameScope(c, expected) &&
        Number.isFinite(c.value) &&
        typeof c.id === 'string' &&
        typeof c.label === 'string' &&
        typeof c.unit === 'string' &&
        Number.isFinite(Date.parse(c.recordedAtUtc)),
    )
  );
}
export function modelLabel(status: string): string {
  switch (status) {
    case 'available':
      return 'AI asistat · cifre verificate de server';
    case 'disabled':
      return 'Rezumat din metrici · LLM dezactivat';
    case 'unconfigured':
      return 'Rezumat din metrici · LLM neconfigurat';
    case 'timeout':
      return 'Rezumat din metrici · LLM a depășit timpul alocat';
    case 'unavailable':
      return 'Rezumat din metrici · LLM indisponibil';
    case 'invalid_response':
      return 'Rezumat din metrici · răspuns LLM respins';
    default:
      return 'Verificare din date · LLM neapelat';
  }
}

import type { CopilotScope, CopilotAnswer } from './copilot.models';
import { sameScope, validAnswer } from './copilot.models';
import { CopilotExplanation, validExplanation } from './copilot-explanation.models';

/** Read-only tools the page offers; staffing optimisation lives in the operator workflow. */
export type ToolName = 'inspect_zone' | 'forecast_queues';
export type ToolIntent = 'diagnose' | 'forecast';
export interface ForecastPoint {
  minute: number;
  arrivals: number;
  capacity: number;
  queuePassengers: number;
  availableStaff: number;
}
export interface QueueForecast {
  kind: string;
  formula: string;
  runId: string;
  fromMinute: number;
  toMinute: number;
  queueUnit: string;
  flowUnit: string;
  timeUnit: string;
  zone: { checkpointId: string; initialQueuePassengers: number; points: ForecastPoint[] };
}
export interface ToolResult {
  contractVersion: string;
  scope: CopilotScope;
  intent: ToolIntent;
  tool: ToolName;
  effect: string;
  message: string;
  investigation?: {
    observation: CopilotAnswer;
    events: { id: string; type: string; minute: number; checkpointId: string | null }[];
  };
  forecast?: QueueForecast;
  explanation?: CopilotExplanation;
}
export function validToolResult(value: ToolResult, scope: CopilotScope, tool: ToolName): boolean {
  if (
    value?.contractVersion !== '1.0' ||
    !value.scope ||
    !sameScope(value.scope, scope) ||
    value.tool !== tool ||
    !validExplanation(value.explanation, [scope.runId])
  )
    return false;
  if (tool === 'inspect_zone')
    return (
      !!value.investigation &&
      validAnswer(value.investigation.observation, scope) &&
      Array.isArray(value.investigation.events)
    );
  const f = value.forecast;
  return (
    !!f &&
    f.runId === scope.runId &&
    f.zone?.checkpointId === scope.zoneId &&
    f.fromMinute === scope.minute &&
    f.toMinute > f.fromMinute &&
    Array.isArray(f.zone.points) &&
    f.zone.points.length === f.toMinute - f.fromMinute &&
    f.zone.points.every(
      (p, index) =>
        p.minute === f.fromMinute + index + 1 &&
        Number.isFinite(p.queuePassengers) &&
        p.queuePassengers >= 0,
    )
  );
}

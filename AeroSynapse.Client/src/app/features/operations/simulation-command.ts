import { validRunId } from '../operational-report/report.models';

export interface SimulationExecution {
  commandId: string;
  runId: string;
  scenarioId: string;
  status: 'pending' | 'running' | 'completed' | 'failed';
  correlationId: string;
  resultsValid: boolean;
  errorCode: string | null;
  failureReason: string | null;
}

/** Receipts cross the HTTP boundary: TypeScript types alone cannot establish command ownership. */
export function simulationReceiptProblem(
  receipt: SimulationExecution,
  command: { scenarioId: string; key: string; correlationId?: string },
  runId?: string,
): string | null {
  if (
    !receipt ||
    receipt.commandId !== command.key ||
    receipt.scenarioId !== command.scenarioId ||
    !validRunId(receipt.runId) ||
    (runId && receipt.runId !== runId) ||
    (command.correlationId && receipt.correlationId !== command.correlationId) ||
    !['pending', 'running', 'completed', 'failed'].includes(receipt.status) ||
    typeof receipt.resultsValid !== 'boolean' ||
    (receipt.resultsValid && receipt.status !== 'completed')
  ) {
    return 'Confirmarea nu corespunde comenzii de simulare. Reverifică aceeași comandă.';
  }
  return null;
}

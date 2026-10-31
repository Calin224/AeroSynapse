import { TimeoutError } from 'rxjs';
import { ApiError } from './api-error.interceptor';

// Allows the existing 30-second solver budget plus transport overhead. A timeout
// cancels waiting in the browser; it does not imply a server command was rolled back.
export const OPERATION_REQUEST_TIMEOUT_MS = 45_000;
// Small reconciliation reads must release exhaustMap so a stalled transport can recover.
export const OPERATIONS_RECOVERY_TIMEOUT_MS = 15_000;
export function operationError(error: unknown): string {
  if (
    error instanceof TimeoutError ||
    (error instanceof ApiError && [408, 504].includes(error.status))
  ) {
    return 'Timeout: confirmarea nu a sosit în limita de timp. Serverul poate continua operația. Reîncearcă verificarea aceleiași comenzi; nu crea o comandă nouă.';
  }
  return error instanceof Error ? error.message : 'Operația nu a putut fi confirmată. Reîncearcă.';
}

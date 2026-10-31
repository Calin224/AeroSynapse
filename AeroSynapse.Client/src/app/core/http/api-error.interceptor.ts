import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { catchError, throwError } from 'rxjs';
export class ApiError extends Error {
  constructor(
    readonly status: number,
    message: string,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}
export const apiErrorInterceptor: HttpInterceptorFn = (request, next) =>
  next(request).pipe(
    catchError((error: unknown) => {
      if (!(error instanceof HttpErrorResponse)) return throwError(() => error);
      const message =
        error.status === 0
          ? 'Conexiunea nu este disponibilă. Verifică rețeaua și încearcă din nou.'
          : error.status === 404
            ? 'Datele solicitate nu au fost găsite.'
            : 'Datele nu au putut fi încărcate. Încearcă din nou.';
      return throwError(() => new ApiError(error.status, message));
    }),
  );

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
          : error.status === 409
            ? 'Datele s-au schimbat sau operația nu este permisă în statusul curent. Actualizează și încearcă din nou.'
            : error.status === 400 || error.status === 422
              ? validationMessage(error.error)
              : error.status === 404
                ? 'Datele solicitate nu au fost găsite.'
                : 'Datele nu au putut fi încărcate. Încearcă din nou.';
      return throwError(() => new ApiError(error.status, message));
    }),
  );

function validationMessage(body: unknown): string {
  if (
    body &&
    typeof body === 'object' &&
    'errors' in body &&
    body.errors &&
    typeof body.errors === 'object'
  ) {
    const details = Object.entries(body.errors).flatMap(([field, messages]) =>
      Array.isArray(messages)
        ? messages
            .filter((message): message is string => typeof message === 'string')
            .map((message) => `${field}: ${message}`)
        : [],
    );
    if (details.length) return details.slice(0, 5).join(' ');
  }
  return 'Datele introduse nu sunt valide. Verifică câmpurile și încearcă din nou.';
}

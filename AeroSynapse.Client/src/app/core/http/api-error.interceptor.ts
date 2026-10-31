import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { catchError, throwError } from 'rxjs';
export class ApiError extends Error {
  constructor(
    readonly status: number,
    message: string,
    readonly fieldErrors: Readonly<Record<string, readonly string[]>> = {},
    readonly code?: string,
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
        error.status === 429
          ? 'Ai atins limita de cereri. Așteaptă înainte de a încerca din nou.'
          : error.status === 401
            ? 'Sesiunea a expirat sau autentificarea este necesară.'
            : error.status === 403
              ? 'Rolul contului nu permite această acțiune.'
              : error.status === 0
                ? 'Conexiunea nu este disponibilă. Verifică rețeaua și încearcă din nou.'
                : error.status === 409
                  ? 'Datele s-au schimbat sau operația nu este permisă în statusul curent. Actualizează și încearcă din nou.'
                  : error.status === 502 && error.error?.code === 'passenger_generator_unavailable'
                    ? 'Serviciul de generare nu este disponibil. Verifică serviciul Intelligence și încearcă din nou.'
                    : error.status === 502 && error.error?.code === 'staffing_planner_unavailable'
                      ? 'Serviciul de optimizare a personalului nu este disponibil. Verifică serviciul Intelligence și încearcă din nou.'
                      : error.status === 400 || error.status === 422
                        ? validationMessage(error.error)
                        : error.status === 404
                          ? 'Datele solicitate nu au fost găsite.'
                          : 'Datele nu au putut fi încărcate. Încearcă din nou.';
      return throwError(
        () =>
          new ApiError(
            error.status,
            message,
            error.status === 400 || error.status === 422 ? validationFields(error.error) : {},
            typeof error.error?.code === 'string' ? error.error.code : undefined,
          ),
      );
    }),
  );

function validationMessage(body: unknown): string {
  const details = Object.entries(validationFields(body)).flatMap(([field, messages]) =>
    messages.map((message) => `${field}: ${message}`),
  );
  if (details.length) return details.slice(0, 5).join(' ');
  return 'Datele introduse nu sunt valide. Verifică câmpurile și încearcă din nou.';
}

function validationFields(body: unknown): Record<string, string[]> {
  if (
    !body ||
    typeof body !== 'object' ||
    !('errors' in body) ||
    !body.errors ||
    typeof body.errors !== 'object'
  )
    return {};
  if (Array.isArray(body.errors)) {
    const fields = new Map<string, string[]>();
    for (const item of body.errors) {
      if (item && typeof item.field === 'string' && typeof item.message === 'string')
        fields.set(item.field, [...(fields.get(item.field) ?? []), item.message]);
    }
    return Object.fromEntries(fields);
  }
  return Object.fromEntries(
    Object.entries(body.errors)
      .filter(([, value]) => Array.isArray(value))
      .map(([field, value]) => [
        field,
        (value as unknown[]).filter((message): message is string => typeof message === 'string'),
      ]),
  );
}

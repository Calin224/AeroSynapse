import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, from, switchMap, throwError } from 'rxjs';
import { API_BASE_URL } from '../http/api-client';
import { AuthService } from './auth-service';
import { runtimeConfig } from '../http/runtime-config';

export const authInterceptor: HttpInterceptorFn = (request, next) => {
  const base = new URL(inject(API_BASE_URL).replace(/\/$/, '') + '/', document.baseURI);
  const url = new URL(request.url, document.baseURI);
  // Never attach session credentials or antiforgery headers to another service.
  const intelligence = new URL(
    runtimeConfig().intelligenceBaseUrl.replace(/\/$/, '') + '/',
    document.baseURI,
  );
  const api = url.origin === base.origin && url.pathname.startsWith(base.pathname);
  const tool =
    intelligence.origin === base.origin &&
    url.origin === intelligence.origin &&
    url.pathname.startsWith(intelligence.pathname);
  if (!api && !tool) return next(request);
  const auth = inject(AuthService);
  const credentials = request.clone({ withCredentials: true });
  const writes = !['GET', 'HEAD', 'OPTIONS'].includes(request.method);
  return (
    writes
      ? from(auth.csrfToken()).pipe(
          switchMap((token) => next(credentials.clone({ setHeaders: { 'X-CSRF-TOKEN': token } }))),
        )
      : next(credentials)
  ).pipe(
    catchError((error: unknown) => {
      if (error instanceof HttpErrorResponse && error.status === 401) auth.expire();
      return throwError(() => error);
    }),
  );
};

import { toSignal } from '@angular/core/rxjs-interop';
import { catchError, map, Observable, of, startWith, Subject, switchMap } from 'rxjs';
import { ApiError } from '../core/http/api-error.interceptor';
export type ResourceState<T> =
  | { status: 'loading' }
  | { status: 'ready'; data: T }
  | { status: 'empty' }
  | { status: 'error'; message: string };
// Invoke in an injection context. toSignal cleans up; switchMap cancels stale requests.
export function createDataResource<T>(load: () => Observable<T>, isEmpty: (data: T) => boolean) {
  const refresh = new Subject<void>();
  const state = toSignal(
    refresh.pipe(
      startWith(undefined),
      switchMap(() =>
        load().pipe(
          map((data): ResourceState<T> =>
            isEmpty(data) ? { status: 'empty' } : { status: 'ready', data },
          ),
          catchError((error: unknown) =>
            of<ResourceState<T>>({
              status: 'error',
              message:
                error instanceof ApiError ? error.message : 'Datele primite nu pot fi afișate.',
            }),
          ),
          startWith<ResourceState<T>>({ status: 'loading' }),
        ),
      ),
    ),
    { initialValue: { status: 'loading' } as ResourceState<T> },
  );
  return { state, reload: () => refresh.next() };
}

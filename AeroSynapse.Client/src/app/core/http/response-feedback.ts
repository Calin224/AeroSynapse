import { HttpInterceptorFn } from '@angular/common/http';
import { DestroyRef, Injectable, computed, inject, signal } from '@angular/core';
import { defer, finalize } from 'rxjs';
import { API_BASE_URL } from './api-client';

/** UX budgets, not simulation results. No request is retried or cancelled here. */
export const SLOW_RESPONSE_MS = 2_000;

@Injectable({ providedIn: 'root' })
export class ResponseFeedback {
  readonly online = signal(navigator.onLine);
  readonly pending = signal(0);
  readonly elapsedMs = signal(0);
  readonly slow = computed(() => this.pending() > 0 && this.elapsedMs() >= SLOW_RESPONSE_MS);
  private readonly requests = new Map<symbol, number>();
  private timer?: ReturnType<typeof setInterval>;

  constructor() {
    const update = () => this.online.set(navigator.onLine);
    window.addEventListener('online', update);
    window.addEventListener('offline', update);
    inject(DestroyRef).onDestroy(() => {
      window.removeEventListener('online', update);
      window.removeEventListener('offline', update);
      clearInterval(this.timer);
    });
  }
  begin() {
    const id = Symbol(),
      start = performance.now();
    this.requests.set(id, start);
    this.pending.set(this.requests.size);
    const update = () =>
      this.elapsedMs.set(Math.max(0, performance.now() - Math.min(...this.requests.values())));
    update();
    this.timer ??= setInterval(update, 250);
    return id;
  }
  finish(id: symbol) {
    this.requests.delete(id);
    this.pending.set(this.requests.size);
    this.elapsedMs.set(
      this.requests.size ? Math.max(0, performance.now() - Math.min(...this.requests.values())) : 0,
    );
    if (!this.requests.size) {
      clearInterval(this.timer);
      this.timer = undefined;
    }
  }
}

export const responseFeedbackInterceptor: HttpInterceptorFn = (request, next) => {
  const base = new URL(inject(API_BASE_URL).replace(/\/$/, '') + '/', document.baseURI);
  const url = new URL(request.url, document.baseURI);
  if (url.origin !== base.origin || !url.pathname.startsWith(base.pathname)) return next(request);
  const feedback = inject(ResponseFeedback);
  return defer(() => {
    const observation = feedback.begin();
    return next(request).pipe(finalize(() => feedback.finish(observation)));
  });
};

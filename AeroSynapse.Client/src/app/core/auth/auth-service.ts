import { HttpBackend, HttpClient, HttpErrorResponse } from '@angular/common/http';
import { DestroyRef, inject, Injectable } from '@angular/core';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { API_BASE_URL } from '../http/api-client';
import { SessionState, WorkspaceSession } from './session-state';

@Injectable({ providedIn: 'root' })
export class AuthService {
  readonly state = inject(SessionState);
  private readonly http = new HttpClient(inject(HttpBackend));
  private readonly base = inject(API_BASE_URL).replace(/\/$/, '');
  private readonly router = inject(Router);
  private csrf?: Promise<string>;
  private restored = false;
  private restoring?: Promise<boolean>;
  private expiry?: ReturnType<typeof setTimeout>;

  constructor() {
    inject(DestroyRef).onDestroy(() => clearTimeout(this.expiry));
  }

  restore(): Promise<boolean> {
    if (this.restored) return Promise.resolve(this.state.session() !== null);
    return (this.restoring ??= firstValueFrom(
      this.http.get<WorkspaceSession>(`${this.base}/auth/session`, { withCredentials: true }),
    )
      .then((session) => {
        this.accept(session);
        return true;
      })
      .catch(() => false)
      .finally(() => {
        this.restored = true;
        this.restoring = undefined;
      }));
  }

  csrfToken(): Promise<string> {
    return (this.csrf ??= firstValueFrom(
      this.http.get<{ requestToken: string }>(`${this.base}/auth/csrf`, { withCredentials: true }),
    )
      .then((response) => response.requestToken)
      .catch((error) => {
        this.csrf = undefined;
        throw error;
      }));
  }

  async login(username: string, password: string): Promise<void> {
    this.csrf = undefined;
    const csrf = await this.csrfToken();
    const session = await firstValueFrom(
      this.http.post<WorkspaceSession>(
        `${this.base}/auth/login`,
        { username, password },
        { withCredentials: true, headers: { 'X-CSRF-TOKEN': csrf } },
      ),
    );
    this.csrf = undefined; // Antiforgery binds to the newly authenticated identity.
    this.restored = true;
    this.accept(session);
  }

  async logout(): Promise<void> {
    const csrf = await this.csrfToken();
    try {
      await firstValueFrom(
        this.http.post(`${this.base}/auth/logout`, null, {
          withCredentials: true,
          headers: { 'X-CSRF-TOKEN': csrf },
        }),
      );
    } catch (error) {
      if (!(error instanceof HttpErrorResponse) || error.status !== 401) throw error;
    }
    this.expire();
  }

  expire(): void {
    clearTimeout(this.expiry);
    this.csrf = undefined;
    this.state.session.set(null);
    this.restored = true;
    if (!this.router.url.startsWith('/login'))
      void this.router.navigate(['/login'], { queryParams: { expired: '1' } });
  }

  private accept(session: WorkspaceSession): void {
    clearTimeout(this.expiry);
    const remaining = Date.parse(session.expiresAtUtc) - Date.now();
    if (!Number.isFinite(remaining) || remaining <= 0) {
      this.expire();
      return;
    }
    this.state.session.set(session);
    this.expiry = setTimeout(() => this.expire(), Math.min(remaining, 2_147_483_647));
  }
}

import { computed, Injectable, signal } from '@angular/core';

export type WorkspaceRole = 'Viewer' | 'Operator' | 'Admin';
export interface WorkspaceSession {
  username: string;
  roles: WorkspaceRole[];
  expiresAtUtc: string;
  tenantId?: string;
}

@Injectable({ providedIn: 'root' })
export class SessionState {
  readonly session = signal<WorkspaceSession | null>(null);
  readonly canWrite = computed(
    () => this.session()?.roles.some((role) => role === 'Operator' || role === 'Admin') === true,
  );
  readonly isAdmin = computed(() => this.session()?.roles.includes('Admin') === true);
}

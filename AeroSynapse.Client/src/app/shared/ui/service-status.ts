import { HttpClient } from '@angular/common/http';
import { Component, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { catchError, map, Observable, of, timeout } from 'rxjs';
import { ApiClient } from '../../core/http/api-client';
import { runtimeConfig } from '../../core/http/runtime-config';

type ConnectionState = 'checking' | 'healthy' | 'unavailable';
interface HealthResponse {
  status: string;
}
const healthState = (request: Observable<HealthResponse>) =>
  request.pipe(
    timeout<HealthResponse>(4000),
    map((health: HealthResponse): ConnectionState =>
      health.status === 'Healthy' ? 'healthy' : 'unavailable',
    ),
    catchError(() => of<ConnectionState>('unavailable')),
  );

@Component({
  selector: 'app-service-status',
  template: `<span aria-live="polite"
    ><span class="connection" [class.ok]="api() === 'healthy'"></span>API · {{ label(api()) }}
    <span class="separator">/</span>
    <span class="connection" [class.ok]="intelligence() === 'healthy'"></span>FastAPI ·
    {{ label(intelligence()) }}</span
  >`,
  styles: `
    .connection {
      display: inline-block;
      width: 6px;
      height: 6px;
      border-radius: 50%;
      background: #dbb264;
      margin-right: 4px;
    }
    .connection.ok {
      background: var(--accent);
    }
    .separator {
      padding: 0 8px;
    }
  `,
})
export class ServiceStatus {
  protected readonly api = toSignal(healthState(inject(ApiClient).get<HealthResponse>('health')), {
    initialValue: 'checking' as ConnectionState,
  });
  protected readonly intelligence = toSignal(
    healthState(
      inject(HttpClient).get<HealthResponse>(
        `${runtimeConfig().intelligenceBaseUrl.replace(/\/$/, '')}/health`,
      ),
    ),
    { initialValue: 'checking' as ConnectionState },
  );
  protected label(state: ConnectionState): string {
    return state === 'healthy' ? 'conectat' : state === 'checking' ? 'verificare' : 'indisponibil';
  }
}

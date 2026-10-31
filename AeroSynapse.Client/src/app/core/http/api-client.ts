import { HttpClient } from '@angular/common/http';
import { inject, Injectable, InjectionToken } from '@angular/core';
import { runtimeConfig } from './runtime-config';
// Public runtime-config.js supplies endpoint URLs without rebuilding the application.
export const API_BASE_URL = new InjectionToken<string>('API_BASE_URL', {
  providedIn: 'root',
  factory: () => runtimeConfig().apiBaseUrl,
});
@Injectable({ providedIn: 'root' })
export class ApiClient {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = inject(API_BASE_URL).replace(/\/$/, '');
  get<T>(path: string) {
    return this.http.get<T>(`${this.baseUrl}/${path.replace(/^\//, '')}`);
  }
}

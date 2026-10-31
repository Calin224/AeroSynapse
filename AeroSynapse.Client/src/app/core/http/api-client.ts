import { HttpClient } from '@angular/common/http';
import { inject, Injectable, InjectionToken } from '@angular/core';
import { runtimeConfig } from './runtime-config';
// Runtime-config.js is public and generated from environment variables in Docker.
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
  post<T>(path: string, body: unknown = null, headers: Record<string, string> = {}) {
    return this.http.post<T>(`${this.baseUrl}/${path.replace(/^\//, '')}`, body, { headers });
  }
  put<T>(path: string, body: unknown) {
    return this.http.put<T>(`${this.baseUrl}/${path.replace(/^\//, '')}`, body);
  }
  delete<T>(path: string) {
    return this.http.delete<T>(`${this.baseUrl}/${path.replace(/^\//, '')}`);
  }
  getText(path: string) {
    return this.http.get(`${this.baseUrl}/${path.replace(/^\//, '')}`, { responseType: 'text' });
  }
}

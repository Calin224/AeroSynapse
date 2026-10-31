import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';

/** Local dashboard fixtures stay separate from the actual backend API. */
@Injectable({ providedIn: 'root' })
export class DemoDataClient {
  private readonly http = inject(HttpClient);
  get<T>(filename: string) {
    return this.http.get<T>(`/data/${filename}`);
  }
}

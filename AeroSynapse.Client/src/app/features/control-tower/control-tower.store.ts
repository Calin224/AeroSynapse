import { inject, Injectable } from '@angular/core';
import { ApiClient } from '../../core/http/api-client';
import { createDataResource } from '../../shared/data-resource';
import { TwinScene } from '../digital-twin/twin-model';
export interface AirportSnapshot {
  airport: string;
  code: string;
  metrics: { label: string; value: string; detail: string; icon: string }[];
  flights: { id: string; destination: string; gate: string; status: string }[];
  scene: TwinScene;
}
@Injectable()
export class ControlTowerStore {
  private readonly api = inject(ApiClient);
  readonly resource = createDataResource(
    () => this.api.get<AirportSnapshot | null>('data/control-tower.json'),
    (data) => data === null,
  );
}

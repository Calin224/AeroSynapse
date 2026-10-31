import { inject, Injectable } from '@angular/core';
import { DemoDataClient } from '../../core/http/demo-data-client';
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
  private readonly api = inject(DemoDataClient);
  readonly resource = createDataResource(
    () => this.api.get<AirportSnapshot | null>('control-tower.json'),
    (data) => data === null,
  );
}

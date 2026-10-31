import { inject, Injectable } from '@angular/core';
import { DemoDataClient } from '../../core/http/demo-data-client';
import { createDataResource } from '../../shared/data-resource';
import { TwinScene } from '../digital-twin/twin-model';
export interface AirportScenario {
  id: string;
  name: string;
  description: string;
  impact: string;
  scene: TwinScene;
}
@Injectable()
export class ScenarioStore {
  private readonly api = inject(DemoDataClient);
  readonly resource = createDataResource(
    () => this.api.get<AirportScenario[]>('scenarios.json'),
    (data) => data.length === 0,
  );
}

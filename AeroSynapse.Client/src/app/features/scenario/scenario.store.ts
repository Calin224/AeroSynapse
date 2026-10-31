import { inject, Injectable } from '@angular/core';
import { ApiClient } from '../../core/http/api-client';
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
  private readonly api = inject(ApiClient);
  readonly resource = createDataResource(
    () => this.api.get<AirportScenario[]>('data/scenarios.json'),
    (data) => data.length === 0,
  );
}

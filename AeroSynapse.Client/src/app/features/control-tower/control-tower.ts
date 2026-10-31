import { Component, inject } from '@angular/core';
import { ButtonModule } from 'primeng/button';
import { ControlTowerStore } from './control-tower.store';
import { DataState } from '../../shared/ui/data-state';
import { DigitalTwin } from '../digital-twin/digital-twin';
@Component({
  selector: 'app-control-tower',
  imports: [ButtonModule, DataState, DigitalTwin],
  providers: [ControlTowerStore],
  templateUrl: './control-tower.html',
  styleUrl: './control-tower.css',
})
export class ControlTower {
  protected readonly resource = inject(ControlTowerStore).resource;
}

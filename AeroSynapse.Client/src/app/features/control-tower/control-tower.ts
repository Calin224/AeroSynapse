import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';

/** One routed workspace keeps the simulation pages and their own leave guards together. */
@Component({
  selector: 'app-control-tower',
  imports: [RouterOutlet],
  templateUrl: './control-tower.html',
  styleUrl: './control-tower.css',
})
export class ControlTower {}

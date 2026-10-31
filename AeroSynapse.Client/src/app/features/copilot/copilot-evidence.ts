import { DecimalPipe } from '@angular/common';
import { Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { CopilotExplanation } from './copilot-explanation.models';

@Component({
  selector: 'app-copilot-evidence',
  imports: [DecimalPipe, RouterLink],
  templateUrl: './copilot-evidence.html',
  styleUrl: './copilot-evidence.css',
})
export class CopilotEvidence {
  readonly explanation = input.required<CopilotExplanation>();
  readonly metricLabels: Record<string, string> = {
    meanWaitMinutes: 'Așteptare medie',
    p95WaitMinutes: 'P95 așteptare',
    boardingRisk: 'Risc îmbarcare',
    unprocessedCount: 'Pasageri neprocesați',
  };
}

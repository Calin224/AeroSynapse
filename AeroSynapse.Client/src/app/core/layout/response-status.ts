import { Component, inject } from '@angular/core';
import { ResponseFeedback } from '../http/response-feedback';

/** Page-level connection alerts: an offline banner and a floating slow-response toast. */
@Component({
  selector: 'app-response-status',
  template: `
    @if (!feedback.online()) {
      <p class="connection-notice" role="alert">
        <i class="pi pi-wifi" aria-hidden="true"></i>
        Browserul este offline. Datele afișate sunt ultimele confirmate. Operațiile deja trimise pot
        continua pe server; folosește reverificarea aceleiași comenzi după reconectare.
      </p>
    }
    <span class="slow-toast" role="status" [class.visible]="feedback.slow()">{{
      feedback.slow() ? 'Răspuns API lent · așteaptă confirmarea; evită o comandă nouă.' : ''
    }}</span>
  `,
  styles: `
    :host {
      display: block;
    }
    .connection-notice {
      display: flex;
      align-items: flex-start;
      gap: 10px;
      margin: 0 0 16px;
      padding: 10px 14px;
      border: 1px solid var(--warning-line);
      background: var(--warning-soft);
      color: var(--text);
      border-radius: var(--radius-sm);
      font-size: 12px;
    }
    .connection-notice i {
      margin-top: 2px;
      color: var(--warning);
    }
    .slow-toast {
      position: fixed;
      left: 50%;
      bottom: 24px;
      z-index: 60;
      max-width: calc(100vw - 32px);
      padding: 10px 16px;
      border: 1px solid var(--warning-line);
      border-radius: var(--radius);
      background: var(--panel);
      color: var(--warning);
      box-shadow: none;
      font-size: 12px;
      font-weight: 500;
      opacity: 0;
      pointer-events: none;
      transform: translate(-50%, 12px);
      transition: none;
    }
    .slow-toast.visible {
      opacity: 1;
      transform: translate(-50%, 0);
    }
  `,
})
export class ResponseStatus {
  readonly feedback = inject(ResponseFeedback);
}

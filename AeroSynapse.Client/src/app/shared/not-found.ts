import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';

@Component({
  template: `<section class="not-found">
    <div class="board" aria-hidden="true">
      <div class="row head"><span>Zbor</span><span>Destinație</span><span>Status</span></div>
      <div class="row">
        <span class="code">AS 404</span><span>Pagină necunoscută</span
        ><span class="cancelled">Anulat</span>
      </div>
    </div>
    <h1>Pagina nu există</h1>

    <a class="back" routerLink="/control-tower"
      ><i class="pi pi-arrow-left" aria-hidden="true"></i> Înapoi la Control Tower</a
    >
  </section>`,
  styles: `
    .not-found {
      display: grid;
      justify-items: center;
      gap: 14px;
      max-width: 640px;
      margin: 8vh auto 0;
      text-align: center;
    }
    .board {
      width: 100%;
      margin-bottom: 18px;
      border: 1px solid var(--border);
      border-radius: var(--radius-lg);
      background: var(--text);
      color: var(--on-accent);
      font-family: var(--font-mono);
      text-transform: uppercase;
      text-align: left;
      overflow: hidden;
    }
    .row {
      display: grid;
      grid-template-columns: 1fr 2fr 1fr;
      gap: 12px;
      padding: 16px 20px;
      font-size: 15px;
      letter-spacing: 0.06em;
    }
    .row.head {
      padding-block: 10px;
      border-bottom: 1px solid var(--border);
      color: #d4d4d4;
      font-size: 12px;
      letter-spacing: 0.12em;
    }
    .code {
      color: var(--on-accent);
    }
    .cancelled {
      color: var(--on-accent);
      text-decoration: underline;
      text-decoration-color: var(--signal);
      text-decoration-thickness: 2px;
      text-underline-offset: 4px;
    }
    p {
      color: var(--text-muted);
    }
    .back {
      display: inline-flex;
      align-items: center;
      gap: 8px;
      margin-top: 8px;
      padding: 10px 18px;
      border-radius: var(--radius-sm);
      background: var(--accent);
      color: var(--on-accent);
      font-weight: 600;
      text-decoration: none;
    }
    .back:hover {
      color: var(--on-accent);
      background: var(--accent-hover);
    }
    @media (max-width: 520px) {
      .row {
        grid-template-columns: 1fr 1.6fr;
        font-size: 13px;
      }
      .row > :last-child {
        grid-column: 1 / -1;
      }
    }
  `,
  imports: [RouterLink],
})
export class NotFound {}

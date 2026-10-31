import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';

@Component({
  template: `<section class="panel not-found">
    <p class="code" aria-hidden="true">404</p>
    <h1>Pagina nu există</h1>
    <p>Adresa solicitată nu corespunde unei pagini AeroSynapse.</p>
    <a class="back" routerLink="/control-tower">Înapoi la Control Tower</a>
  </section>`,
  styles: `
    .not-found {
      display: grid;
      justify-items: center;
      gap: 12px;
      padding: 72px 24px;
      text-align: center;
    }
    .code {
      font-size: clamp(64px, 12vw, 112px);
      font-weight: 700;
      letter-spacing: -0.05em;
      line-height: 1;
      background: linear-gradient(180deg, var(--accent), #1f6f5f);
      -webkit-background-clip: text;
      background-clip: text;
      color: transparent;
    }
    p {
      color: var(--text-muted);
    }
    .back {
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
  `,
  imports: [RouterLink],
})
export class NotFound {}

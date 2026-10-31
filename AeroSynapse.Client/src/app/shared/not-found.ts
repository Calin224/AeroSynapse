import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';
@Component({
  template: `<p class="eyebrow">404</p>
    <h1>Pagina nu există</h1>
    <p>Adresa solicitată nu corespunde unei pagini AeroSynapse.</p>
    <a routerLink="/control-tower">Înapoi la Control Tower</a>`,
  imports: [RouterLink],
})
export class NotFound {}

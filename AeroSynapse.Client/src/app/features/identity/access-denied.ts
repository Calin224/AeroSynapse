import { Component } from '@angular/core';
import { RouterLink } from '@angular/router';

@Component({
  selector: 'app-access-denied',
  imports: [RouterLink],
  template: ` <h1>Acces restricționat</h1>
    <p>
      Rolul contului nu permite această acțiune. Viewer poate consulta scenarii și rezultate;
      modificările necesită Operator sau Admin.
    </p>
    <a routerLink="/control-tower">Înapoi la panou</a><a routerLink="/login">Schimbă contul</a>`,
})
export class AccessDenied {}

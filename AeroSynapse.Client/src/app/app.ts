import { afterNextRender, Component, ElementRef, inject, Injector, viewChild } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { AppHeader } from './core/layout/app-header';
import { ResponseStatus } from './core/layout/response-status';
@Component({
  selector: 'app-root',
  imports: [AppHeader, RouterOutlet, ResponseStatus],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App {
  private readonly injector = inject(Injector);
  private readonly mainContent = viewChild.required<ElementRef<HTMLElement>>('mainContent');

  protected routeActivated(): void {
    afterNextRender(
      () => {
        const heading = this.mainContent().nativeElement.querySelector<HTMLElement>('h1');
        if (heading) {
          heading.tabIndex = -1;
          heading.focus();
        }
      },
      { injector: this.injector },
    );
  }

  protected skipToContent(event: Event): void {
    event.preventDefault();
    this.mainContent().nativeElement.focus();
  }
}

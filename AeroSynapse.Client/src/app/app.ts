import { Component, ElementRef, viewChild } from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { AppSidebar } from './core/layout/app-sidebar';
import { ServiceStatus } from './shared/ui/service-status';
@Component({
  selector: 'app-root',
  imports: [AppSidebar, RouterOutlet, ServiceStatus],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App {
  private readonly mainContent = viewChild.required<ElementRef<HTMLElement>>('mainContent');

  protected skipToContent(event: Event): void {
    event.preventDefault();
    this.mainContent().nativeElement.focus();
  }
}

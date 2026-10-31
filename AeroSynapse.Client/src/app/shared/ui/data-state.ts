import { Component, input, output } from '@angular/core';
import { ButtonModule } from 'primeng/button';
import { SkeletonModule } from 'primeng/skeleton';
import { MessageModule } from 'primeng/message';
@Component({
  selector: 'app-data-state',
  imports: [ButtonModule, SkeletonModule, MessageModule],
  template: ` <section class="state" [attr.aria-busy]="status() === 'loading'" aria-live="polite">
    @switch (status()) {
      @case ('loading') {
        <p>Se încarcă {{ label() }}…</p>
        <p-skeleton height="7rem" /><p-skeleton width="65%" height="1rem" />
      }
      @case ('empty') {
        <i class="pi pi-inbox" aria-hidden="true"></i>
        <h2>Nu există date</h2>
        <p>{{ emptyMessage() }}</p>
        <p-button
          label="Reîncarcă"
          icon="pi pi-refresh"
          [outlined]="true"
          (onClick)="retry.emit()"
        />
      }
      @case ('error') {
        <p-message severity="error">{{ message() }}</p-message
        ><p-button label="Încearcă din nou" icon="pi pi-refresh" (onClick)="retry.emit()" />
      }
    }
  </section>`,
  styles: `
    .state {
      display: grid;
      gap: 18px;
      padding: 32px;
      min-height: 240px;
      align-content: center;
    }
    p {
      margin: 0;
      color: var(--muted);
      font-size: 13px;
    }
    i {
      color: var(--accent);
      font-size: 24px;
    }
  `,
})
export class DataState {
  readonly status = input.required<'loading' | 'empty' | 'error'>();
  readonly label = input('datele');
  readonly message = input('Datele nu pot fi încărcate.');
  readonly emptyMessage = input('Datele vor apărea aici când sunt disponibile.');
  readonly retry = output<void>();
}

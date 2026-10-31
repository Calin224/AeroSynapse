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
        <div class="skeletons">
          <p-skeleton height="7rem" /><p-skeleton width="65%" height="1rem" />
        </div>
      }
      @case ('empty') {
        <span class="state-icon"><i class="pi pi-inbox" aria-hidden="true"></i></span>
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
        <span class="state-icon error"
          ><i class="pi pi-exclamation-triangle" aria-hidden="true"></i
        ></span>
        <p-message severity="error">{{ message() || 'Datele nu pot fi încărcate.' }}</p-message
        ><p-button label="Încearcă din nou" icon="pi pi-refresh" (onClick)="retry.emit()" />
      }
    }
  </section>`,
  styles: `
    .state {
      display: grid;
      justify-items: center;
      gap: 14px;
      padding: 40px 24px;
      min-height: 240px;
      align-content: center;
      text-align: center;
    }
    .skeletons {
      display: grid;
      gap: 12px;
      width: 100%;
    }
    p {
      margin: 0;
      max-width: 52ch;
      color: var(--text-muted);
      font-size: 13px;
    }
    .state-icon {
      width: 48px;
      height: 48px;
      display: grid;
      place-items: center;
      border-radius: 50%;
      border: 1px solid var(--accent-line);
      background: var(--accent-soft);
      color: var(--accent);
      font-size: 20px;
    }
    .state-icon.error {
      border-color: var(--danger-line);
      background: var(--danger-soft);
      color: var(--danger);
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

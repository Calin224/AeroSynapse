import { Component, effect, input, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { localDateTime, utcDateTime } from '../absolute-time';

@Component({
  selector: 'app-absolute-time-input',
  imports: [FormsModule],
  template: `
    <label [for]="id"
      >{{ label() }}
      <input
        [id]="id"
        type="datetime-local"
        lang="ro"
        step="any"
        required
        [ngModel]="draft()"
        [ngModelOptions]="{ standalone: true }"
        (ngModelChange)="change($event)"
        [disabled]="disabled()"
        [attr.aria-invalid]="error() ? 'true' : null"
        [attr.aria-describedby]="error() ? id + '-error' : null"
      />
    </label>
    @if (error()) {
      <p [id]="id + '-error'" role="alert">{{ error() }}</p>
    }
  `,
  styles: `
    :host {
      display: block;
      min-width: 0;
    }
    label {
      display: grid;
      gap: 6px;
      font-size: 12px;
      font-weight: 500;
    }
    input {
      width: 100%;
      min-width: 0;
    }
    p {
      margin: 4px 0 0;
      color: var(--danger);
      font-size: 12px;
    }
  `,
})
export class AbsoluteTimeInput {
  private static nextId = 0;
  readonly id = `absolute-time-${++AbsoluteTimeInput.nextId}`;
  readonly value = input<string | null | undefined>('');
  readonly label = input.required<string>();
  readonly timeZone = input('UTC');
  readonly disabled = input(false);
  readonly changed = output<string>();
  readonly draft = signal('');
  readonly error = signal('');
  private lastPublished?: string;
  private zone = '';
  constructor() {
    effect(() => {
      const value = this.value() ?? '',
        timeZone = this.timeZone();
      untracked(() => {
        if (value !== this.lastPublished || timeZone !== this.zone) {
          this.draft.set(localDateTime(value, timeZone));
          this.error.set('');
        }
        this.zone = timeZone;
      });
    });
  }
  change(value: string) {
    this.draft.set(value);
    const utc = utcDateTime(value, this.timeZone(), this.value());
    this.error.set(
      utc
        ? ''
        : value
          ? 'Ora locală este invalidă sau ambiguă la schimbarea orei. Alege o dată și o oră neechivoce.'
          : 'Introdu data și ora (HH:MM).',
    );
    this.lastPublished = utc ?? '';
    this.changed.emit(this.lastPublished);
  }
}

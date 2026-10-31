import { Component, forwardRef, input, output } from '@angular/core';
import { FormsModule } from '@angular/forms';

export type FieldValue =
  string | number | boolean | null | FieldValue[] | { [key: string]: FieldValue };
@Component({
  selector: 'app-object-fields',
  imports: [FormsModule, forwardRef(() => ObjectFields)],
  template: `
    @if (type(value()) === 'object' || type(value()) === 'array') {
      <div class="fields">
        @for (entry of entries(); track entry.key) {
          <div class="entry">
            <label
              >{{ type(value()) === 'array' ? 'Element' : 'Câmp' }}
              <input
                [value]="entry.key"
                (change)="renameInput(entry.key, $event)"
                [disabled]="disabled() || type(value()) === 'array'"
              />
            </label>
            <app-object-fields
              [value]="entry.value"
              [depth]="depth() + 1"
              [disabled]="disabled()"
              (changed)="replace(entry.key, $event)"
            />
            <button
              type="button"
              class="btn-sm"
              [disabled]="disabled()"
              (click)="remove(entry.key)"
            >
              Elimină câmpul {{ entry.key }}
            </button>
          </div>
        }
        @if (depth() < 12) {
          <button type="button" class="btn-sm" [disabled]="disabled()" (click)="add()">
            Adaugă {{ type(value()) === 'array' ? 'element' : 'câmp' }}
          </button>
        }
      </div>
    } @else {
      @switch (type(value())) {
        @case ('boolean') {
          <label
            ><input
              type="checkbox"
              [ngModel]="value()"
              (ngModelChange)="changed.emit($event)"
              [disabled]="disabled()"
              [ngModelOptions]="{ standalone: true }"
            />Activ</label
          >
        }
        @case ('number') {
          <label
            >Valoare numerică<input
              type="number"
              step="any"
              [ngModel]="value()"
              (ngModelChange)="number($event)"
              [disabled]="disabled()"
              [ngModelOptions]="{ standalone: true }"
          /></label>
        }
        @case ('null') {
          <span class="hint">Fără valoare</span>
        }
        @default {
          <label
            >Valoare text<input
              [ngModel]="value()"
              (ngModelChange)="changed.emit($event)"
              [disabled]="disabled()"
              [ngModelOptions]="{ standalone: true }"
          /></label>
        }
      }
    }
    @if (depth() > 0) {
      <label class="kind"
        >Tip<select
          [ngModel]="type(value())"
          (ngModelChange)="changeType($event)"
          [disabled]="disabled()"
          [ngModelOptions]="{ standalone: true }"
        >
          <option value="string">Text</option>
          <option value="number">Număr</option>
          <option value="boolean">Da / nu</option>
          <option value="null">Fără valoare</option>
          <option value="object">Grup de câmpuri</option>
          <option value="array">Listă</option>
        </select></label
      >
    }
  `,
  styles: `
    :host {
      display: block;
      min-width: 0;
    }
    .fields {
      display: grid;
      gap: 0.75rem;
    }
    .entry {
      border-left: 2px solid var(--border-strong);
      padding-left: 0.75rem;
      display: grid;
      gap: 0.5rem;
    }
    label {
      display: grid;
      gap: 0.25rem;
      font-size: 0.85rem;
    }
    input,
    select {
      max-width: 100%;
      width: 100%;
    }
    input[type='checkbox'] {
      width: auto;
    }
    .kind {
      margin: 0.4rem 0;
      color: var(--text-muted);
    }
    .hint {
      color: var(--text-muted);
    }
  `,
})
export class ObjectFields {
  readonly value = input.required<FieldValue>();
  readonly disabled = input(false);
  readonly depth = input(0);
  readonly changed = output<FieldValue>();
  type(value: FieldValue): string {
    return value === null ? 'null' : Array.isArray(value) ? 'array' : typeof value;
  }
  entries(): { key: string; value: FieldValue }[] {
    const value = this.value();
    return value && typeof value === 'object'
      ? Object.entries(value).map(([key, value]) => ({ key, value }))
      : [];
  }
  replace(key: string, value: FieldValue) {
    const current = this.value();
    if (Array.isArray(current)) {
      const copy = [...current];
      copy[Number(key)] = value;
      this.changed.emit(copy);
    } else this.changed.emit({ ...(current as object), [key]: value });
  }
  rename(key: string, next: string) {
    const entries = this.entries();
    if (!next || next === key || entries.some((item) => item.key === next)) return;
    this.changed.emit(
      Object.fromEntries(entries.map((item) => [item.key === key ? next : item.key, item.value])),
    );
  }
  renameInput(key: string, event: Event) {
    const input = event.target as HTMLInputElement;
    if (
      !input.value ||
      this.entries().some((entry) => entry.key === input.value && entry.key !== key)
    ) {
      input.value = key;
      return;
    }
    this.rename(key, input.value);
  }
  remove(key: string) {
    const current = this.value();
    this.changed.emit(
      Array.isArray(current)
        ? current.filter((_, index) => String(index) !== key)
        : Object.fromEntries(
            this.entries()
              .filter((item) => item.key !== key)
              .map((item) => [item.key, item.value]),
          ),
    );
  }
  add() {
    const current = this.value();
    if (Array.isArray(current)) this.changed.emit([...current, '']);
    else {
      let key = 'camp';
      for (let index = 1; this.entries().some((item) => item.key === key); index++)
        key = `camp${index}`;
      this.replace(key, '');
    }
  }
  number(value: number | null) {
    this.changed.emit(value === null || !Number.isFinite(value) ? null : value);
  }
  changeType(kind: string) {
    this.changed.emit(
      (
        { string: '', number: 0, boolean: false, null: null, object: {}, array: [] } as Record<
          string,
          FieldValue
        >
      )[kind],
    );
  }
}

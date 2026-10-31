import { Component, inject, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { SessionState } from '../../core/auth/session-state';

@Component({
  selector: 'app-simulation-journey',
  imports: [RouterLink],
  template: `
    @if (session.canWrite()) {
      <nav aria-label="Parcursul unei simulări">
        @for (item of links; track item.id) {
          <a
            [routerLink]="item.path"
            [queryParams]="scenarioId() ? { scenarioId: scenarioId() } : {}"
            [attr.aria-current]="active() === item.id ? 'page' : null"
          >
            <i [class]="'pi pi-' + item.icon" aria-hidden="true"></i
            >{{ formal() ? formalLabels[item.id] : item.label }}
          </a>
        }
      </nav>
    }
  `,
  styles: `
    :host {
      display: block;
      margin-bottom: 22px;
    }
    nav {
      display: flex;
      flex-wrap: wrap;
      border-block: var(--dp-border-rule) solid var(--text);
      background: var(--text);
    }
    a {
      display: flex;
      align-items: center;
      gap: 8px;
      padding: 12px;
      flex: 1 1 140px;
      min-height: 44px;
      border-radius: var(--radius-sm);
      color: var(--on-accent);
      border-bottom: 3px solid transparent;
      font-size: 14px;
      text-decoration: none;
    }
    a[aria-current] {
      color: var(--accent);
      background: var(--panel);
      border-bottom-color: var(--signal);
      font-weight: 700;
    }
    a:hover {
      color: var(--accent);
      background: var(--surface-hover);
    }
    a:focus-visible {
      outline: 3px solid var(--signal);
      outline-offset: -4px;
    }
    @media (max-width: 700px) {
      a {
        padding: 8px;
        font-size: 12px;
      }
    }
  `,
})
export class SimulationJourney {
  readonly session = inject(SessionState);
  readonly active = input.required<'create' | 'run' | 'compare' | 'repeat'>();
  readonly scenarioId = input<string | null>(null);
  readonly formal = input(false);
  readonly formalLabels: Record<string, string> = {
    create: '1. Configurare',
    run: '2. Simulare',
    compare: '3. Comparație',
    repeat: '4. Testare repetată',
  };
  readonly links = [
    { id: 'create', label: '1. Pregătește', path: '/scenario-studio', icon: 'file-edit' },
    { id: 'run', label: '2. Simulează', path: '/scenario', icon: 'send' },
    { id: 'compare', label: '3. Compară', path: '/compare', icon: 'clone' },
    { id: 'repeat', label: '4. Repetă testul', path: '/batches', icon: 'chart-bar' },
  ];
}

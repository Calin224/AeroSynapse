import { Component } from '@angular/core';
import { RouterLink, RouterLinkActive } from '@angular/router';

@Component({
  selector: 'app-sidebar',
  imports: [RouterLink, RouterLinkActive],
  template: `
    <aside class="sidebar" aria-label="Navigare principală">
      <a class="brand" routerLink="/control-tower" title="AeroSynapse"
        ><span class="brand-mark"><i class="pi pi-send" aria-hidden="true"></i></span
        ><span class="brand-text">AeroSynapse<small>DIGITAL TWIN PLATFORM</small></span></a
      >
      <nav aria-label="Workspace">
        <span class="nav-label" aria-hidden="true">Workspace</span>
        <a
          routerLink="/control-tower"
          routerLinkActive="active"
          ariaCurrentWhenActive="page"
          title="Control Tower"
          ><i class="pi pi-th-large" aria-hidden="true"></i
          ><span class="nav-text">Control Tower</span></a
        >
        <a
          routerLink="/scenario"
          routerLinkActive="active"
          ariaCurrentWhenActive="page"
          title="Scenario"
          ><i class="pi pi-sliders-h" aria-hidden="true"></i
          ><span class="nav-text">Scenario</span></a
        >
      </nav>
      <nav aria-label="Simulări">
        <span class="nav-label" aria-hidden="true">Simulare</span>
        <a
          routerLink="/operations"
          routerLinkActive="active"
          ariaCurrentWhenActive="page"
          title="Scenarii și rulări"
          ><i class="pi pi-play" aria-hidden="true"></i
          ><span class="nav-text">Scenarii și rulări</span></a
        >
      </nav>
      <div class="sidebar-footer">
        <span class="status-dot"></span><strong>Mediu demonstrativ</strong
        ><small>Date sintetice pentru testare</small>
      </div>
    </aside>
  `,
  styles: `
    :host {
      display: contents;
    }
    .sidebar {
      position: sticky;
      top: 0;
      align-self: start;
      height: 100dvh;
      overflow-y: auto;
      display: flex;
      flex-direction: column;
      gap: 28px;
      padding: 24px 16px;
      background: var(--bg-elevated);
      border-right: 1px solid var(--border);
    }
    .brand {
      display: flex;
      gap: 12px;
      align-items: center;
      padding: 0 4px;
      color: var(--text);
      font-size: 19px;
      font-weight: 650;
      letter-spacing: -0.02em;
      text-decoration: none;
    }
    .brand-mark {
      flex: none;
      width: 38px;
      height: 38px;
      display: grid;
      place-items: center;
      border-radius: 11px;
      color: var(--accent);
      background: linear-gradient(145deg, #1d4b45, var(--accent-soft));
      border: 1px solid var(--accent-line);
      box-shadow: 0 6px 18px -6px #59d9ba66;
    }
    .brand-text {
      display: grid;
      line-height: 1.2;
    }
    .brand small {
      margin-top: 4px;
      font-size: 10px;
      font-weight: 500;
      letter-spacing: 0.16em;
      color: var(--text-faint);
    }

    nav {
      display: grid;
      gap: 4px;
    }
    .nav-label {
      padding: 0 12px 6px;
      font-size: 11px;
      font-weight: 600;
      letter-spacing: 0.12em;
      text-transform: uppercase;
      color: var(--text-faint);
    }
    nav a {
      display: flex;
      align-items: center;
      gap: 12px;
      min-height: 42px;
      padding: 0 12px;
      border-radius: var(--radius-sm);
      color: var(--text-muted);
      font-size: 14px;
      font-weight: 500;
      text-decoration: none;
      transition:
        background 0.15s,
        color 0.15s;
    }
    nav a i {
      width: 18px;
      text-align: center;
      font-size: 15px;
    }
    nav a:hover {
      background: var(--surface-raised);
      color: var(--text);
    }
    nav a.active {
      color: var(--accent);
      background: var(--accent-soft);
      box-shadow: inset 3px 0 var(--accent);
    }

    .sidebar-footer {
      margin-top: auto;
      display: grid;
      grid-template-columns: auto 1fr;
      align-items: center;
      padding: 14px;
      border: 1px solid var(--border);
      border-radius: var(--radius);
      background: var(--panel);
      font-size: 13px;
    }
    .sidebar-footer small {
      grid-column: 2;
      margin-top: 2px;
      font-size: 12px;
      color: var(--text-muted);
    }

    /* Tablet: icon rail (the shell grid narrows its first column to match). */
    @media (min-width: 651px) and (max-width: 1023px) {
      .sidebar {
        align-items: center;
        padding: 20px 10px;
      }
      .brand-text,
      .nav-label,
      .sidebar-footer strong,
      .sidebar-footer small {
        display: none;
      }
      .nav-text {
        position: absolute;
        width: 1px;
        height: 1px;
        overflow: hidden;
        clip-path: inset(50%);
      }
      nav {
        width: 100%;
      }
      nav a {
        justify-content: center;
        padding: 0;
      }
      nav a i {
        font-size: 17px;
      }
      .sidebar-footer {
        display: block;
        padding: 12px;
        line-height: 1;
      }
      .sidebar-footer .status-dot {
        margin: 0;
      }
    }

    /* Phone: brand row above tab-style navigation. */
    @media (max-width: 650px) {
      .sidebar {
        z-index: 30;
        height: auto;
        flex-direction: row;
        flex-wrap: wrap;
        align-items: center;
        gap: 10px;
        padding: 12px 16px;
        border-right: 0;
        border-bottom: 1px solid var(--border);
        overflow: visible;
      }
      .brand {
        flex: 1 0 100%;
        font-size: 17px;
      }
      .brand-mark {
        width: 34px;
        height: 34px;
      }
      .brand small,
      .nav-label,
      .sidebar-footer,
      nav a i {
        display: none;
      }
      nav {
        display: contents;
      }
      nav a {
        flex: 1 1 auto;
        justify-content: center;
        min-height: 44px;
        padding: 0 10px;
        font-size: 13px;
        white-space: nowrap;
        border: 1px solid var(--border);
      }
      nav a.active {
        box-shadow: none;
        border-color: var(--accent-line);
      }
    }
  `,
})
export class AppSidebar {}

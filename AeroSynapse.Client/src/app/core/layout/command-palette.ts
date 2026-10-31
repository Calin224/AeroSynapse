import { Component, computed, ElementRef, inject, output, signal, viewChild } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink } from '@angular/router';
import { filter } from 'rxjs';
import { WorkspaceNavigation } from './workspace-navigation';

@Component({
  selector: 'app-command-palette',
  imports: [RouterLink],
  templateUrl: './command-palette.html',
  styleUrl: './command-palette.css',
  host: { '(document:keydown)': 'shortcut($event)' },
})
export class CommandPalette {
  readonly navigation = inject(WorkspaceNavigation);
  private readonly router = inject(Router);
  readonly palette = viewChild.required<ElementRef<HTMLDialogElement>>('palette');
  private readonly searchInput = viewChild.required<ElementRef<HTMLInputElement>>('searchInput');
  readonly opened = output<void>();
  readonly closed = output<void>();
  readonly query = signal('');
  readonly activeResult = signal(0);
  readonly results = computed(() =>
    this.query().trim()
      ? this.navigation.search(this.query())
      : [
          ...this.navigation.recent(),
          ...this.navigation.available().filter((item) => !this.navigation.recent().includes(item)),
        ],
  );
  constructor() {
    this.router.events
      .pipe(
        filter((event) => event instanceof NavigationEnd),
        takeUntilDestroyed(),
      )
      .subscribe(() => this.palette().nativeElement.close());
  }
  openSearch() {
    this.opened.emit();
    this.query.set('');
    this.activeResult.set(0);
    this.palette().nativeElement.showModal();
    this.searchInput().nativeElement.focus();
  }

  shortcut(event: KeyboardEvent) {
    if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 'k' && !event.altKey) {
      if (event.isComposing || document.querySelector('[aria-modal="true"]')) return;
      event.preventDefault();
      if (this.palette().nativeElement.open) this.palette().nativeElement.close();
      else this.openSearch();
    }
  }

  updateQuery(event: Event) {
    this.query.set((event.target as HTMLInputElement).value);
    this.activeResult.set(0);
  }

  searchKey(event: KeyboardEvent) {
    if (event.isComposing) return;
    if (event.key === 'Escape') {
      // Search inputs otherwise consume Escape to clear text before the dialog can close.
      event.preventDefault();
      this.palette().nativeElement.close();
      return;
    }
    const count = this.results().length;
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault();
      if (!count) return;
      this.activeResult.update(
        (index) => (index + (event.key === 'ArrowDown' ? 1 : -1) + count) % count,
      );
      this.palette()
        .nativeElement.querySelector(`#destination-${this.activeResult()}`)
        ?.scrollIntoView({ block: 'nearest' });
    } else if (event.key === 'Enter' && count) {
      event.preventDefault();
      const path = this.results()[this.activeResult()]?.path ?? this.results()[0].path;
      this.closeOnCurrentPage(path);
      void this.router.navigateByUrl(path);
    }
  }

  dismissBackdrop(event: MouseEvent) {
    const dialog = event.currentTarget as HTMLDialogElement;
    if (event.target !== dialog) return;
    const bounds = dialog.getBoundingClientRect();
    if (
      event.clientX < bounds.left ||
      event.clientX > bounds.right ||
      event.clientY < bounds.top ||
      event.clientY > bounds.bottom
    )
      dialog.close();
  }

  closeOnCurrentPage(path: string) {
    if (this.navigation.path() === path) {
      this.palette().nativeElement.close();
    }
  }
}

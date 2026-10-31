import { Directive, effect, ElementRef, inject } from '@angular/core';
import { SessionState } from './session-state';

/** Visible editing controls follow the same roles as server-side write authorization. */
@Directive({ selector: '[appWriteAccess]' })
export class WriteAccess {
  private readonly host = inject(ElementRef<HTMLElement>);
  private readonly session = inject(SessionState);
  private readonly display = this.host.nativeElement.style.display;
  constructor() {
    effect(() => {
      this.host.nativeElement.hidden = !this.session.canWrite();
      this.host.nativeElement.style.display = this.session.canWrite() ? this.display : 'none';
    });
  }
}

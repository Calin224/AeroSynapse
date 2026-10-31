import { Component, ElementRef, inject, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { AuthService } from '../../core/auth/auth-service';

@Component({
  selector: 'app-login',
  imports: [FormsModule],
  templateUrl: './login.html',
  styleUrl: './login.css',
})
export class Login {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly usernameField =
    viewChild.required<ElementRef<HTMLInputElement>>('usernameField');
  private readonly passwordField =
    viewChild.required<ElementRef<HTMLInputElement>>('passwordField');
  readonly passwordVisible = signal(false);
  readonly capsLock = signal(false);

  checkCapsLock(event: KeyboardEvent) {
    this.capsLock.set(event.getModifierState('CapsLock'));
  }

  readonly expired = this.route.snapshot.queryParamMap.has('expired');
  username = '';
  password = '';
  readonly busy = signal(false);
  readonly error = signal('');

  async submit() {
    if (this.busy()) return;
    // Browser autofill can show values without an input event, so the fields are the source of truth.
    this.username = this.usernameField().nativeElement.value;
    this.password = this.passwordField().nativeElement.value;
    if (!this.username.trim() || !this.password) {
      this.error.set('Completează utilizatorul și parola.');
      return;
    }
    this.busy.set(true);
    this.error.set('');
    try {
      await this.auth.login(this.username.trim(), this.password);
      const target = this.route.snapshot.queryParamMap.get('returnUrl') ?? '/control-tower';
      await this.router.navigateByUrl(
        target.startsWith('/') && !target.startsWith('//') && !target.startsWith('/login')
          ? target
          : '/control-tower',
      );
    } catch {
      this.error.set('Autentificarea nu a reușit.');
    } finally {
      this.password = '';
      this.passwordVisible.set(false);
      this.busy.set(false);
    }
  }
}

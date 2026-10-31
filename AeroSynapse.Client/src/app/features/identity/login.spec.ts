import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { AuthService } from '../../core/auth/auth-service';
import { Login } from './login';

describe('Login form', () => {
  let login: ReturnType<typeof vi.fn>;
  beforeEach(() => {
    login = vi.fn().mockResolvedValue(undefined);
    TestBed.configureTestingModule({
      imports: [Login],
      providers: [provideRouter([]), { provide: AuthService, useValue: { login } }],
    });
  });

  async function render() {
    const fixture = TestBed.createComponent(Login);
    await fixture.whenStable();
    const host = fixture.nativeElement as HTMLElement;
    return {
      fixture,
      host,
      field: (id: string) => host.querySelector<HTMLInputElement>('#' + id)!,
      submit: () => host.querySelector('form')!.dispatchEvent(new Event('submit')),
    };
  }

  it('keeps Enter available while idle and explains empty fields instead of ignoring them', async () => {
    const { fixture, host, submit } = await render();
    // A disabled default button makes the browser drop Enter, e.g. right after a paste.
    expect(host.querySelector<HTMLButtonElement>('.login-submit')!.disabled).toBe(false);
    submit();
    await fixture.whenStable();
    expect(login).not.toHaveBeenCalled();
    expect(host.querySelector('[role="alert"]')!.textContent).toContain(
      'Completează utilizatorul și parola.',
    );
  });

  it('signs in with values the browser autofilled without an input event', async () => {
    const { fixture, field, submit } = await render();
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    field('username').value = ' operator.demo ';
    field('password').value = 'filled-by-the-browser';
    submit();
    await fixture.whenStable();
    expect(login).toHaveBeenCalledWith('operator.demo', 'filled-by-the-browser');
    expect(navigate).toHaveBeenCalledWith('/control-tower');
  });
});

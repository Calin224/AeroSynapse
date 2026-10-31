import { TestBed } from '@angular/core/testing';
import { AbsoluteTimeInput } from './absolute-time-input';

describe('Absolute time input', () => {
  function component(value = '2026-10-31T22:30:00Z') {
    const fixture = TestBed.createComponent(AbsoluteTimeInput);
    fixture.componentRef.setInput('label', 'Ora plecării');
    fixture.componentRef.setInput('timeZone', 'Europe/Bucharest');
    fixture.componentRef.setInput('value', value);
    fixture.detectChanges();
    return fixture;
  }

  it('shows an absolute calendar value and emits the matching UTC instant through the form', async () => {
    const fixture = component();
    await fixture.whenStable();
    const control: HTMLInputElement = fixture.nativeElement.querySelector('input');
    const changed = vi.fn();
    fixture.componentInstance.changed.subscribe(changed);
    expect(control.type).toBe('datetime-local');
    expect(control.value).toBe('2026-11-01T00:30');
    control.value = '2026-11-01T01:30';
    control.dispatchEvent(new Event('input'));
    await fixture.whenStable();
    expect(changed).toHaveBeenCalledWith('2026-10-31T23:30:00.000Z');
  });

  it('keeps an invalid wall clock visible with an accessible error instead of silently changing the hour', async () => {
    const fixture = component('2026-03-29T00:30:00Z');
    const changed = vi.fn();
    fixture.componentInstance.changed.subscribe(changed);
    fixture.componentInstance.change('2026-03-29T03:30');
    fixture.componentRef.setInput('value', '');
    fixture.detectChanges();
    await fixture.whenStable();
    const control: HTMLInputElement = fixture.nativeElement.querySelector('input');
    expect(changed).toHaveBeenCalledWith('');
    expect(control.value).toBe('2026-03-29T03:30');
    expect(control.getAttribute('aria-invalid')).toBe('true');
    expect(fixture.nativeElement.querySelector('[role=alert]').textContent).toContain(
      'invalidă sau ambiguă',
    );
    fixture.componentInstance.change('2026-03-29T04:30');
    fixture.detectChanges();
    expect(changed).toHaveBeenLastCalledWith('2026-03-29T01:30:00.000Z');
    expect(control.hasAttribute('aria-invalid')).toBe(false);
  });

  it('clears the timestamp when emptied and follows external timezone changes', async () => {
    const fixture = component();
    const changed = vi.fn();
    fixture.componentInstance.changed.subscribe(changed);
    fixture.componentInstance.change('');
    expect(changed).toHaveBeenCalledWith('');
    expect(fixture.componentInstance.error()).toContain('HH:MM');
    fixture.componentRef.setInput('timeZone', 'UTC');
    fixture.componentRef.setInput('disabled', true);
    fixture.detectChanges();
    await fixture.whenStable();
    const control: HTMLInputElement = fixture.nativeElement.querySelector('input');
    expect(control.value).toBe('2026-10-31T22:30');
    expect(control.disabled).toBe(true);
    expect(fixture.componentInstance.error()).toBe('');
  });
});

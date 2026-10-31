import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { AbsoluteTimeInput } from '../../shared/ui/absolute-time-input';
import { FieldValue, ObjectFields } from './object-fields';

describe('Absolute arrival profile editor', () => {
  const profile = {
    batches: [
      {
        flightId: 'flight',
        arrival: { kind: 'uniform', minimum: 30, maximum: 60, mean: 45, mode: 45, stdDev: 5 },
        service: { mean: 2 },
      },
    ],
  };
  function component(value: FieldValue = structuredClone(profile)) {
    const fixture = TestBed.createComponent(ObjectFields);
    fixture.componentRef.setInput('value', value);
    fixture.componentRef.setInput('arrivalStartUtc', '2026-10-31T21:30:00Z');
    fixture.componentRef.setInput('timeZone', 'Europe/Bucharest');
    fixture.detectChanges();
    return fixture;
  }
  it('shows arrival positions as calendar times while service and spread remain numeric durations', () => {
    const fixture = component();
    const times = fixture.debugElement
      .queryAll(By.directive(AbsoluteTimeInput))
      .map((item) => item.componentInstance as AbsoluteTimeInput);
    expect(times.map((time) => time.label())).toEqual([
      'Început sosiri',
      'Sfârșit sosiri',
      'Ora medie a sosirii',
      'Ora cea mai frecventă a sosirii',
    ]);
    expect(times.map((time) => time.draft())).toEqual([
      '2026-11-01T00:00',
      '2026-11-01T00:30',
      '2026-11-01T00:15',
      '2026-11-01T00:15',
    ]);
    const numbers = [
      ...(fixture.nativeElement as HTMLElement).querySelectorAll<HTMLInputElement>(
        'input[type=number]',
      ),
    ];
    expect(numbers).toHaveLength(2);
    expect(profile.batches[0].arrival.stdDev).toBe(5);
  });
  it('adapts an edited UTC instant to the legacy contract without changing other profile values', () => {
    const fixture = component(),
      changed = vi.fn();
    fixture.componentInstance.changed.subscribe(changed);
    const minimum = fixture.debugElement.queryAll(By.directive(AbsoluteTimeInput))[0]
      .componentInstance as AbsoluteTimeInput;
    minimum.change('2026-11-01T00:10');
    expect(changed).toHaveBeenLastCalledWith({
      batches: [{ ...profile.batches[0], arrival: { ...profile.batches[0].arrival, minimum: 40 } }],
    });
    minimum.change('');
    expect(changed).toHaveBeenLastCalledWith({
      batches: [
        { ...profile.batches[0], arrival: { ...profile.batches[0].arrival, minimum: null } },
      ],
    });
  });
  it('keeps unrelated numeric minimum and maximum fields unchanged', () => {
    const fixture = component({ navigation: { minimum: 30, maximum: 60 } });
    expect(fixture.debugElement.queryAll(By.directive(AbsoluteTimeInput))).toHaveLength(0);
    expect(fixture.nativeElement.querySelectorAll('input[type=number]')).toHaveLength(2);
  });
});

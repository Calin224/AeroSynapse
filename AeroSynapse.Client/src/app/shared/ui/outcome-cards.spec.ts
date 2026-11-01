import { TestBed } from '@angular/core/testing';
import { formatWait, OutcomeCards, percentChange, signedPercent } from './outcome-cards';

describe('Outcome precision', () => {
  it('keeps a ten percent change visible for waits close to two seconds', () => {
    expect(formatWait(2 / 60)).toBe('2.00 s');
    expect(formatWait(1.8 / 60)).toBe('1.80 s');
    expect(signedPercent(percentChange(2, 1.8))).toBe('−10.00%');
    expect(formatWait(2)).toBe('2.00 min');
    expect(formatWait(null)).toBe('—');
    expect(signedPercent(null)).toBe('—');
    expect(signedPercent(0.125)).toBe('+0.13%');
    expect(signedPercent(-0.125)).toBe('−0.13%');
  });
  it('shows two decimal places without changing the input numbers', () => {
    const fixture = TestBed.createComponent(OutcomeCards);
    fixture.componentRef.setInput('rows', [{key:'wait', label:'Așteptare', unit:'secunde', before:2, after:1.8, percent:-10}]);
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('2.00');
    expect(fixture.nativeElement.textContent).toContain('1.80');
    expect(fixture.componentInstance.cards()[0].after).toBe(1.8);
  });
});

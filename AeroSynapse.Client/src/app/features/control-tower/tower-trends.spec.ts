import { TestBed } from '@angular/core/testing';
import { TowerTrends } from './tower-trends';

const points = [
  { minute: 0, queued: 20, processed: 0 },
  { minute: 2, queued: 9, processed: 11 },
  { minute: 9, queued: 3, processed: 17 },
];
describe('Interactive terminal trends', () => {
  function setup(rows = points) {
    const fixture = TestBed.createComponent(TowerTrends);
    fixture.componentRef.setInput('points', rows);
    fixture.componentRef.setInput('contextKey', 'run-one');
    fixture.detectChanges();
    return fixture;
  }
  it('starts at the latest actual observation and scrubs both metrics together', () => {
    const fixture = setup(),
      component = fixture.componentInstance;
    expect(component.selected()).toEqual(points[2]);
    const slider = fixture.nativeElement.querySelector('input') as HTMLInputElement;
    slider.value = '1';
    slider.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    expect(component.selected()).toEqual(points[1]);
    expect(slider.getAttribute('aria-valuetext')).toContain('9 în coadă, 11 procesați');
    expect(fixture.nativeElement.querySelectorAll('svg')[0].getAttribute('aria-label')).toContain(
      '9 pasageri la minutul 2',
    );
  });
  it('keeps an inspected observation during updates but resets on a different run', () => {
    const fixture = setup(),
      component = fixture.componentInstance;
    component.selectedMinute.set(2);
    fixture.componentRef.setInput('points', [...points, { minute: 10, queued: 0, processed: 20 }]);
    fixture.detectChanges();
    expect(component.selected()?.minute).toBe(2);
    fixture.componentRef.setInput('contextKey', 'run-two');
    fixture.detectChanges();
    expect(component.selected()?.minute).toBe(10);
    expect(component.selectedMinute()).toBeNull();
  });
  it('uses the nearest timestamp for non-uniform observations', () => {
    const fixture = setup(),
      component = fixture.componentInstance;
    component.inspect({
      clientX: 250,
      currentTarget: { getBoundingClientRect: () => ({ left: 0, width: 500 }) },
    } as unknown as PointerEvent);
    expect(component.selected()?.minute).toBe(2);
    component.inspect({
      clientX: 450,
      currentTarget: { getBoundingClientRect: () => ({ left: 0, width: 500 }) },
    } as unknown as PointerEvent);
    expect(component.selected()?.minute).toBe(9);
  });
  it('handles empty and single-observation data without inventing a chart or dividing by zero', () => {
    const fixture = setup([]);
    expect(fixture.nativeElement.querySelector('svg')).toBeNull();
    expect(fixture.componentInstance.selected()).toBeUndefined();
    fixture.componentRef.setInput('points', [{ minute: 0, queued: 0, processed: 0 }]);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('input').disabled).toBe(true);
    expect(fixture.componentInstance.plots().queued.path).toBe('8,82');
  });
});

import { TestBed } from '@angular/core/testing';
import { FlightTimelineView } from './flight-timeline';
import type { FlightTimeline, TimelineFlight } from './workflow.models';

const flight = (id: string, cancelled = false): TimelineFlight => ({
  id,
  code: id.toUpperCase(),
  gate: 'G-1',
  passengers: 50,
  departure: '13:45',
  departureAt: 75,
  arrivals: { left: 10, width: 40 },
  arrivalPeak: cancelled ? null : 30,
  arrivalsLabel: '13:10–13:30',
  boarding: null,
  cancelled,
});
const line: FlightTimeline = {
  ticks: [{ left: 0, label: '13:00' }],
  flights: [flight('ro1'), flight('ro2', true)],
  surge: { flightId: 'ro1', left: 25, width: 5, label: '13:15–13:20' },
};

describe('Flight timeline', () => {
  it('marks the chosen flight, keeps cancelled flights unpickable and reports picks', () => {
    const fixture = TestBed.createComponent(FlightTimelineView);
    fixture.componentRef.setInput('line', line);
    fixture.componentRef.setInput('selected', 'ro1');
    const picked: string[] = [];
    fixture.componentInstance.pick.subscribe((id) => picked.push(id));
    fixture.detectChanges();
    const [first, second] = fixture.nativeElement.querySelectorAll('button.flight');
    expect(first.getAttribute('aria-pressed')).toBe('true');
    expect(first.querySelector('.surge')).not.toBeNull();
    expect(second.disabled).toBe(true);
    // Arrivals peak where most passengers come; even arrivals stay a flat band.
    expect(first.querySelector('.arrivals path').getAttribute('d')).toContain('50,0');
    expect(second.querySelector('.arrivals path').getAttribute('d')).toContain('L100,45');
    expect(first.getAttribute('aria-label')).toContain('sosiri 13:10–13:30');
    first.click();
    expect(picked).toEqual(['ro1']);
    fixture.componentRef.setInput('disabled', true);
    fixture.detectChanges();
    expect(first.disabled).toBe(true);
  });
});

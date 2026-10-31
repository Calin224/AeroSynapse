import { TestBed } from '@angular/core/testing';
import { PassengerPlayback } from './passenger-playback';
import { congestionRunFixture } from './heatmap.testing';
import { PassengerPacket } from './passenger-model';

describe('Congestion on the simulation playback slider', () => {
  it('colors the whole timeline, follows the cursor state and replaces colors when the run changes', async () => {
    const fixture = TestBed.createComponent(PassengerPlayback);
    const run = congestionRunFixture();
    const before = JSON.stringify(run);
    fixture.componentRef.setInput('run', run);
    fixture.componentRef.setInput('timelineMinutes', [0, 2, 5, 7]);
    fixture.componentRef.setInput('minute', 0);
    await fixture.whenStable();
    const slider = fixture.nativeElement.querySelector('input[type="range"]') as HTMLInputElement;
    const track = () => slider.style.getPropertyValue('--congestion-track');
    expect(track()).toContain('#41d3a2');
    expect(track()).toContain('#fa665c');
    expect(track()).toContain('#ffc45b');
    expect(slider.getAttribute('data-congestion-band')).toBe('low');
    let packet: PassengerPacket | undefined;
    fixture.componentInstance.packetChange.subscribe((value) => (packet = value));
    slider.value = '3';
    slider.dispatchEvent(new Event('input'));
    await fixture.whenStable();
    expect(slider.getAttribute('data-congestion-band')).toBe('critical');
    expect(slider.getAttribute('aria-valuetext')).toContain('Critic');
    expect(slider.style.getPropertyValue('--congestion-thumb')).toBe('#fa665c');
    expect(packet).toMatchObject({ timeMinutes: 3, snapshotMinute: 2 });
    expect(JSON.stringify(run)).toBe(before);
    fixture.componentRef.setInput('run', {
      ...run,
      runId: 'optimized-run',
      snapshots: run.snapshots.map((row) => ({ ...row, queuedPassengers: 0 })),
    });
    await fixture.whenStable();
    expect(track()).not.toContain('#fa665c');
    expect(track()).not.toContain('#ffc45b');
    expect(slider.getAttribute('data-congestion-band')).toBe('low');
    fixture.destroy();
  });
});

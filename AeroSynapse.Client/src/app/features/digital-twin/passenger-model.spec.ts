import { PassengerClock, preparePassengerReplay, samplePassenger } from './passenger-model';
import { motionFixture } from './passenger.testing';
import { datasetFixture } from './heatmap.testing';

describe('Simulator passenger playback model', () => {
  it('interpolates along the supplied polyline corners and uses the exact simulator queue position', () => {
    const run = motionFixture(),
      original = JSON.stringify(run),
      replay = preparePassengerReplay(run);
    const track = replay.tracks[0];
    expect(samplePassenger(track, replay, 1.25)?.position).toEqual([5, 0.45, 0]);
    expect(samplePassenger(track, replay, 1.75)?.position).toEqual([10, 0.45, 5]);
    expect(samplePassenger(track, replay, 3.5)).toMatchObject({
      position: [10, 0.45, 12],
      state: 'queued',
    });
    expect(samplePassenger(track, replay, 0.5)).toBeNull();
    expect(samplePassenger(track, replay, 5)).toBeNull();
    expect(samplePassenger(track, replay, 6)).toBeNull();
    expect(JSON.stringify(run)).toBe(original);
  });
  it('uses aggregate snapshots when trajectories are absent or incompatible and rejects malformed paths', () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    expect(preparePassengerReplay(datasetFixture().baseline).mode).toBe('aggregate');
    const run = motionFixture();
    run.passengerMotion!.runId = 'another-run';
    expect(preparePassengerReplay(run).mode).toBe('aggregate');
    run.passengerMotion!.runId = run.runId;
    run.passengerMotion!.trajectories[0].keyframes[1].routeId = 'unknown-route';
    const invalid = preparePassengerReplay(run);
    expect(invalid.mode).toBe('aggregate');
    expect(invalid.rejectedTracks).toBe(1);
    expect(warn).toHaveBeenCalled();
    expect(preparePassengerReplay({ ...run, snapshots: [], passengerMotion: undefined }).mode).toBe(
      'unavailable',
    );
  });
  it('does not invent a connecting path when successive keyframes switch routes', () => {
    const run = motionFixture();
    run.passengerMotion!.routes.push({
      routeId: 'separate-route',
      zoneIds: [run.capacities[0].zoneId],
      points: [
        [100, 0.45, 100],
        [110, 0.45, 100],
      ],
    });
    run.passengerMotion!.trajectories[0].keyframes[1].routeId = 'separate-route';
    const replay = preparePassengerReplay(run);
    expect(samplePassenger(replay.tracks[0], replay, 1.5)?.position).toEqual([0, 0.45, 0]);
    expect(samplePassenger(replay.tracks[0], replay, 2)?.position).toEqual([110, 0.45, 100]);
  });
  it('runs at real-time 1x/4x/16x, pauses, resumes and seeks without changing source data', () => {
    const clock = new PassengerClock();
    clock.configure(1, 5);
    clock.seek(2);
    clock.play();
    clock.step(0);
    clock.step(1000);
    expect(clock.time).toBeCloseTo(2 + 1 / 60);
    clock.speed = 4;
    clock.step(2000);
    expect(clock.time).toBeCloseTo(2 + 5 / 60);
    clock.pause();
    const held = clock.time;
    clock.step(5000);
    expect(clock.time).toBe(held);
    clock.play();
    clock.step(6000);
    clock.speed = 16;
    clock.step(7000);
    expect(clock.time).toBeCloseTo(held + 16 / 60);
    clock.seek(3.25);
    clock.step(8000);
    expect(clock.time).toBe(3.25);
    clock.seek(4.9);
    clock.step(9000);
    clock.step(10000);
    expect(clock.time).toBe(5);
    expect(clock.playing).toBe(false);
  });
});

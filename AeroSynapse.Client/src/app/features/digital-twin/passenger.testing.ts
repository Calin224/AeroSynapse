import { datasetFixture } from './heatmap.testing';
import { HeatmapRun } from './heatmap-model';
/** Synthetic consumer contract harness, never served as actual simulator output. */
export function motionFixture(count = 1): HeatmapRun {
  const run = datasetFixture().baseline,
    zoneId = run.capacities[0].zoneId;
  return {
    ...run,
    passengerMotion: {
      runId: run.runId,
      layoutId: run.layoutId,
      source: 'simulator',
      units: 'meters',
      upAxis: 'Y',
      routes: [
        {
          routeId: 'sim-route',
          zoneIds: [zoneId],
          points: [
            [0, 0.45, 0],
            [10, 0.45, 0],
            [10, 0.45, 10],
          ],
        },
      ],
      trajectories: Array.from({ length: count }, (_, i) => ({
        agentId: `test-agent-${i}`,
        keyframes: [
          { elapsedMinutes: 1, routeId: 'sim-route', zoneId, progress: 0, state: 'walking' },
          { elapsedMinutes: 2, routeId: 'sim-route', zoneId, progress: 1, state: 'walking' },
          {
            elapsedMinutes: 3,
            routeId: 'sim-route',
            zoneId,
            progress: 1,
            state: 'queued',
            queuePosition: [10, 0.45, 12],
          },
          {
            elapsedMinutes: 4,
            routeId: 'sim-route',
            zoneId,
            progress: 1,
            state: 'queued',
            queuePosition: [10, 0.45, 12],
          },
          { elapsedMinutes: 5, routeId: 'sim-route', zoneId, progress: 1, state: 'exited' },
        ],
      })),
    },
  };
}

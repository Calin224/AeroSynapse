import { QueueForecastResult, QueueForecastSeries } from './queue-forecast.models';
import { niceScale, outlooks, projectWithExtraStaff, queueChart, withObservation } from './queue-forecast.view';

function series(id: string, queues: number[], alert: QueueForecastSeries['alert'] = null, extraStaff = 0): QueueForecastSeries {
  return { checkpointId: id, initialQueuePassengers: 4, serviceRatePerStaffMinute: 2, serviceRateSource: 'configured_service_rate',
    points: queues.map((queuePassengers, index) => ({ minute: 11 + index, arrivals: 6, capacity: 2, queuePassengers, availableStaff: 1 })),
    alert, mitigation: { extraStaff, peakQueuePassengers: extraStaff ? 4 : Math.max(...queues) } };
}
function result(rows: QueueForecastSeries[], threshold: number | null = 10): QueueForecastResult {
  return { runId: 'run', kind: 'simulated_estimate', formula: 'Q[t+1]=max(0,Q[t]+arrivals-capacity)', fromMinute: 10, toMinute: 25,
    periodStartUtc: '', periodEndUtc: '', queueUnit: 'passengers', flowUnit: 'passengers/minute', timeUnit: 'simulated minutes',
    queueThresholdPassengers: threshold, arrivalSource: 'configured_arrival_rate', observationSource: 'captured_snapshot', checkpoints: rows };
}

describe('Queue forecast view model', () => {
  it('ranks breaches first by how soon they happen, then near misses, then clear checkpoints', () => {
    const rows = outlooks(result([
      series('clear', Array(15).fill(2)),
      series('late', [...Array(10).fill(5), ...Array(5).fill(12)], { minute: 21, queuePassengers: 12, thresholdPassengers: 10 }, 1),
      series('near', Array(15).fill(9)),
      series('soon', Array(15).fill(12), { minute: 11, queuePassengers: 12, thresholdPassengers: 10 }, 2),
    ]), {});
    expect(rows.map(row => [row.id, row.risk, row.breachInMinutes])).toEqual([
      ['soon', 'breach', 1], ['late', 'breach', 11], ['near', 'near', null], ['clear', 'clear', null]]);
  });
  it('redraws the service mitigation with the published recurrence and refuses an inconsistent one', () => {
    const row = series('a', [8, 12, 16], { minute: 11, queuePassengers: 8, thresholdPassengers: 4 }, 2);
    expect(projectWithExtraStaff(row, 2).map(sample => sample.queue)).toEqual([4, 4, 4]);
    expect(outlooks(result([row], 4), {})[0].mitigated).not.toBeNull();
    expect(outlooks(result([{ ...row, mitigation: { extraStaff: 2, peakQueuePassengers: 3 } }], 4), {})[0].mitigated).toBeNull();
  });
  it('keeps observations and forecasts apart and never treats live frames as backtest evidence', () => {
    let history = withObservation({}, 9, [{ zoneId: 'a', queuedPassengers: 3, processedPassengers: 0 }]);
    history = withObservation(history, 12, [{ zoneId: 'a', queuedPassengers: 7, processedPassengers: 0 }]);
    const captured = outlooks(result([series('a', Array(15).fill(5))]), history)[0];
    expect(captured.observed).toEqual([{ minute: 9, queue: 3 }, { minute: 10, queue: 4 }]);
    expect(captured.actual).toEqual([{ minute: 12, queue: 7 }]); expect(captured.backtestError).toBe(2);
    const live = outlooks({ ...result([series('a', Array(15).fill(5))]), observationSource: 'live_simulation' }, history)[0];
    expect(live.actual).toEqual([]);
    const chart = queueChart(captured, result([series('a', Array(15).fill(5))]));
    expect(chart.nowX).toBeGreaterThan(chart.left); expect(chart.thresholdY).toBeLessThan(chart.bottom);
  });
  it('chooses readable axis steps', () => {
    expect(niceScale(23)).toEqual({ max: 30, step: 10 }); expect(niceScale(9)).toEqual({ max: 10, step: 2.5 }); expect(niceScale(0)).toEqual({ max: 1, step: .25 });
  });
});

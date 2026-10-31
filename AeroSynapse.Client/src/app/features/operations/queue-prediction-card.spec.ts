import { TestBed } from '@angular/core/testing';
import { QueuePredictionCard } from './queue-prediction-card';
import { QueueForecastResult, QueuePredictionResult } from './queue-forecast.models';

const forecast = { runId: 'run-1', fromMinute: 0, toMinute: 15, queueThresholdPassengers: 20, observationSource: 'captured_snapshot', checkpoints: [] } as unknown as QueueForecastResult;
function prediction(kind: 'ml' | 'deterministic', fallbackReason: QueuePredictionResult['predictor']['fallbackReason'] = null): QueuePredictionResult {
  const ml = kind === 'ml';
  return {
    runId: 'run-1', kind: 'simulated_estimate', validity: 'synthetic_only: no operational accuracy is claimed without authorised real data',
    fromMinute: 0, toMinute: 15, queueThresholdPassengers: 20, observationSource: 'captured_snapshot',
    predictor: { kind, modelVersion: ml ? 'us038-residual-cqr-calibrated-2.0+0123456789abcdef' : 'us036-fluid-1.0', estimator: ml ? 'hgb_shallow' : null,
      datasetVersion: ml ? 'us038-synthetic-2.0' : null, datasetHash: ml ? 'c3e77ffd17a9' + '0'.repeat(52) : null,
      trainingDataSource: ml ? 'synthetic_simpy_simulation' : null, formula: ml ? null : 'Q[t+1]=max(0,Q[t]+arrivals-capacity)', fallbackReason },
    checkpoints: [
      { checkpointId: 'control-1', deterministicQueuePassengers: 30, predictedQueuePassengers: ml ? 34.2 : 30,
        predictionInterval: ml ? { lowerPassengers: 22, upperPassengers: 41, nominalCoverage: 0.8 } : null, exceedanceProbability: ml ? 0.91 : null },
      { checkpointId: 'control-2', deterministicQueuePassengers: 4, predictedQueuePassengers: ml ? 3 : 4,
        predictionInterval: ml ? { lowerPassengers: 0, upperPassengers: 9, nominalCoverage: 0.8 } : null, exceedanceProbability: ml ? 0.02 : null },
    ],
  };
}

describe('Queue prediction card', () => {
  function render(value: QueuePredictionResult | null, state: 'idle' | 'loading' | 'failed' = 'idle') {
    const fixture = TestBed.createComponent(QueuePredictionCard);
    fixture.componentRef.setInput('prediction', value); fixture.componentRef.setInput('forecast', forecast);
    fixture.componentRef.setInput('names', { 'control-1': 'Control Nord' }); fixture.componentRef.setInput('state', state);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }
  it('compares ML with the deterministic forecast and shows model, dataset and data source', () => {
    const element = render(prediction('ml')); const text = element.textContent!;
    const rows = [...element.querySelectorAll('tbody tr')].map(row => row.textContent);
    expect(rows[0]).toContain('Control Nord'); expect(rows[0]).toContain('34.2'); expect(rows[0]).toContain('+4.2'); expect(rows[0]).toContain('91%');
    expect(rows[0]).toContain('22–41'); expect(rows[1]).toContain('Checkpoint control-');
    expect(text).toContain('us038-residual-cqr-calibrated-2.0+0123456789abcdef'); expect(text).toContain('P(coadă > 20)');

  });
  it.each([['predictor_disabled', 'QUEUE_ML_PREDICTOR=disabled'], ['model_warming', 'se antrenează'], ['model_unavailable', 'a eșuat']] as const)(
    'explains the deterministic fallback for %s', (reason, expected) => {
      const element = render(prediction('deterministic', reason));
      expect(element.textContent).toContain(expected); expect(element.querySelector('table')).toBeNull();
    });
  it('omits probabilities without a threshold and reports loading or failure without inventing values', () => {
    const value = prediction('ml'); value.queueThresholdPassengers = null;
    value.checkpoints.forEach(row => row.exceedanceProbability = null);
    expect(render(value).textContent).not.toContain('P(coadă');
    expect(render(null, 'loading').textContent).toContain('Se calculează');
    expect(render(null, 'failed').textContent).toContain('forecast-ul determinist de mai sus rămâne valid');
  });
});

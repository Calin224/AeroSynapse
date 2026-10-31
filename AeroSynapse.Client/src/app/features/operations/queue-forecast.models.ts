export interface QueueForecastOptions {
  fromMinute: number;
  horizonMinutes: number;
  queueThresholdPassengers: number | null;
}
export interface QueueForecastPoint {
  minute: number;
  arrivals: number;
  capacity: number;
  queuePassengers: number;
  availableStaff: number;
}
/** Smallest extra staffing on open lanes that keeps every estimated minute at or below the threshold; nulls mean staffing alone cannot. */
export interface QueueForecastMitigation {
  extraStaff: number | null;
  peakQueuePassengers: number | null;
}
export interface QueueForecastSeries {
  checkpointId: string;
  initialQueuePassengers: number;
  serviceRatePerStaffMinute: number;
  serviceRateSource: 'sampled_service_mean' | 'configured_service_rate';
  points: QueueForecastPoint[];
  alert: { minute: number; queuePassengers: number; thresholdPassengers: number } | null;
  mitigation: QueueForecastMitigation | null;
}
export interface QueueForecastResult {
  runId: string;
  kind: 'simulated_estimate';
  formula: string;
  fromMinute: number;
  toMinute: number;
  periodStartUtc: string;
  periodEndUtc: string;
  queueUnit: 'passengers';
  flowUnit: 'passengers/minute';
  timeUnit: 'simulated minutes';
  queueThresholdPassengers: number | null;
  arrivalSource: 'planned_passenger_profiles' | 'configured_arrival_rate';
  observationSource: 'live_simulation' | 'captured_snapshot';
  checkpoints: QueueForecastSeries[];
}
/** Research predictor. `deterministic` repeats the forecast; `ml` was trained on synthetic simulations only. */
export interface QueuePredictor {
  kind: 'ml' | 'deterministic';
  modelVersion: string;
  estimator: string | null;
  datasetVersion: string | null;
  datasetHash: string | null;
  trainingDataSource: 'synthetic_simpy_simulation' | null;
  formula: string | null;
  fallbackReason: 'predictor_disabled' | 'model_warming' | 'model_unavailable' | null;
}
export interface QueuePredictionCheckpoint {
  checkpointId: string;
  deterministicQueuePassengers: number;
  predictedQueuePassengers: number;
  predictionInterval: { lowerPassengers: number; upperPassengers: number; nominalCoverage: number } | null;
  exceedanceProbability: number | null;
}
export interface QueuePredictionResult {
  runId: string;
  kind: 'simulated_estimate';
  validity: string;
  fromMinute: number;
  toMinute: number;
  queueThresholdPassengers: number | null;
  observationSource: 'live_simulation' | 'captured_snapshot';
  predictor: QueuePredictor;
  checkpoints: QueuePredictionCheckpoint[];
}

"""Prediction wire invariants, mirrored by QueuePredictionValidation.cs.

A deterministic answer is exactly the forecast with no model provenance; an ML answer
carries its full provenance, an ordered interval, and a probability only when a threshold is set.
"""
DETERMINISTIC_VERSION = 'us036-fluid-1.0'
CLAIM = 'synthetic_only: no operational accuracy is claimed without authorised real data'
PROVENANCE = ('estimator', 'datasetVersion', 'datasetHash', 'trainingDataSource')


def valid_prediction(value):
    predictor, rows = value['predictor'], value['checkpoints']
    horizon = value['toMinute'] - value['fromMinute']
    if not 15 <= horizon <= 30 or len({row['checkpointId'] for row in rows}) != len(rows):
        return False
    if predictor['kind'] == 'deterministic':
        return (predictor['modelVersion'] == DETERMINISTIC_VERSION and predictor['formula'] is not None
                and predictor['fallbackReason'] is not None and all(predictor[key] is None for key in PROVENANCE)
                and all(row['predictedQueuePassengers'] == row['deterministicQueuePassengers'] and row['predictionInterval'] is None
                        and row['exceedanceProbability'] is None for row in rows))
    return (predictor['modelVersion'] != DETERMINISTIC_VERSION and predictor['formula'] is None and predictor['fallbackReason'] is None
            and all(predictor[key] is not None for key in PROVENANCE)
            and all(row['predictionInterval'] is not None
                    and row['predictionInterval']['lowerPassengers'] <= row['predictionInterval']['upperPassengers']
                    and (row['exceedanceProbability'] is None) == (value['queueThresholdPassengers'] is None) for row in rows))


def validate_prediction(value, path, errors, error):
    if not valid_prediction(value):
        errors.append(error(path, 'invalid_result'))

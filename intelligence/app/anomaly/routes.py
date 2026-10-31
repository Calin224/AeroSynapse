from fastapi import APIRouter, Request
from fastapi.responses import JSONResponse

from app.anomaly.detector import detect
from app.anomaly.evaluation import case_detail, evaluation
from app.contracts.routes import openapi_schema
from app.simulation.routes import execute

router = APIRouter(tags=['Anomaly v1'])


def body(definition):
    return {'requestBody': {'required': True, 'content': {'application/json': {'schema': openapi_schema(definition)}}}}


def reply(definition):
    return {200: {'content': {'application/json': {'schema': openapi_schema(definition)}}}}


@router.post('/anomaly/detect', response_model=None, openapi_extra=body('AnomalyDetectionRequest'), responses=reply('AnomalyDetectionResult'),
             summary='Screen a run against its Monte Carlo normal pattern (aggregate synthetic counts only)')
async def detect_anomalies(request: Request):
    return await execute(request, 'AnomalyDetectionRequest', detect)


@router.post('/anomaly/evaluation', response_model=None, openapi_extra=body('AnomalyEvaluationRequest'), responses=reply('AnomalyEvaluationResult'),
             summary='False positives and negatives of given settings on the synthetic test set; "warming" while it is built')
async def evaluate_settings(request: Request):
    return await execute(request, 'AnomalyEvaluationRequest', evaluation)


@router.post('/anomaly/evaluation/case', response_model=None, openapi_extra=body('AnomalyCaseRequest'), responses=reply('AnomalyCaseResult'),
             summary='One synthetic test case with its label, outcome and full detection')
async def evaluation_case(request: Request):
    return await execute(request, 'AnomalyCaseRequest',
                         lambda payload: case_detail(payload) or JSONResponse(status_code=404, content={'detail': 'Not Found'}))

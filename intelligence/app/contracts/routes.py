import json
import logging

from fastapi import APIRouter, Request
from fastapi.encoders import jsonable_encoder
from fastapi.responses import JSONResponse

from app.contracts.validation import CONTRACTS, DOCUMENT, error, parse_json, response, schema, validate

router = APIRouter(prefix="/contracts/v1", tags=["Contracts v1"])
logger = logging.getLogger("aerosynapse.intelligence.contracts")


def openapi_schema(definition):
    # OpenAPI refs point to components; standalone JSON Schemas keep their own $defs.
    return {"$ref": f"#/components/schemas/Contract{definition}"}


def components():
    text = json.dumps(DOCUMENT["$defs"]).replace("#/$defs/", "#/components/schemas/Contract")
    return {"Contract" + name: value for name, value in json.loads(text).items()}


def endpoint(definition):
    async def validate_contract(request: Request):
        media_type = request.headers.get("content-type", "").split(";")[0].strip().lower()
        if media_type != "application/json":
            return JSONResponse(status_code=415, content=response([error("", "unsupported_media_type")]))
        try:
            payload, errors = parse_json(await request.body())
        except (ValueError, UnicodeDecodeError, RecursionError):
            return JSONResponse(status_code=422, content=response([error("", "malformed_json")]))
        if not errors:
            errors = validate(definition, payload)
        if errors:
            logger.warning("Contract validation failed: definition=%s", definition)
            return JSONResponse(status_code=422, content=response(errors))
        return JSONResponse(content=jsonable_encoder(payload))
    return validate_contract


for path, definition in (
    ('journeys/response/validate', 'JourneySimulationResponse'),
    ("simulation/validate", "SimulationRequest"), ("optimization/validate", "OptimizationRequest"),
    ("scenario/validate", "ScenarioSnapshot"), ("zone/validate", "ZoneSnapshot"),
    ("simulation/response/validate", "SimulationResponse"), ("optimization/response/validate", "OptimizationResponse"),
    ("simulation/result/validate", "SimulationResult"), ("optimization/result/validate", "OptimizationResult"),
    ("passengers/validate", "PassengerGenerationRequest"), ("passengers/simulation/validate", "ProfiledSimulationRequest"),
    ('processes/simulation/validate','SimPySimulationRequest'), ('processes/result/validate','ProcessResult'),
    ('kpis/simulation/validate','KpiSimulationRequest'), ('kpis/result/validate','KpiResult'), ('kpis/snapshot/validate','KpiSnapshot'),
    ('resources/validate', 'ResourceSolveRequest'), ('resources/result/validate', 'ResourceSolveResult'),
    ('forecast/validate', 'QueueForecastRequest'), ('forecast/result/validate', 'QueueForecastResult'),
    ('forecast/prediction/validate', 'QueuePredictionResult'),
    ('anomaly/validate', 'AnomalyDetectionRequest'), ('anomaly/result/validate', 'AnomalyDetectionResult'),
    ('anomaly/evaluation/result/validate', 'AnomalyEvaluationResult'), ('anomaly/case/result/validate', 'AnomalyCaseResult'),
    ('staffing/validate', 'StaffingSolveRequest'), ('staffing/result/validate', 'StaffingSolveResult'),
    ('staffing/candidates/validate', 'StaffingCandidateSearchRequest'), ('staffing/candidates/result/validate', 'StaffingCandidateSearchResult'),
    ('staffing/shifts/validate', 'ShiftPlanRequest'), ('staffing/shifts/result/validate', 'ShiftPlanResult'),
    ('navigation/validate','NavigationPreviewRequest'), ('navigation/simulation/validate','RoutedSimulationRequest'), ('navigation/result/validate','NavigationResult'),
):
    router.add_api_route("/" + path, endpoint(definition), methods=["POST"], response_model=None,
                         name="validate_" + definition,
                         openapi_extra={"requestBody": {"required": True, "content": {"application/json": {"schema": openapi_schema(definition)}}}},
                         responses={200: {"content": {"application/json": {"schema": openapi_schema(definition)}}},
                                    422: {"content": {"application/json": {"schema": openapi_schema("ValidationError")}}},
                                    415: {"content": {"application/json": {"schema": openapi_schema("ValidationError")}}}})


@router.get("/schemas/{definition}", response_model=None)
async def get_schema(definition: str):
    if definition not in ("ScenarioSnapshot", "ZoneSnapshot", "SimulationResponse", "OptimizationResponse",
                           "SimulationRequest", "OptimizationRequest", "SimulationResult", "OptimizationResult", "ValidationError",
                           "PassengerGenerationRequest", "PassengerGenerationConfig", "ProfiledSimulationRequest", "ProfiledScenarioSnapshot",
                           'NavigationConfig','NavigationPreviewRequest','NavigationResult','RoutedSimulationRequest','RoutedSimulationResponse',
                           'SimPySimulationRequest','SimPyScenarioSnapshot','SimPySimulationResponse','ProcessResult',
                           'KpiSimulationRequest','KpiScenarioSnapshot','KpiSimulationResponse','KpiResult','KpiSnapshot',
                           'JourneyInput','JourneyResult','JourneySimulationResponse',
                           'IntegratedResourceConfig', 'ResourceSolveRequest', 'ResourceSolveResult',
                           'QueueForecastOptions', 'QueueForecastLiveOptions', 'QueueForecastRequest', 'QueueForecastResult', 'QueuePredictionResult', 'AnomalySettings', 'AnomalyDetectionRequest', 'AnomalyDetectionResult', 'AnomalyEvaluationRequest',
                           'AnomalyEvaluationResult', 'AnomalyCaseRequest', 'AnomalyCaseResult',
                           'StaffingSolveRequest', 'StaffingSolveResult', 'StaffingProposalRequest',
                           'StaffingCandidateSearchRequest', 'StaffingCandidateSearchResult', 'ShiftPlanRequest', 'ShiftPlanResult'):
        return JSONResponse(status_code=404, content={"detail": "Not Found"})
    return schema(definition)


@router.get("/openapi.json", response_model=None)
async def get_contract_openapi():
    return json.loads((CONTRACTS / "v1/contract.openapi.json").read_text(encoding="utf-8"))

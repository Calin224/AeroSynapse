from fastapi import APIRouter, Request
from fastapi.responses import JSONResponse
from starlette.concurrency import run_in_threadpool

from app.contracts.models import SimulationRequest
from app.contracts.routes import openapi_schema
from app.contracts.validation import error, parse_json, response, validate
from app.forecast.model import predict
from app.simulation.engine import simulate
from app.simulation.observation import observe
from app.simulation.forecast import forecast

router = APIRouter(tags=["Simulation v1"])


@router.post('/simulation/queue-forecast', response_model=None,
    summary='Estimate the next 15–30 simulated minutes from planned arrivals, captured backlog and service capacity',
    openapi_extra={'requestBody': {'required': True, 'content': {'application/json': {'schema': openapi_schema('QueueForecastRequest')}}}},
    responses={200: {'content': {'application/json': {'schema': openapi_schema('QueueForecastResult')}}}})
async def queue_forecast(request: Request):
    return await execute(request, 'QueueForecastRequest', forecast)


@router.post('/simulation/queue-prediction', response_model=None,
    summary='Research predictor on synthetic data; QUEUE_ML_PREDICTOR=disabled returns the deterministic forecast',
    openapi_extra={'requestBody': {'required': True, 'content': {'application/json': {'schema': openapi_schema('QueueForecastRequest')}}}})
async def queue_prediction(request: Request):
    return await execute(request, 'QueueForecastRequest', predict)


@router.post('/simulation/kpi-run', response_model=None, summary='Run SimPy with observed KPI populations and persisted 1–5 minute zone snapshots',
    openapi_extra={'requestBody': {'required': True, 'content': {'application/json': {'schema': openapi_schema('KpiSimulationRequest')}}}})
async def run_kpi_simulation(request: Request):
    return await execute(request, 'KpiSimulationRequest')


@router.post('/simulation/simpy-run', response_model=None, summary='Run passenger processes through bounded SimPy resources in virtual time',
    openapi_extra={'requestBody': {'required': True, 'content': {'application/json': {'schema': openapi_schema('SimPySimulationRequest')}}}})
async def run_simpy_simulation(request: Request):
    return await execute(request, 'SimPySimulationRequest')


@router.post('/simulation/navigation-run', response_model=None, summary='Execute captured routes and assess gate arrival against boarding closure',
    openapi_extra={'requestBody': {'required': True, 'content': {'application/json': {'schema': openapi_schema('RoutedSimulationRequest')}}}})
async def run_navigation_simulation(request: Request):
    return await execute(request, 'RoutedSimulationRequest')


@router.post('/simulation/passenger-run', response_model=None, summary='Execute deterministic flight-linked passenger inputs',
             openapi_extra={'requestBody': {'required': True, 'content': {'application/json': {'schema': openapi_schema('ProfiledSimulationRequest')}}}})
async def run_passenger_simulation(request: Request):
    return await execute(request, 'ProfiledSimulationRequest')


@router.post("/simulation/v1/run", response_model=None,
             summary="Execute frozen v1 queue inputs with an explicit seed",
             openapi_extra={"requestBody": {"required": True, "content": {"application/json": {"schema": openapi_schema("SimulationRequest")}}}},
             responses={200: {"content": {"application/json": {"schema": openapi_schema("SimulationResponse")}}},
                        422: {"content": {"application/json": {"schema": openapi_schema("ValidationError")}}},
                        415: {"content": {"application/json": {"schema": openapi_schema("ValidationError")}}}})
async def run_simulation(request: Request):
    return await execute(request, 'SimulationRequest')


@router.post('/simulation/v1/observe', response_model=None,
             summary='Run a KPI request up to a minute and return each control, the desks and an optional delay\'s gate options')
async def observe_simulation(request: Request):
    if request.headers.get("content-type", "").split(";")[0].strip().lower() != "application/json":
        return JSONResponse(status_code=415, content=response([error("", "unsupported_media_type")]))
    body = await request.body()
    if len(body) > 20_000_000:
        return JSONResponse(status_code=413, content=response([error('', 'out_of_range')]))
    try:
        payload, errors = parse_json(body)
    except (ValueError, UnicodeDecodeError, RecursionError):
        return JSONResponse(status_code=422, content=response([error("", "malformed_json")]))
    if not errors and (not isinstance(payload, dict) or set(payload) - {'request', 'minute', 'gateEventId'} or 'request' not in payload):
        errors = [error('', 'invalid_value')]
    if errors:
        return JSONResponse(status_code=422, content=response(errors))
    minute, gate = payload.get('minute'), payload.get('gateEventId')
    try:
        wire = SimulationRequest.model_validate(payload['request'])
    except ValueError:
        return JSONResponse(status_code=422, content=response([error('/request', 'invalid_value')]))
    if (wire.engine != 'simpy-kpi-1.0' or minute is None or minute != int(minute) or not 0 < int(minute) < wire.duration_minutes
            or gate is not None and (wire.journey is None or not any(e.type == 'flight_change' and str(e.event_id) == gate for e in wire.events or []))):
        return JSONResponse(status_code=422, content=response([error('/minute', 'out_of_range')]))
    return JSONResponse(content=await run_in_threadpool(observe, wire, int(minute), gate))


async def execute(request: Request, definition: str, operation=None):
    if request.headers.get("content-type", "").split(";")[0].strip().lower() != "application/json":
        return JSONResponse(status_code=415, content=response([error("", "unsupported_media_type")]))
    body = await request.body()
    if operation is not None and len(body) > 2_000_000:
        return JSONResponse(status_code=413, content=response([error('', 'out_of_range')]))
    try:
        payload, errors = parse_json(body)
    except (ValueError, UnicodeDecodeError, RecursionError):
        return JSONResponse(status_code=422, content=response([error("", "malformed_json")]))
    if not errors:
        errors = validate(definition, payload)
    if errors:
        return JSONResponse(status_code=422, content=response(errors))
    if operation is not None:
        result = await run_in_threadpool(operation, payload)
        return result if isinstance(result, JSONResponse) else JSONResponse(content=result)
    result = await run_in_threadpool(simulate, SimulationRequest.model_validate(payload))
    return JSONResponse(content=result.model_dump(mode="json", by_alias=True))

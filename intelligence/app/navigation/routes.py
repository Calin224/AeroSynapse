from fastapi import APIRouter, Query, Request
from fastapi.responses import JSONResponse
from starlette.concurrency import run_in_threadpool

from app.contracts.models import NavigationConfig, NavigationInput, PassengerGenerationRequest
from app.contracts.routes import openapi_schema
from app.contracts.validation import error, parse_json, response, validate
from app.navigation.graph import NavigationGraph
from app.navigation.planner import page, plan_population, without_closed_checkpoints
from app.passengers.generator import generate_population

router = APIRouter(tags=['Operational navigation v1'])


async def read(request, definition):
    if request.headers.get('content-type', '').split(';')[0].strip().lower() != 'application/json':
        return None, JSONResponse(status_code=415, content=response([error('', 'unsupported_media_type')]))
    body = await request.body()
    if len(body) > 8_000_000:
        return None, JSONResponse(status_code=413, content=response([error('', 'out_of_range')]))
    try:
        payload, errors = parse_json(body)
    except (ValueError, UnicodeDecodeError, RecursionError):
        return None, JSONResponse(status_code=422, content=response([error('', 'malformed_json')]))
    if not errors: errors = validate(definition, payload)
    return payload, JSONResponse(status_code=422, content=response(errors)) if errors else None


@router.post('/navigation/v1/preview', response_model=None,
    openapi_extra={'requestBody': {'required': True, 'content': {'application/json': {'schema': openapi_schema('NavigationPreviewRequest')}}}})
async def preview(request: Request, offset: int = Query(0, ge=0, le=100000), limit: int = Query(100, ge=1, le=100000)):
    payload, failure = await read(request, 'NavigationPreviewRequest')
    if failure is not None: return failure
    generation = PassengerGenerationRequest.model_validate(payload['passengerGeneration'])
    navigation = NavigationInput.model_validate(payload['navigation']).model_dump(mode='json', by_alias=True)
    def compute():
        return page(plan_population(generate_population(without_closed_checkpoints(generation, navigation)), generation, navigation), offset, limit)
    return JSONResponse(content=await run_in_threadpool(compute))


@router.post('/navigation/v1/route', response_model=None,
    openapi_extra={'requestBody': {'required': True, 'content': {'application/json': {'schema': openapi_schema('NavigationRouteQuery')}}}})
async def route(request: Request):
    payload, failure = await read(request, 'NavigationRouteQuery')
    if failure is not None: return failure
    navigation = NavigationConfig.model_validate(payload['navigation']).model_dump(mode='json', by_alias=True)
    result = await run_in_threadpool(NavigationGraph(navigation).route, payload['fromZoneId'], payload['toZoneId'], payload['groupKind'])
    return JSONResponse(content=result, status_code=422 if result['error'] else 200)

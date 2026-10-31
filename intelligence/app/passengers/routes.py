from fastapi import APIRouter, Query, Request
from fastapi.responses import JSONResponse
from starlette.concurrency import run_in_threadpool

from app.contracts.models import PassengerGenerationRequest
from app.contracts.routes import openapi_schema
from app.contracts.validation import error, parse_json, response, validate
from app.passengers.generator import generate_population, page
from app.simulation.flows import report

router = APIRouter(tags=['Synthetic passengers v1'])


@router.post('/passengers/v1/generate', response_model=None,
             summary='Generate reproducible demo flight batches; return an explicit page of the logical population',
             openapi_extra={'requestBody': {'required': True, 'content': {'application/json': {'schema': openapi_schema('PassengerGenerationRequest')}}}})
async def generate(request: Request, offset: int = Query(0, ge=0, le=100000), limit: int = Query(100, ge=1, le=100000)):
    generation = await read(request)
    if isinstance(generation, JSONResponse):
        return generation
    population = await run_in_threadpool(generate_population, generation)
    return JSONResponse(content=page(population, offset, limit))


@router.post('/passengers/v1/flows', response_model=None,
             summary='Simulate stage routes and return aggregate statistics per stage and passenger type',
             openapi_extra={'requestBody': {'required': True, 'content': {'application/json': {'schema': openapi_schema('PassengerGenerationRequest')}}}})
async def analyze_flows(request: Request):
    generation = await read(request)
    if isinstance(generation, JSONResponse):
        return generation
    return JSONResponse(content=await run_in_threadpool(report, generation))


async def read(request: Request):
    if request.headers.get('content-type', '').split(';')[0].strip().lower() != 'application/json':
        return JSONResponse(status_code=415, content=response([error('', 'unsupported_media_type')]))
    body = await request.body()
    if len(body) > 4_000_000:
        return JSONResponse(status_code=413, content=response([error('', 'out_of_range')]))
    try:
        payload, errors = parse_json(body)
    except (ValueError, UnicodeDecodeError, RecursionError):
        return JSONResponse(status_code=422, content=response([error('', 'malformed_json')]))
    if not errors:
        errors = validate('PassengerGenerationRequest', payload)
    if errors:
        return JSONResponse(status_code=422, content=response(errors))
    return PassengerGenerationRequest.model_validate(payload)

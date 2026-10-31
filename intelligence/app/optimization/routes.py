import json

from fastapi import APIRouter, Request
from fastapi.responses import JSONResponse
from starlette.concurrency import run_in_threadpool

from app.contracts.routes import openapi_schema
from app.contracts.validation import error, parse_json, response, validate
from app.optimization.staffing import solve, generate_candidates
from app.optimization.models import StaffingSolveRequest, StaffingCandidateSearchRequest
from app.optimization.models import ParetoSearchRequest, ShiftPlanRequest
from app.optimization.shifts import solve as plan_shifts
from app.optimization.pareto import search as pareto_search

router = APIRouter(tags=['Staff redistribution v1'])


@router.post('/optimization/staffing/v1/pareto', response_model=None,
    summary='Re-solve weighted CP-SAT objectives and anchors under a hard operating budget',
    openapi_extra={'requestBody': {'required': True, 'content': {'application/json': {'schema': openapi_schema('ParetoSearchRequest')}}}},
    responses={200: {'content': {'application/json': {'schema': openapi_schema('ParetoSearchResult')}}}})
async def pareto(request: Request):
    return await execute(request, 'ParetoSearchRequest', ParetoSearchRequest, pareto_search)


@router.post('/optimization/staffing/v1/solve', response_model=None,
    summary='Propose integer staffing within physical, operating and terminal staff limits',
    openapi_extra={'requestBody': {'required': True, 'content': {'application/json': {
        'schema': openapi_schema('StaffingSolveRequest')}}}},
        responses={200: {'content': {'application/json': {'schema': openapi_schema('StaffingSolveResult')}}}})
async def propose(request: Request):
    return await execute(request, 'StaffingSolveRequest', StaffingSolveRequest, solve)


@router.post('/optimization/staffing/v1/candidates', response_model=None,
    summary='Generate up to four distinct non-baseline staffing allocations within one search budget',
    openapi_extra={'requestBody': {'required': True, 'content': {'application/json': {
        'schema': openapi_schema('StaffingCandidateSearchRequest')}}}},
        responses={200: {'content': {'application/json': {'schema': openapi_schema('StaffingCandidateSearchResult')}}}})
async def candidates(request: Request):
    return await execute(request, 'StaffingCandidateSearchRequest', StaffingCandidateSearchRequest, generate_candidates)


@router.post('/optimization/staffing/v1/shifts', response_model=None,
    summary='Plan who works at which checkpoint in each 15-minute slot, within shift, break and move rules',
    openapi_extra={'requestBody': {'required': True, 'content': {'application/json': {
        'schema': openapi_schema('ShiftPlanRequest')}}}},
        responses={200: {'content': {'application/json': {'schema': openapi_schema('ShiftPlanResult')}}}})
async def shifts(request: Request):
    return await execute(request, 'ShiftPlanRequest', ShiftPlanRequest, plan_shifts)


async def execute(request, definition, model, operation):
    if request.headers.get('content-type', '').split(';')[0].strip().lower() != 'application/json':
        return JSONResponse(status_code=415, content=response([error('', 'unsupported_media_type')]))
    body = await request.body()
    if len(body) > 2_000_000:
        return JSONResponse(status_code=413, content=response([error('', 'out_of_range')]))
    try:
        payload, errors = parse_json(body)
    except (ValueError, UnicodeDecodeError, RecursionError):
        return JSONResponse(status_code=422, content=response([error('', 'malformed_json')]))
    if not errors: errors = validate(definition, payload)
    if errors: return JSONResponse(status_code=422, content=response(errors))
    # A contract without a Pydantic model is used as validated.
    normalized = model.model_validate(payload).model_dump(mode='json', by_alias=True) if model else payload
    result = await run_in_threadpool(operation, normalized)
    return result if isinstance(result, JSONResponse) else JSONResponse(content=result)


@router.post('/optimization/resources/v1/solve', response_model=None,
    summary='Propose advisory gates and handling teams within availability, skills and shift constraints',
    tags=['Integrated resource planning v1'],
    openapi_extra={'requestBody': {'required': True, 'content': {'application/json': {
        'schema': openapi_schema('ResourceSolveRequest')}}}},
    responses={200: {'content': {'application/json': {'schema': openapi_schema('ResourceSolveResult')}}}})
async def resources(request: Request):
    from app.optimization.models import ResourceSolveRequest
    from app.optimization.resources import solve as solve_resources
    return await execute(request, 'ResourceSolveRequest', ResourceSolveRequest, solve_resources)


@router.post('/optimization/gates/v1/advice', response_model=None,
    summary='Recommend the gate plan that absorbs a delay reported now, against keeping every gate',
    tags=['Gate advice v1'],
    openapi_extra={'requestBody': {'required': True, 'content': {'application/json': {
        'schema': openapi_schema('GateAdviceRequest')}}}},
    responses={200: {'content': {'application/json': {'schema': openapi_schema('GateAdvice')}}},
               409: {'description': 'No gate plan absorbs the delay, or it contradicts the timeline'}})
async def gate_advice(request: Request):
    return await execute(request, 'GateAdviceRequest', None, advise_gates)


def advise_gates(payload):
    from app.optimization.gates import GatePlanningError, advise
    # The strict parser keeps numbers as Decimal; the solver works in floats like the journey run.
    payload = json.loads(json.dumps(payload, default=float))
    try:
        return advise(payload)
    except GatePlanningError as failure:
        return JSONResponse(status_code=409, content={'code': failure.code, 'message': str(failure)})

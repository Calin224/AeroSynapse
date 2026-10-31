import os

from fastapi import APIRouter, Depends, HTTPException

from app.airside.engine import simulate
from app.airside.models import AirsideRequest, AirsideResult, OptimizeResult
from app.airside.optimizer import optimize


def require_enabled():
    if os.getenv("AIRSIDE_TWIN_ENABLED", "false").lower() != "true":
        raise HTTPException(status_code=404, detail="Synthetic airside twin is disabled.")


router = APIRouter(prefix="/airside/v1", tags=["Synthetic airside twin"], dependencies=[Depends(require_enabled)])


@router.post("/run", response_model=AirsideResult, summary="Simulate exclusive synthetic stands, teams and vehicles")
def run(request: AirsideRequest):
    return simulate(request)


@router.post("/optimize", response_model=OptimizeResult, summary="Advise a complete synthetic CP-SAT allocation and replay it")
def propose(request: AirsideRequest):
    return optimize(request)

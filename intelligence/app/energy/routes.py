import os

from fastapi import APIRouter, Depends, HTTPException
from fastapi.responses import JSONResponse

from app.energy.engine import compare
from app.energy.models import EnergyComparison, EnergyRequest, OptimizeResult
from app.energy.optimizer import ConfigurationLimitError, optimize


def require_enabled():
    if os.getenv("ENERGY_ANALYTICS_ENABLED", "false").lower() != "true":
        raise HTTPException(status_code=404, detail="Synthetic energy analytics is disabled.")


router = APIRouter(prefix="/energy/v1", tags=["Synthetic energy analytics"], dependencies=[Depends(require_enabled)])


@router.post("/compare", response_model=EnergyComparison, summary="Compare assumed kWh and existing SimPy traffic on synthetic demand")
def comparison(request: EnergyRequest):
    return compare(request)


@router.post("/optimize", response_model=OptimizeResult, summary="Advise checkpoint capacity under a hard simulated kWh budget")
def proposal(request: EnergyRequest):
    try:
        return optimize(request)
    except ConfigurationLimitError as error:
        return JSONResponse(status_code=422, content={
            "detail": {"code": "energy_configuration_limit", "message": str(error)},
            "errors": [{"field": "optimization", "message": str(error)}],
        })

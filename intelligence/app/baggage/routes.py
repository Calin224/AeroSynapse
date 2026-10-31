import os

from fastapi import APIRouter, Depends, HTTPException

from app.baggage.engine import simulate
from app.baggage.models import BaggageRequest, BaggageResult


def require_enabled():
    if os.getenv("BAGGAGE_TWIN_ENABLED", "false").lower() != "true":
        raise HTTPException(status_code=404, detail="Synthetic baggage twin is disabled.")


router = APIRouter(prefix="/baggage/v1", tags=["Synthetic baggage twin"], dependencies=[Depends(require_enabled)])


@router.post("/run", response_model=BaggageResult, summary="Simulate synthetic baggage from check-in through loading")
def run(request: BaggageRequest):
    return simulate(request)

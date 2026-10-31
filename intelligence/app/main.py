import logging
import os
import time
import re
from uuid import uuid4
from contextvars import ContextVar
from starlette.concurrency import run_in_threadpool
from contextlib import asynccontextmanager
from datetime import datetime, timezone

import psycopg
from fastapi import FastAPI, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse
from fastapi.openapi.utils import get_openapi

from app.contracts.routes import router as contract_router, components as contract_components
from app.simulation.routes import router as simulation_router
from app.passengers.routes import router as passenger_router
from app.navigation.routes import router as navigation_router
from app.optimization.routes import router as staffing_router
from app.forecast.model import PREDICTOR, enabled as predictor_enabled
from app.anomaly.routes import router as anomaly_router
from app.baggage.routes import router as baggage_router
from app.airside.routes import router as airside_router
from app.energy.routes import router as energy_router

from app.simulation.contracts import SimulationRequest, ValidationErrorResponse
from app.simulation.executions import router as execution_router, initialize_storage

correlation = ContextVar("correlation_id", default="-")


class CorrelationFilter(logging.Filter):
    def filter(self, record):
        record.correlation_id = correlation.get()
        return True

logging.Formatter.converter = time.gmtime
logging.basicConfig(
    level=os.getenv("LOG_LEVEL", "INFO").upper(),
    format="%(asctime)sZ %(levelname)s %(name)s correlationId=%(correlation_id)s %(message)s",
    datefmt="%Y-%m-%dT%H:%M:%S",
)
logger = logging.getLogger("aerosynapse.intelligence")
logger.addFilter(CorrelationFilter())
for handler in logging.getLogger().handlers:
    handler.addFilter(CorrelationFilter())

@asynccontextmanager
async def lifespan(app: FastAPI):
    logger.info("Intelligence service starting (environment=%s)", os.getenv("APP_ENV", "development"))
    if os.getenv("POSTGRES_HOST"):
        await run_in_threadpool(initialize_storage)
    if predictor_enabled():
        PREDICTOR.start()  # The queue predictor trains in the background; requests get the deterministic forecast meanwhile.
    yield
    logger.info("Intelligence service stopping")


app = FastAPI(
    title="AeroSynapse Intelligence",
    version="0.1.0",
    root_path=os.getenv("ROOT_PATH", ""),
    lifespan=lifespan,
)
app.include_router(contract_router)
app.include_router(simulation_router)
app.include_router(passenger_router)
app.include_router(navigation_router)
app.include_router(staffing_router)
app.include_router(execution_router)
app.include_router(anomaly_router)
app.include_router(baggage_router)
app.include_router(airside_router)
app.include_router(energy_router)


@app.middleware("http")
async def correlate(request: Request, call_next):
    supplied = request.headers.get("X-Correlation-ID", "")
    identifier = supplied if re.fullmatch(r"[A-Za-z0-9_.-]{1,64}", supplied) else uuid4().hex
    request.state.correlation_id = identifier
    context_token = correlation.set(identifier)
    started = time.perf_counter()
    try:
        response = await call_next(request)
        response.headers["X-Correlation-ID"] = identifier
        # Health probes run every few seconds; logging them buries the simulation requests.
        if not request.url.path.startswith("/health"):
            logger.info("%s %s -> %s (%d ms)", request.method, request.url.path, response.status_code,
                        (time.perf_counter() - started) * 1000)
        return response
    finally:
        correlation.reset(context_token)


def versioned_openapi():
    if app.openapi_schema is None:
        app.openapi_schema = get_openapi(title=app.title, version=app.version, routes=app.routes, servers=[{"url": app.root_path}] if app.root_path else None)
        app.openapi_schema.setdefault("components", {}).setdefault("schemas", {}).update(contract_components())
    return app.openapi_schema


app.openapi = versioned_openapi

VALIDATION_MESSAGES = {
    "missing": "Field is required.",
    "int_type": "Must be an integer.",
    "greater_than": "Must be greater than zero.",
    "greater_than_equal": "Must be zero or greater.",
    "extra_forbidden": "Unknown field.",
    "json_invalid": "Malformed JSON.",
    "model_attributes_type": "Must be a JSON object.",
}


@app.exception_handler(RequestValidationError)
async def validation_error_handler(
    request: Request, exception: RequestValidationError
) -> JSONResponse:
    errors = []
    for error in exception.errors():
        location = error["loc"]
        field = ".".join(str(part) for part in location if part != "body") or "body"
        if error["type"] == "json_invalid":
            field = "body"
        errors.append(
            {
                "field": field,
                "message": VALIDATION_MESSAGES.get(error["type"], "Invalid value."),
            }
        )

    logger.warning("Request validation failed: path=%s", request.url.path)
    return JSONResponse(
        status_code=422,
        content={"message": "Invalid request.", "errors": errors},
    )


@app.get("/health", tags=["Health"])
def health() -> dict[str, str]:
    return {"status": "Healthy"}


@app.post(
    "/simulation/validate",
    response_model=SimulationRequest,
    responses={422: {"model": ValidationErrorResponse}},
    tags=["Simulation"],
    summary="Validează contractul unei cereri de simulare",
)
async def validate_simulation(request: SimulationRequest) -> SimulationRequest:
    logger.info("Simulation request validated")
    return request
def health_payload(status: str) -> dict:
    return {"status": status, "service": "intelligence", "timestampUtc": datetime.now(timezone.utc).isoformat()}


@app.get("/health/ready", tags=["Health"])
def ready():
    # Standalone simulation validation stays independent of PostgreSQL.
    if not os.getenv("POSTGRES_HOST"):
        return health_payload("Healthy")
    try:
        # Individual kwargs preserve special characters in environment passwords.
        with psycopg.connect(
            host=os.getenv("POSTGRES_HOST", "localhost"),
            port=int(os.getenv("POSTGRES_PORT", "5432")),
            dbname=os.getenv("POSTGRES_DB", "aerosynapse"),
            user=os.getenv("POSTGRES_USER", "aerosynapse"),
            password=os.getenv("POSTGRES_PASSWORD", ""),
            connect_timeout=3,
            options="-c statement_timeout=3000",
        ) as connection:
            connection.execute("SELECT 1").fetchone()
        return health_payload("Healthy")
    except (psycopg.Error, ValueError) as error:
        logger.warning("PostgreSQL readiness failed (%s)", type(error).__name__)
        return JSONResponse(status_code=503, content=health_payload("Unhealthy"))

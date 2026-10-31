from pydantic import BaseModel, ConfigDict, Field


class SimulationRequest(BaseModel):
    model_config = ConfigDict(strict=True, extra="forbid")

    duration_minutes: int = Field(gt=0, description="Durata simulării în minute.")
    passenger_count: int = Field(gt=0, description="Numărul de pasageri simulați.")
    seed: int = Field(default=42, ge=0, description="Seed pentru reproductibilitate.")


class FieldError(BaseModel):
    field: str
    message: str


class ValidationErrorResponse(BaseModel):
    message: str
    errors: list[FieldError]

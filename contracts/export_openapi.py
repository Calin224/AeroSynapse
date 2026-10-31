"""Regenerate the shared OpenAPI 3.1 document: python contracts/export_openapi.py."""
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parent / "v1"
ENDPOINTS = {
    "scenario/validate": "ScenarioSnapshot",
    "zone/validate": "ZoneSnapshot",
    "simulation/validate": "SimulationRequest",
    "optimization/validate": "OptimizationRequest",
    "simulation/response/validate": "SimulationResponse",
    "optimization/response/validate": "OptimizationResponse",
    "simulation/result/validate": "SimulationResult",
    "optimization/result/validate": "OptimizationResult",
}


def document():
    source = json.loads((ROOT / "contract.schema.json").read_text(encoding="utf-8"))
    schemas = json.loads(json.dumps(source["$defs"]).replace("#/$defs/", "#/components/schemas/Contract"))
    def content(name):
        return {"application/json": {"schema": {"$ref": "#/components/schemas/Contract" + name}}}
    return {
        "openapi": "3.1.0",
        "info": {"title": "AeroSynapse .NET–Python contracts", "version": "1.0"},
        "paths": {
            "/contracts/v1/" + path: {"post": {
                "operationId": "validate_" + name,
                "requestBody": {"required": True, "content": content(name)},
                "responses": {
                    "200": {"description": "Valid contract", "content": content(name)},
                    "422": {"description": "Invalid contract or malformed JSON", "content": content("ValidationError")},
                    "415": {"description": "Unsupported media type", "content": content("ValidationError")},
                },
            }} for path, name in ENDPOINTS.items()
        },
        "components": {"schemas": {"Contract" + name: value for name, value in schemas.items()}},
    }


if __name__ == "__main__":
    (ROOT / "contract.openapi.json").write_text(json.dumps(document(), indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

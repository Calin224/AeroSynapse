import json
import math
from decimal import Decimal, InvalidOperation
from datetime import datetime, timedelta
from functools import lru_cache
from pathlib import Path
from zoneinfo import ZoneInfo, ZoneInfoNotFoundError
from uuid import UUID

from jsonschema import Draft202012Validator, validators


def mathematical_integer(checker, value):
    if isinstance(value, bool):
        return False
    if isinstance(value, Decimal):
        return value.is_finite() and value == value.to_integral_value()
    return isinstance(value, int) or isinstance(value, float) and value.is_integer()


def decimal_bound(keyword):
    """Compare JSON decimal bounds without mixing Decimal payloads with binary schema floats."""
    def check(validator, bound, instance, schema):
        def number(value):
            return Decimal(str(value)) if isinstance(value, float) and math.isfinite(value) else value
        yield from Draft202012Validator.VALIDATORS[keyword](validator, number(bound), number(instance), schema)
    return check


ContractValidator = validators.extend(
    Draft202012Validator,
    validators={name: decimal_bound(name) for name in ('minimum', 'maximum', 'exclusiveMinimum', 'exclusiveMaximum')},
    type_checker=Draft202012Validator.TYPE_CHECKER.redefine("integer", mathematical_integer),
)

# Local checkout: repo/contracts; container: /app/contracts. Both contain the same files.
_SOURCE = Path(__file__).resolve()
CONTRACTS = next(path for path in (_SOURCE.parents[3] / "contracts", _SOURCE.parents[2] / "contracts") if path.exists())
DOCUMENT = json.loads((CONTRACTS / "v1/contract.schema.json").read_text(encoding="utf-8"))
DOCUMENT['$defs'].update(json.loads((CONTRACTS / 'passengers/v1/contract.schema.json').read_text(encoding='utf-8'))['$defs'])
DOCUMENT['$defs'].update(json.loads((CONTRACTS / 'navigation/v1/contract.schema.json').read_text(encoding='utf-8'))['$defs'])
DOCUMENT['$defs'].update(json.loads((CONTRACTS / 'processes/v1/contract.schema.json').read_text(encoding='utf-8'))['$defs'])
DOCUMENT['$defs'].update(json.loads((CONTRACTS / 'kpis/v1/contract.schema.json').read_text(encoding='utf-8'))['$defs'])
DOCUMENT['$defs'].update(json.loads((CONTRACTS / 'events/v1/contract.schema.json').read_text(encoding='utf-8'))['$defs'])
DOCUMENT['$defs'].update(json.loads((CONTRACTS / 'staffing/v1/contract.schema.json').read_text(encoding='utf-8'))['$defs'])
DOCUMENT['$defs'].update(json.loads((CONTRACTS / 'resources/v1/contract.schema.json').read_text(encoding='utf-8'))['$defs'])
DOCUMENT['$defs'].update(json.loads((CONTRACTS / 'forecast/v1/contract.schema.json').read_text(encoding='utf-8'))['$defs'])
DOCUMENT['$defs'].update(json.loads((CONTRACTS / 'anomaly/v1/contract.schema.json').read_text(encoding='utf-8'))['$defs'])
DOCUMENT['$defs'].update(json.loads((CONTRACTS / 'journeys/v1/contract.schema.json').read_text(encoding='utf-8'))['$defs'])
VERSION = "1.0"
MESSAGES = {
    "required": "Field is required.", "unknown_field": "Unknown field.",
    "invalid_type": "Value has an invalid JSON type.", "out_of_range": "Value is outside the allowed range.",
    "invalid_format": "Value has an invalid format.", "unsupported_version": "Unsupported contract version.",
    "invalid_value": "Value is not allowed.", "invalid_timezone": "A supported IANA time zone is required.",
    "duplicate_id": "Identifiers must be unique.",
    "event_conflict": "The event contradicts the scheduled timeline.",
    "comparison_mismatch": "Compared inputs must use the same layout, horizon, time zone, demand and seed.",
    "invalid_result": "Result fields contradict the run status or comparison.",
    "malformed_json": "Malformed JSON.", "duplicate_field": "Duplicate JSON field.",
    "unsupported_media_type": "Content-Type must be application/json.",
}


def error(field, code):
    return {"field": field, "code": code, "message": MESSAGES[code]}


def response(errors):
    return {"contractVersion": VERSION, "code": "validation_failed", "message": "Invalid contract request.", "errors": errors}


def schema(definition):
    if definition not in DOCUMENT["$defs"]:
        raise ValueError("Unknown contract definition.")
    return DOCUMENT | {"$ref": f"#/$defs/{definition}"}


@lru_cache
def validator(definition):
    document = schema(definition)
    Draft202012Validator.check_schema(document)
    return ContractValidator(document)


def escape(value):
    return str(value).replace("~", "~0").replace("/", "~1")


def validate(definition, payload):
    errors = []
    try:
        validate_unicode(payload)
    except UnicodeEncodeError:
        return [error("", "malformed_json")]
    validate_numbers(payload, "", errors)
    if errors:
        return sorted(errors, key=error_key)
    def collect(item):
        field = "".join("/" + escape(part) for part in item.absolute_path)
        keyword = item.validator
        if keyword == "anyOf" and isinstance(item.instance, (dict, list)):
            non_null = [index for index, branch in enumerate(item.schema['anyOf']) if branch.get('type') != 'null']
            if len(non_null) == 1:
                for child in item.context:
                    if child.schema_path[0] == non_null[0]:
                        collect(child)
                return
        if keyword == "required":
            for name in item.schema["required"]:
                if name not in item.instance:
                    errors.append(error(field + "/" + escape(name), "required"))
        elif keyword == "additionalProperties":
            for name in item.instance:
                if name not in item.schema["properties"]:
                    errors.append(error(field + "/" + escape(name), "unknown_field"))
        else:
            code = {"type": "invalid_type", "anyOf": "invalid_type", "pattern": "invalid_format", "not": "invalid_format",
                    "minimum": "out_of_range", "maximum": "out_of_range", "exclusiveMinimum": "out_of_range",
                    "exclusiveMaximum": "out_of_range", "minLength": "out_of_range", "maxLength": "out_of_range",
                    "minItems": "out_of_range", "maxItems": "out_of_range"}.get(keyword, "invalid_value")
            if keyword == "const" and field.endswith("/contractVersion"):
                code = "unsupported_version"
            errors.append(error(field, code))
    for item in validator(definition).iter_errors(payload):
        collect(item)
    if not errors:
        validate_identifiers(payload, "", errors)
        semantics(definition, payload, "", errors)
    unique = {(item["field"], item["code"]): item for item in errors}
    return sorted(unique.values(), key=error_key)


def error_key(item):
    # .NET ordinal ordering compares UTF-16 code units, including supplementary characters.
    return item["field"].encode("utf-16-be"), item["code"]


def validate_unicode(value):
    if isinstance(value, dict):
        for key, child in value.items():
            key.encode("utf-8")
            validate_unicode(child)
    elif isinstance(value, list):
        for child in value:
            validate_unicode(child)
    elif isinstance(value, str):
        value.encode("utf-8")


def validate_numbers(value, path, errors):
    if isinstance(value, dict):
        for key, child in value.items():
            validate_numbers(child, path + "/" + escape(key), errors)
    elif isinstance(value, list):
        for index, child in enumerate(value):
            validate_numbers(child, f"{path}/{index}", errors)
    elif isinstance(value, (int, float, Decimal)) and not isinstance(value, bool):
        try:
            number = float(value)
            representable = math.isfinite(number) and (number != 0 or value == 0)
        except (ValueError, OverflowError):
            representable = False
        if not representable:
            errors.append(error(path, "out_of_range"))


def validate_identifiers(value, path, errors):
    if isinstance(value, dict):
        for key, child in value.items():
            field = path + "/" + escape(key)
            if key.endswith("Id") and isinstance(child, str):
                try:
                    canonical = str(UUID(child)) == child
                except ValueError:
                    canonical = False
                if not canonical:
                    errors.append(error(field, "invalid_format"))
            validate_identifiers(child, field, errors)
    elif isinstance(value, list):
        for index, child in enumerate(value):
            validate_identifiers(child, f"{path}/{index}", errors)


def utc(value):
    # Schema enforces uppercase Z, seconds and at most 6 fractional digits.
    return datetime.fromisoformat(value.replace("Z", "+00:00"))

def equivalent(name, left, right):
    if name == "startsAtUtc":
        try:
            return utc(left) == utc(right)
        except ValueError:
            return False
    if isinstance(left, (int, float, Decimal)):
        return float(left) == float(right)
    return left == right


def unique_ids(items, key, path, errors, baseline=None):
    seen = {baseline} if baseline else set()
    for index, item in enumerate(items):
        if item[key] in seen:
            errors.append(error(f"{path}/{index}/{key}", "duplicate_id"))
        seen.add(item[key])


def semantics(definition, value, path, errors):
    if definition in ('QueueForecastRequest', 'QueueForecastResult'):
        from app.simulation.forecast import validate_request, validate_result
        (validate_request if definition.endswith('Request') else validate_result)(value, path, errors, error)
        return
    if definition.startswith('Anomaly'):
        from app.anomaly import contract
        if definition == 'AnomalyDetectionRequest':
            contract.validate_request(value, path, errors, error)
        elif definition == 'AnomalyDetectionResult':
            contract.validate_result(value, path, errors, error)
        elif definition in ('AnomalyEvaluationRequest', 'AnomalyCaseRequest'):
            contract.validate_settings(value['settings'], 1, path, errors, error)
        elif definition == 'AnomalyEvaluationResult':
            contract.validate_evaluation(value, path, errors, error)
        elif definition == 'AnomalyCaseResult':
            contract.validate_case(value, path, errors, error)
            if value['status'] == 'ready' and 'detection' in value:
                contract.validate_result(value['detection'], path + '/detection', errors, error)
        return
    if definition == 'QueuePredictionResult':
        from app.forecast.contract import validate_prediction
        validate_prediction(value, path, errors, error)
        return
    if definition in ('IntegratedResourceConfig', 'ResourceSolveRequest', 'ResourceSolveResult'):
        from app.optimization.resources import validate_config, validate_request, validate_result
        {'IntegratedResourceConfig': validate_config, 'ResourceSolveRequest': validate_request,
         'ResourceSolveResult': validate_result}[definition](value, path, errors, error)
        return
    if definition == 'ResourceConfigWriteRequest':
        if value['resources'] is not None:
            semantics('IntegratedResourceConfig', value['resources'], path + '/resources', errors)
        return

    if definition in ('ParetoSearchRequest', 'ParetoSearchResult'):
        from app.optimization.pareto import validate_request, validate_result
        (validate_request if definition.endswith('Request') else validate_result)(value, path, errors, error)
        return
    if definition in ('ShiftPlanRequest', 'ShiftPlanResult'):
        from app.optimization.shifts import validate_request, validate_result
        (validate_request if definition.endswith('Request') else validate_result)(value, path, errors, error)
        return
    if definition in ('StaffingCandidateSearchRequest', 'StaffingCandidateSearchResult'):
        from app.optimization.staffing import validate_candidate_request, validate_candidate_result
        (validate_candidate_request if definition.endswith('Request') else validate_candidate_result)(value, path, errors, error)
        return
    if definition == 'StaffingSolveRequest':
        from app.optimization.staffing import validate_request
        validate_request(value, path, errors, error)
        return
    if definition == 'StaffingSolveResult':
        from app.optimization.staffing import validate_result
        validate_result(value, path, errors, error)
        return
    if definition in ('KpiSimulationRequest','KpiScenarioSnapshot'):
        semantics(definition.replace('Kpi','SimPy'),value,path,errors)
        interval=int(value['snapshotIntervalMinutes']); horizon=int(value['durationMinutes'])
        if ((horizon+interval-1)//interval+1)*len(value['checkpoints'])>50000:
            errors.append(error(path+'/snapshotIntervalMinutes','out_of_range'))
        from app.contracts.kpi_validation import validate_check_in_request, validate_staff_schedules
        validate_check_in_request(value,path,errors,error)
        validate_staff_schedules(value,path,errors,error)
        from app.contracts.journey_validation import validate_request
        validate_request(value,path,errors,error)
        if not errors:
            from app.simulation.events import validate_events
            validate_events(value,path,errors,error)
        return
    if definition=='JourneySimulationResponse':
        semantics('SimulationResponse',value,path,errors)
        from app.contracts.journey_validation import validate_result
        validate_result(value['journeyResult'],path+'/journeyResult',errors,error)
        semantics('KpiSimulationResponse',value['journeyResult']['upstreamResult'],path+'/journeyResult/upstreamResult',errors)
        return
    if definition=='KpiSimulationResponse':
        semantics('SimPySimulationResponse',value,path,errors)
        semantics('KpiResult',value['kpiResult'],path+'/kpiResult',errors)
        if 'checkInResult' in value:
            from app.contracts.kpi_validation import validate_check_in_result
            validate_check_in_result(value['checkInResult'],path+'/checkInResult',errors,error)
        return
    if definition=='KpiProcessResult':
        semantics('ProcessResult',value,path,errors); return
    if definition in ('KpiResult','KpiSnapshot'):
        from app.contracts.kpi_validation import validate_kpis, validate_snapshot
        (validate_kpis if definition=='KpiResult' else validate_snapshot)(value,path,errors,error)
        return
    if definition in ('SimPySimulationRequest', 'SimPyScenarioSnapshot'):
        semantics(definition.removeprefix('SimPy'), value, path, errors)
        if value['passengerCount'] > 0 and value['arrivalRatePerMinute'] == 0:
            errors.append(error(path + '/arrivalRatePerMinute', 'out_of_range'))
        if 'navigation' in value:
            if 'passengerGeneration' not in value:
                errors.append(error(path + '/passengerGeneration', 'required'))
            else:
                from app.contracts.navigation_validation import validate_navigation_envelope
                validate_navigation_envelope(value, path, errors, error)
        return
    if definition == 'SimPySimulationResponse':
        semantics('RoutedSimulationResponse' if 'navigationResult' in value else 'SimulationResponse', value, path, errors)
        semantics('ProcessResult', value['processResult'], path + '/processResult', errors)
        return
    if definition == 'ProcessResult':
        from app.contracts.process_validation import validate_result
        validate_result(value, path, errors, error)
        return
    if definition in ('NavigationConfig', 'NavigationInput'):
        from app.contracts.navigation_validation import validate_navigation
        validate_navigation(value, path, errors, error)
        return
    if definition in ('NavigationPreviewRequest', 'NavigationRouteQuery'):
        from app.contracts.navigation_validation import validate_navigation, validate_navigation_envelope
        if definition == 'NavigationPreviewRequest':
            validate_navigation_envelope(value, path, errors, error)
        else:
            validate_navigation(value['navigation'], path + '/navigation', errors, error)
            ids = {item['id'] for item in value['navigation']['graph']['nodes']}
            for name in ('fromZoneId', 'toZoneId'):
                if value[name] not in ids:
                    errors.append(error(path + '/' + name, 'invalid_value'))
        return
    if definition in ('RoutedSimulationRequest', 'RoutedScenarioSnapshot'):
        semantics('Profiled' + definition.removeprefix('Routed'), {key: child for key, child in value.items() if key != 'navigation'}, path, errors)
        from app.contracts.navigation_validation import validate_navigation_envelope
        validate_navigation_envelope(value, path, errors, error)
        return
    if definition == 'RoutedSimulationResponse':
        semantics('SimulationResponse', {key: child for key, child in value.items() if key != 'navigationResult'}, path, errors)
        semantics('NavigationResult', value['navigationResult'], path + '/navigationResult', errors)
        return
    if definition == 'NavigationResult':
        result = value
        if (result['routedCount'] + result['errorCount'] != result['modeledCount']
                or result['returnedCount'] != len(result['journeys'])
                or result['returnedCount'] > max(0, result['modeledCount'] - result['offset'])
                or result['hasMore'] != (result['offset'] + result['returnedCount'] < result['modeledCount'])
                or sum(item['passengerCount'] for item in result['routes']) != result['routedCount']
                or sum(item['modeledCount'] for item in result['flights']) != result['modeledCount']):
            errors.append(error(path, 'invalid_result'))
        return
    if definition == "PassengerGenerationRequest":
        from app.contracts.passenger_validation import validate_passengers
        validate_passengers(value, path, errors, error)
        return
    if definition == "SimulationResponse":
        if value["status"] == "completed" and value["simulatedMinutes"] == 0:
            errors.append(error(path + "/simulatedMinutes", "invalid_result"))
        for index, snapshot in enumerate(value["snapshots"]):
            if snapshot["elapsedMinutes"] > value["simulatedMinutes"]:
                errors.append(error(f"{path}/snapshots/{index}/elapsedMinutes", "invalid_result"))
    definition = {"SimulationResponse": "SimulationResult", "OptimizationResponse": "OptimizationResult",
                  'ProfiledSimulationRequest': 'SimulationRequest', 'ProfiledScenarioSnapshot': 'ScenarioSnapshot',
                  'ProfiledOptimizationRequest': 'OptimizationRequest'}.get(definition, definition)
    def add(field, code):
        errors.append(error(path + field, code))
    if definition in ("SimulationRequest", "ScenarioSnapshot"):
        if definition == "ScenarioSnapshot":
            try:
                utc(value["capturedAtUtc"])
            except ValueError:
                add("/capturedAtUtc", "invalid_format")
        try:
            utc(value["startsAtUtc"]) + timedelta(minutes=int(value["durationMinutes"]))
        except (ValueError, OverflowError):
            add("/startsAtUtc", "invalid_format")
        zone = value["timeZone"]
        try:
            if zone != "UTC" and "/" not in zone:
                raise ValueError()
            ZoneInfo(zone)
        except (ValueError, ZoneInfoNotFoundError):
            add("/timeZone", "invalid_timezone")
        unique_ids(value["checkpoints"], "checkpointId", path + "/checkpoints", errors)
        if "passengerGeneration" in value:
            generation = value['passengerGeneration']
            semantics('PassengerGenerationRequest', generation, path + '/passengerGeneration', errors)
            for name in ('seed', 'durationMinutes'):
                if generation[name] != value[name]:
                    add('/passengerGeneration/' + name, 'comparison_mismatch')
            if sum(int(batch['modeledCount']) for batch in generation['batches']) != value['passengerCount']:
                add('/passengerCount', 'comparison_mismatch')
            canonical = lambda items: sorted(items, key=lambda item: item['checkpointId'])
            if canonical(generation['checkpoints']) != canonical(value['checkpoints']):
                add('/passengerGeneration/checkpoints', 'comparison_mismatch')
    elif definition == "OptimizationRequest":
        baseline = value["baseline"]
        semantics("SimulationRequest", baseline, path + "/baseline", errors)
        unique_ids(value["candidates"], "runId", path + "/candidates", errors, baseline["runId"])
        for index, candidate in enumerate(value["candidates"]):
            candidate_path = f"{path}/candidates/{index}"
            semantics("SimulationRequest", candidate, candidate_path, errors)
            for name in ("layoutVersionId", "startsAtUtc", "timeZone", "durationMinutes", "seed", "passengerCount", "arrivalRatePerMinute"):
                if not equivalent(name, candidate[name], baseline[name]):
                    errors.append(error(candidate_path + "/" + name, "comparison_mismatch"))
            comparable = lambda generation: {key: child for key, child in generation.items() if key != 'checkpoints'} if generation else None
            if comparable(candidate.get('passengerGeneration')) != comparable(baseline.get('passengerGeneration')):
                errors.append(error(candidate_path + '/passengerGeneration', 'comparison_mismatch'))
    elif definition in ("SimulationResult", "OptimizationResult"):
        completed = value["status"] == "completed"
        if completed != (value["failure"] is None):
            add("/failure", "invalid_result")
        if value["failure"] is not None and all(character.isspace() and character not in "\x1c\x1d\x1e\x1f" for character in value["failure"]["message"]):
            add("/failure/message", "invalid_value")
        if value["failure"] is not None and "\n" in value["failure"]["code"]:
            add("/failure/code", "invalid_format")
        if definition == "SimulationResult":
            if not completed:
                for name in ("metrics", "snapshots"):
                    if value[name]:
                        add("/" + name, "invalid_result")
            for index, metric in enumerate(value["metrics"]):
                try:
                    utc(metric["recordedAtUtc"])
                except ValueError:
                    add(f"/metrics/{index}/recordedAtUtc", "invalid_format")
        elif completed:
            if value["baselineScore"] is None:
                add("/baselineScore", "invalid_result")
            if not value["candidates"]:
                add("/candidates", "invalid_result")
            unique_ids(value["candidates"], "runId", path + "/candidates", errors, value["baselineRunId"])
            ranks = sorted(item["rank"] for item in value["candidates"])
            if ranks != list(range(1, len(ranks) + 1)):
                add("/candidates", "invalid_result")
                seen_ranks = set()
                for index, candidate in enumerate(value["candidates"]):
                    if candidate["rank"] > len(ranks) or candidate["rank"] in seen_ranks:
                        add(f"/candidates/{index}/rank", "invalid_result")
                    seen_ranks.add(candidate["rank"])
            winner = next((item["runId"] for item in value["candidates"] if item["rank"] == 1), None)
            if value["recommendedRunId"] is None or value["recommendedRunId"] != winner:
                add("/recommendedRunId", "invalid_result")
            scores = [float(item["score"]) for item in sorted(value["candidates"], key=lambda item: item["rank"])]
            if scores != sorted(scores, reverse=value["objective"] == "maximizeThroughputPerMinute"):
                add("/candidates", "invalid_result")
        else:
            for name in ("baselineScore", "recommendedRunId"):
                if value[name] is not None:
                    add("/" + name, "invalid_result")
            if value["candidates"]:
                add("/candidates", "invalid_result")


class Pairs(list):
    """Keep object pairs until duplicate-field validation finishes."""


def parse_json(body):
    def reject_constant(value):
        raise ValueError("Non-JSON constant.")
    if isinstance(body, bytes):
        body = body.decode("utf-8-sig")
    try:
        raw = json.loads(body, object_pairs_hook=Pairs, parse_constant=reject_constant, parse_float=Decimal, parse_int=Decimal)
    except InvalidOperation as exception:
        raise ValueError("Unsupported JSON number.") from exception
    errors = []
    def unpack(value, path="", depth=0):
        if isinstance(value, (Pairs, list)) and depth >= 64:
            raise ValueError("JSON nesting limit exceeded.")
        if isinstance(value, Pairs):
            result = {}
            for key, child in value:
                key.encode("utf-8")
                field = path + "/" + escape(key)
                if key in result:
                    errors.append(error(field, "duplicate_field"))
                result[key] = unpack(child, field, depth + 1)
            return result
        if isinstance(value, list):
            return [unpack(child, f"{path}/{index}", depth + 1) for index, child in enumerate(value)]
        if isinstance(value, str):
            value.encode("utf-8")
        return value
    payload = unpack(raw)
    unique = {(item["field"], item["code"]): item for item in errors}
    return payload, sorted(unique.values(), key=error_key)

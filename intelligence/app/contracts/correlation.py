from app.contracts.validation import error


def simulation(request, result):
    errors = []
    def invalid(field):
        errors.append(error(field, "invalid_result"))
    if result.run_id != request.run_id:
        invalid("/runId")
    zones = {item.checkpoint_id for item in request.checkpoints}
    from datetime import timedelta
    simulated = getattr(result, "simulated_minutes", request.duration_minutes)
    if simulated > request.duration_minutes:
        invalid("/simulatedMinutes")
    end = request.starts_at_utc + timedelta(minutes=min(simulated, request.duration_minutes))
    for index, metric in enumerate(result.metrics):
        if metric.zone_id not in zones:
            invalid(f"/metrics/{index}/zoneId")
        if not request.starts_at_utc <= metric.recorded_at_utc <= end:
            invalid(f"/metrics/{index}/recordedAtUtc")
        if metric.processed_passengers > request.passenger_count:
            invalid(f"/metrics/{index}/processedPassengers")
    for index, snapshot in enumerate(result.snapshots):
        if snapshot.zone_id not in zones:
            invalid(f"/snapshots/{index}/zoneId")
        if snapshot.elapsed_minutes > min(simulated, request.duration_minutes):
            invalid(f"/snapshots/{index}/elapsedMinutes")
        if snapshot.queued_passengers + snapshot.processed_passengers > request.passenger_count:
            invalid(f"/snapshots/{index}/queuedPassengers")
    return errors


def optimization(request, result):
    errors = []
    for field, actual, expected in (("optimizationId", result.optimization_id, request.optimization_id),
                                    ("baselineRunId", result.baseline_run_id, request.baseline.run_id),
                                    ("objective", result.objective, request.objective)):
        if actual != expected:
            errors.append(error("/" + field, "invalid_result"))
    if result.status == "completed" and {item.run_id for item in request.candidates} != {item.run_id for item in result.candidates}:
        errors.append(error("/candidates", "invalid_result"))
    return errors

namespace AeroSynapse.Application.Contracts;

// Consumers must correlate an engine response with the exact request before persisting it.
public static class ContractCorrelation
{
    public static IReadOnlyList<ContractFieldError> Validate(SimulationRequest request, SimulationResponse response)
    {
        var legacy = new SimulationResult(response.ContractVersion, response.RunId, response.Status,
            response.Metrics, response.Snapshots.Select(x => new SimulationSnapshot(x.ZoneId, x.ElapsedMinutes,
                x.QueuedPassengers, x.ProcessedPassengers)).ToArray(), response.Failure);
        var errors = Validate(request, legacy).ToList();
        if (request.Journey is not null || response.JourneyResult is not null)
        {
            if (!JourneyCorrelation.Valid(request, response)) errors.Add(VersionedContract.Error("/journeyResult", "invalid_result"));
            return errors;
        }
        if (!ProcessCorrelation.Valid(request, response)) errors.Add(VersionedContract.Error("/processResult", "invalid_result"));
        if (!KpiCorrelation.Valid(request, response)) errors.Add(VersionedContract.Error("/kpiResult", "invalid_result"));
        if (!CheckInCorrelation.Valid(request, response)) errors.Add(VersionedContract.Error("/checkInResult", "invalid_result"));
        if (request.Navigation is not null && response.Status == "completed"
            && (response.NavigationResult is null || request.PassengerGeneration is null
                || !NavigationCorrelation.Valid(request.PassengerGeneration,request.Navigation,response.NavigationResult,true,0,100000,request,response.ProcessResult)))
            errors.Add(VersionedContract.Error("/navigationResult","invalid_result"));
        if (request.Navigation is null && response.NavigationResult is not null)
            errors.Add(VersionedContract.Error("/navigationResult","invalid_result"));
        if (response.SimulatedMinutes > request.DurationMinutes)
            errors.Add(VersionedContract.Error("/simulatedMinutes", "invalid_result"));
        var end = request.StartsAtUtc.AddMinutes(Math.Min(response.SimulatedMinutes, request.DurationMinutes));
        for (var index = 0; index < response.Metrics.Count; index++)
            if (response.Metrics[index].RecordedAtUtc > end && response.Metrics[index].RecordedAtUtc <= request.StartsAtUtc.AddMinutes(request.DurationMinutes))
                errors.Add(VersionedContract.Error($"/metrics/{index}/recordedAtUtc", "invalid_result"));
        for (var index = 0; index < response.Snapshots.Count; index++)
            if (response.Snapshots[index].ElapsedMinutes > response.SimulatedMinutes && response.Snapshots[index].ElapsedMinutes <= request.DurationMinutes)
                errors.Add(VersionedContract.Error($"/snapshots/{index}/elapsedMinutes", "invalid_result"));
        return errors;
    }

    public static IReadOnlyList<ContractFieldError> Validate(OptimizationRequest request, OptimizationResponse response) =>
        Validate(request, new OptimizationResult(response.ContractVersion, response.OptimizationId, response.Status,
            response.Objective, response.BaselineRunId, response.BaselineScore, response.Candidates, response.RecommendedRunId, response.Failure));

    public static IReadOnlyList<ContractFieldError> Validate(SimulationRequest request, SimulationResult result)
    {
        var errors = new List<ContractFieldError>();
        void Invalid(string path) => errors.Add(VersionedContract.Error(path, "invalid_result"));
        if (result.RunId != request.RunId) Invalid("/runId");
        var zones = request.Checkpoints.Select(x => x.CheckpointId).ToHashSet();
        var end = request.StartsAtUtc.AddMinutes(request.DurationMinutes);
        // Surges add demand beyond the captured passenger count.
        var demand = (long)request.PassengerCount + new EventTimeline(request).SurgeTotal;
        for (var index = 0; index < result.Metrics.Count; index++)
        {
            var metric = result.Metrics[index];
            if (!zones.Contains(metric.ZoneId)) Invalid($"/metrics/{index}/zoneId");
            if (metric.RecordedAtUtc < request.StartsAtUtc || metric.RecordedAtUtc > end) Invalid($"/metrics/{index}/recordedAtUtc");
            if (metric.ProcessedPassengers > demand) Invalid($"/metrics/{index}/processedPassengers");
        }
        for (var index = 0; index < result.Snapshots.Count; index++)
        {
            var snapshot = result.Snapshots[index];
            if (!zones.Contains(snapshot.ZoneId)) Invalid($"/snapshots/{index}/zoneId");
            if (snapshot.ElapsedMinutes > request.DurationMinutes) Invalid($"/snapshots/{index}/elapsedMinutes");
            if ((long)snapshot.ProcessedPassengers + snapshot.QueuedPassengers > demand) Invalid($"/snapshots/{index}/queuedPassengers");
        }
        return errors;
    }

    public static IReadOnlyList<ContractFieldError> Validate(OptimizationRequest request, OptimizationResult result)
    {
        var errors = new List<ContractFieldError>();
        if (result.OptimizationId != request.OptimizationId) errors.Add(VersionedContract.Error("/optimizationId", "invalid_result"));
        if (result.BaselineRunId != request.Baseline.RunId) errors.Add(VersionedContract.Error("/baselineRunId", "invalid_result"));
        if (result.Objective != request.Objective) errors.Add(VersionedContract.Error("/objective", "invalid_result"));
        if (result.Status == "completed" && !request.Candidates.Select(x => x.RunId).ToHashSet().SetEquals(result.Candidates.Select(x => x.RunId)))
            errors.Add(VersionedContract.Error("/candidates", "invalid_result"));
        return errors;
    }
}

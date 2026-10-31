using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Storage;
using AeroSynapse.Application.Simulation;
using MediatR;

namespace AeroSynapse.Application.Management;

public sealed record ForecastQueuesQuery(Guid RunId, QueueForecastOptions Options, bool UseLatest = false) : IRequest<QueueForecastResult>;
public sealed record PredictQueuesQuery(Guid RunId, QueueForecastOptions Options, bool UseLatest = false) : IRequest<QueuePredictionResult>;

public sealed class QueueForecastOperations(IRunStorageService runs, ISender sender, IQueueForecaster forecaster, IQueuePredictor predictor,
    IIntelligenceClient intelligence) : IRequestHandler<ForecastQueuesQuery, QueueForecastResult>, IRequestHandler<PredictQueuesQuery, QueuePredictionResult>
{
    public async Task<QueueForecastResult> Handle(ForecastQueuesQuery query, CancellationToken token) =>
        await forecaster.ForecastAsync(await RequestAsync(query.RunId, query.Options, query.UseLatest, token), token);

    // The predictor sees exactly the forecast's inputs, and its deterministic baseline must match the validated forecast.
    public async Task<QueuePredictionResult> Handle(PredictQueuesQuery query, CancellationToken token)
    {
        var request = await RequestAsync(query.RunId, query.Options, query.UseLatest, token);
        var forecast = await forecaster.ForecastAsync(request, token);
        var prediction = await predictor.PredictAsync(request, token);
        return QueuePredictionValidation.Correlates(request, forecast, prediction) ? prediction : throw new QueueForecasterUnavailableException();
    }

    private async Task<QueueForecastRequest> RequestAsync(Guid runId, QueueForecastOptions value, bool useLatest, CancellationToken token)
    {
        var options = ContractJson.Read<QueueForecastOptions>(JsonSerializer.SerializeToElement(value, ContractJson.Options), "QueueForecastOptions");
        var run = await runs.GetAsync(runId, token) ?? throw new ScenarioNotFoundException("Run was not found.");
        if (useLatest ? run.Status != "running" : !run.ResultsValid)
            throw new ScenarioConflictException(useLatest ? "Live forecasting requires a running simulation." : "Choose live forecasting while the simulation is running, or a captured snapshot after completion.");
        if (run.InputSnapshotJson is null) throw new ScenarioNotFoundException("This historical run has no captured forecast inputs.");
        var captured = JsonSerializer.Deserialize<RunInputSnapshot>(run.InputSnapshotJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        if (captured.Engine != KpiConventions.Engine || captured.Scenario.Inputs.PassengerGeneration?.Flow is not null)
            throw new ScenarioConflictException("Forecasts require captured KPI data without alternative passenger flows.");
        var simulation = CapturedSimulation.Request(run.Id, captured);
        IReadOnlyList<QueueForecastBacklog> backlogs;
        if (useLatest)
        {
            EngineExecution? execution;
            try { execution = await intelligence.GetAsync(run.Id, run.Id.ToString("N"), token); }
            catch (IntelligenceException) { throw new QueueForecasterUnavailableException(); }
            if (execution?.Status != "running" || execution.Progress is not { } progress)
                throw new ScenarioConflictException("A live observation is not available yet, or this simulation has just finished. The monitor will refresh with the next state.");
            if (!double.IsFinite(progress.SimulationMinute) || progress.SimulationMinute != Math.Truncate(progress.SimulationMinute)
                || progress.SimulationMinute < 0 || progress.SimulationMinute > simulation.DurationMinutes
                || progress.Zones.Count != simulation.Checkpoints.Count
                || progress.Zones.Select(row => row.ZoneId).Distinct().Count() != progress.Zones.Count
                || !progress.Zones.Select(row => row.ZoneId).ToHashSet().SetEquals(simulation.Checkpoints.Select(cp => cp.CheckpointId))
                || progress.Zones.Any(row => row.QueuedPassengers is null or < 0 || row.NotArrivedPassengers is null or < 0))
                throw new QueueForecasterUnavailableException();
            options = options with { FromMinute = (int)progress.SimulationMinute };
            backlogs = progress.Zones.Select(row => new QueueForecastBacklog(row.ZoneId, row.QueuedPassengers!.Value, row.NotArrivedPassengers!.Value)).ToArray();
        }
        else
        {
            var report = await sender.Send(new GetRunKpisQuery(run.Id, FromMinute: options.FromMinute, ToMinute: options.FromMinute, Limit: KpiConventions.MaxSnapshots), token);
            if (report.Snapshots.Count != simulation.Checkpoints.Count || report.RunId != run.Id
                || report.Snapshots.Any(row => row.Minute != options.FromMinute || row.RunId != run.Id)
                || !report.Snapshots.Select(row => row.ZoneId).ToHashSet().SetEquals(simulation.Checkpoints.Select(cp => cp.CheckpointId)))
                throw Invalid("fromMinute", "Choose a captured KPI snapshot minute with observations for every checkpoint.");
            backlogs = report.Snapshots.Select(row => new QueueForecastBacklog(row.ZoneId, row.QueuedPassengers, row.NotArrivedPassengers)).ToArray();
        }
        if (options.FromMinute + options.HorizonMinutes > simulation.DurationMinutes)
            throw Invalid("fromMinute", "The complete 15–30 minute forecast must fit inside the captured simulation period.");
        var request = new QueueForecastRequest(simulation, options, backlogs, useLatest ? "live_simulation" : "captured_snapshot");
        try { return ContractJson.Read<QueueForecastRequest>(JsonSerializer.SerializeToElement(request, ContractJson.Options), "QueueForecastRequest"); }
        catch (ContractValidationException) when (useLatest) { throw new QueueForecasterUnavailableException(); }
    }

    private static ScenarioValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });
}

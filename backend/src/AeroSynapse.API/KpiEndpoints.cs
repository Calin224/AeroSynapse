using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Storage;
using MediatR;
using System.Text.Json;

namespace AeroSynapse.API;

internal static class KpiEndpoints
{
    public static void MapKpiEndpoints(this WebApplication app)
    {
        app.MapPost("/runs/{id}/queue-forecast/live", async (Guid id, JsonElement payload, ISender sender, CancellationToken token) =>
        {
            var options = ContractJson.Read<QueueForecastLiveOptions>(payload, "QueueForecastLiveOptions");
            return Results.Json(await sender.Send(new ForecastQueuesQuery(id, new(0, options.HorizonMinutes, options.QueueThresholdPassengers), UseLatest: true), token), ContractJson.Options);
        }).AddEndpointFilter<ScenarioErrorFilter>().Accepts<QueueForecastLiveOptions>("application/json")
            .Document<QueueForecastResult>("Forecast from the latest observed state of a running simulation; no future snapshots are used").ProducesProblem(502);
        app.MapPost("/runs/{id}/queue-forecast", async (Guid id, JsonElement payload, ISender sender, CancellationToken token) =>
            Results.Json(await sender.Send(new ForecastQueuesQuery(id,
                ContractJson.Read<QueueForecastOptions>(payload, "QueueForecastOptions")), token), ContractJson.Options))
            .AddEndpointFilter<ScenarioErrorFilter>().Accepts<QueueForecastOptions>("application/json")
            .Document<QueueForecastResult>("Forecast queues for 15–30 simulated minutes from a captured KPI snapshot; advisory only").ProducesProblem(502);
        app.MapPost("/runs/{id}/queue-prediction/live", async (Guid id, JsonElement payload, ISender sender, CancellationToken token) =>
        {
            var options = ContractJson.Read<QueueForecastLiveOptions>(payload, "QueueForecastLiveOptions");
            return Results.Json(await sender.Send(new PredictQueuesQuery(id, new(0, options.HorizonMinutes, options.QueueThresholdPassengers), UseLatest: true), token), ContractJson.Options);
        }).AddEndpointFilter<ScenarioErrorFilter>().Accepts<QueueForecastLiveOptions>("application/json")
            .Document<QueuePredictionResult>("Research predictor on the latest live observation; synthetic training data, advisory only").ProducesProblem(502);
        app.MapPost("/runs/{id}/queue-prediction", async (Guid id, JsonElement payload, ISender sender, CancellationToken token) =>
            Results.Json(await sender.Send(new PredictQueuesQuery(id,
                ContractJson.Read<QueueForecastOptions>(payload, "QueueForecastOptions")), token), ContractJson.Options))
            .AddEndpointFilter<ScenarioErrorFilter>().Accepts<QueueForecastOptions>("application/json")
            .Document<QueuePredictionResult>("Research predictor beside the deterministic forecast; falls back to it when disabled").ProducesProblem(502);
        app.MapPut("/scenarios/{id}/kpis/settings", async (Guid id, KpiWriteRequest value, ISender sender, CancellationToken token) =>
            Results.Ok(await sender.Send(new WriteKpiSettingsCommand(id, value), token)))
            .AddEndpointFilter<ScenarioErrorFilter>().Document<ScenarioDetails>("Save a 1–5 minute KPI cadence for the next captured run");
        app.MapGet("/runs/{id}/kpis", async (Guid id, Guid? zoneId, int? fromMinute, int? toMinute, int? offset, int? limit, ISender sender, CancellationToken token) =>
            Results.Ok(await sender.Send(new GetRunKpisQuery(id, zoneId, fromMinute, toMinute, offset ?? 0, limit ?? 100), token)))
            .AddEndpointFilter<ScenarioErrorFilter>().Document<KpiResult>("Read captured KPI populations and coherent temporal zone snapshots");
    }
}

using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Management;
using MediatR;
using System.Text.Json;

namespace AeroSynapse.API;

// Synthetic demonstration only; reviews record a judgement and trigger no operational action.
internal static class AnomalyEndpoints
{
    public static void MapAnomalyEndpoints(this WebApplication app)
    {
        app.MapPost("/runs/{id}/anomalies", async (Guid id, AnomalyRequest request, ISender sender, CancellationToken token) =>
            Results.Json(await sender.Send(new DetectAnomaliesQuery(id, request), token), ContractJson.Options))
            .AddEndpointFilter<ScenarioErrorFilter>()
            .Document<AnomalyReport>("Screen a completed run against the Monte Carlo replications of a batch baseline (aggregate counts only)").ProducesProblem(502);
        app.MapGet("/runs/{id}/anomalies/reviews", async (Guid id, ISender sender, CancellationToken token) =>
            Results.Json(await sender.Send(new ListAnomalyReviewsQuery(id), token), ContractJson.Options))
            .AddEndpointFilter<ScenarioErrorFilter>().Document<IReadOnlyList<AnomalyReviewDetails>>("Demo decisions on anomaly alerts of a run, oldest first");
        app.MapPost("/runs/{id}/anomalies/reviews", async (Guid id, AnomalyReviewRequest request, ISender sender, CancellationToken token) =>
            Results.Json(await sender.Send(new ReviewAnomalyCommand(id, request), token), ContractJson.Options))
            .AddEndpointFilter<ScenarioErrorFilter>()
            .Document<AnomalyReviewDetails>("Confirm or reject an alert the detector raises under the stated settings (demo, idempotent)").ProducesProblem(502);
        app.MapPost("/anomalies/evaluation", async (AnomalyEvaluationBody body, ISender sender, CancellationToken token) =>
            Results.Json(await sender.Send(new EvaluateAnomaliesQuery(body.Settings), token)))
            .AddEndpointFilter<ScenarioErrorFilter>()
            .Document<JsonElement>("False positives and negatives of the settings on the synthetic test set; status 'warming' while it is built").ProducesProblem(502);
        app.MapPost("/anomalies/evaluation/cases/{caseKey}", async (string caseKey, AnomalyEvaluationBody body, ISender sender, CancellationToken token) =>
            Results.Json(await sender.Send(new AnomalyCaseQuery(caseKey, body.Settings), token)))
            .AddEndpointFilter<ScenarioErrorFilter>()
            .Document<JsonElement>("One synthetic test case: label, outcome and the full detection").ProducesProblem(502);
    }
}

internal sealed record AnomalyEvaluationBody(AnomalySettings Settings);

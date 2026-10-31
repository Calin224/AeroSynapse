using AeroSynapse.Application.Management;

namespace AeroSynapse.API;

internal static class BatchEndpoints
{
    public static void MapBatchEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/simulation-batches").WithTags("Monte Carlo batches").AddEndpointFilter<ScenarioErrorFilter>();
        group.MapPost("", async (CreateBatchRequest request, IBatchService service, CancellationToken token) =>
        { var batch = await service.CreateAsync(request, token); return Results.Accepted($"/simulation-batches/{batch.Batch.Id}", batch); })
            .Document<BatchDetails>("Capture a reproducible batch with explicit shared seeds and a wall-clock deadline", 202);
        group.MapGet("", async (Guid? scenarioId, IBatchService service, CancellationToken token) => Results.Ok(await service.ListAsync(scenarioId, token)))
            .Document<IReadOnlyList<BatchSummary>>("List the latest 50 retained batches");
        group.MapGet("/{batchId:guid}", async (Guid batchId, IBatchService service, CancellationToken token) => Results.Ok(await service.GetAsync(batchId, token)))
            .Document<BatchDetails>("Read every replication, descriptive variability and seed-paired differences");
        group.MapPost("/{batchId:guid}/cancel", async (Guid batchId, IBatchService service, CancellationToken token) =>
            Results.Accepted($"/simulation-batches/{batchId}", await service.CancelAsync(batchId, token)))
            .Document<BatchDetails>("Request durable cancellation, retaining completed and failed replications", 202);
        group.MapPost("/{batchId:guid}/replay", async (Guid batchId, ReplayBatchRequest request, IBatchService service, CancellationToken token) =>
        { var batch = await service.ReplayAsync(batchId, request, token); return Results.Accepted($"/simulation-batches/{batch.Batch.Id}", batch); })
            .Document<BatchDetails>("Replay the original captured variants and exact ordered seed list", 202);
    }
}

using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using AeroSynapse.Application.Management;
using MediatR;

namespace AeroSynapse.API;

internal static class RunStorageEndpoints
{
    public static void MapRunStorageEndpoints(this WebApplication app)
    {
        var runs = app.MapGroup("/runs/{runId}").WithTags("Runs").AddEndpointFilter<ScenarioErrorFilter>();
        runs.MapGet("/replay", async (Guid runId, IReplayService service, CancellationToken token) => Results.Ok(await service.GetAsync(runId, token)))
            .Document<ReplayEnvelope>("Read a hashed replay bundle exclusively from persisted run data");
        runs.MapGet("/bookmarks", async (Guid runId, IReplayService service, CancellationToken token) => Results.Ok(await service.BookmarksAsync(runId, token)))
            .Document<IReadOnlyList<ReplayBookmark>>("Read operational replay annotations");
        runs.MapPut("/bookmarks/{bookmarkId:guid}", async (Guid runId, Guid bookmarkId, BookmarkWriteRequest request, IReplayService service, CancellationToken token) =>
            Results.Ok(await service.WriteBookmarkAsync(runId, bookmarkId, request, token)))
            .Document<ReplayBookmark>("Create or edit a replay bookmark using its revision; identical retries are idempotent");
        runs.MapDelete("/bookmarks/{bookmarkId:guid}", async (Guid runId, Guid bookmarkId, int revision, IReplayService service, CancellationToken token) =>
        { await service.DeleteBookmarkAsync(runId, bookmarkId, revision, token); return Results.NoContent(); })
            .WithSummary("Delete a replay bookmark using its revision").Produces(204).ProducesProblem(409);
        runs.MapGet("/compare/{candidateId:guid}", async (Guid runId, Guid candidateId, IRunComparisonService service, CancellationToken token) =>
            Results.Ok(await service.CompareAsync(runId, candidateId, token)))
            .Document<RunComparison>("Compare input fingerprints, engine builds, result fingerprints and final metrics for two runs");
        runs.MapGet("/journey", async (Guid runId, int? offset, int? limit, ISender sender, CancellationToken token) =>
            Results.Ok(await sender.Send(new GetRunJourneyQuery(runId, offset ?? 0, limit ?? 100), token)))
            .Document<AeroSynapse.Application.Contracts.JourneyReport>("Read captured passport queues, transfers, boarding and the rolling gate schedule");
        runs.MapGet("/ai-optimization", async (Guid runId, AeroSynapse.Infrastructure.Execution.RunOptimizationService optimization, CancellationToken token) =>
            await optimization.GetAsync(runId, token) is { } value ? Results.Ok(value) : Results.NoContent())
            .Document<RunOptimization>("Staffing options for a finished run, each simulated on the same day and seed, explained and ranked by the AI model");
        runs.MapPost("/ai-optimization/apply", async (Guid runId, OptimizationApplyRequest request, AeroSynapse.Infrastructure.Execution.RunOptimizationService optimization, CancellationToken token) =>
            Results.Ok(await optimization.ApplyAsync(runId, request.StaffDelta, request.DeskDelta, token)))
            .Document<ScenarioDetails>("Write a staffing option into the run's scenario as a new revision; 409 once the scenario changed since the run")
            .ProducesProblem(409);
        runs.MapGet("/processes", async (Guid runId, int? offset, int? limit, ISender sender, CancellationToken token) =>
            Results.Ok(await sender.Send(new GetRunProcessesQuery(runId, offset ?? 0, limit ?? 100), token)))
            .Document<AeroSynapse.Application.Contracts.ProcessResult>("Read captured SimPy resource capacity and passenger stage timestamps");
        runs.MapGet("", async (Guid runId, ISender sender, CancellationToken token) => Results.Ok(await sender.Send(new GetRunQuery(runId), token)))
            .Document<RunDetails>("Get status, valid results and readable error");
        runs.MapPost("/retry", async (Guid runId, RevisionRequest request, ISender sender, CancellationToken token) =>
        {
            var run = await sender.Send(new RetryRunCommand(runId, request.Revision), token);
            return Results.Accepted($"/runs/{run.Id}", run);
        }).Document<SimulationRun>("Retry a failed run from the exact original snapshot", 202);
        runs.MapPatch("/status", async (Guid runId, RunTransitionRequest request, ISender sender, CancellationToken token) =>
            Results.Ok(await sender.Send(new TransitionRunCommand(runId, request), token))).Document<SimulationRun>("Transition an external run");
        runs.MapPost("/metrics", async (Guid runId, RunMetricRequest request, ISender sender, CancellationToken token) =>
        {
            var result = await sender.Send(new AddRunMetricCommand(runId, request), token);
            return Results.Created($"/runs/{runId}/metrics", new RunResults<RunMetric>(runId, "running", false, null, [result]));
        }).Document<RunResults<RunMetric>>("Append an external metric", 201);
        runs.MapPost("/snapshots", async (Guid runId, RunSnapshotRequest request, ISender sender, CancellationToken token) =>
        {
            var result = await sender.Send(new AddRunSnapshotCommand(runId, request), token);
            return Results.Created($"/runs/{runId}/snapshots", new RunResults<RunSnapshot>(runId, "running", false, null, [result]));
        }).Document<RunResults<RunSnapshot>>("Append an external snapshot", 201);
        runs.MapGet("/metrics", async (Guid runId, DateTime? fromUtc, DateTime? toUtc, string? zoneType,
            Guid? zoneId, int? limit, bool? includeInvalid, ISender sender, CancellationToken token) =>
            Results.Ok(await sender.Send(new GetRunMetricsQuery(runId, new(fromUtc, toUtc, zoneType, zoneId, limit ?? 50, includeInvalid ?? false)), token)))
            .Document<RunResults<RunMetric>>("Query metrics by UTC time and zone; limit 1–100");
        runs.MapGet("/snapshots", async (Guid runId, DateTime? fromUtc, DateTime? toUtc, string? zoneType,
            Guid? zoneId, int? limit, bool? includeInvalid, ISender sender, CancellationToken token) =>
            Results.Ok(await sender.Send(new GetRunSnapshotsQuery(runId, new(fromUtc, toUtc, zoneType, zoneId, limit ?? 50, includeInvalid ?? false)), token)))
            .Document<RunResults<RunSnapshot>>("Query snapshots by UTC time and zone; invalid data requires includeInvalid=true");
        runs.MapGet("/recommendations", async (Guid runId, IRunStorageService service, CancellationToken token) =>
            Results.Ok(await service.RecommendationsAsync(runId, token)));
        var recommendations = app.MapGroup("/recommendations").AddEndpointFilter<ScenarioErrorFilter>();
        recommendations.MapPost("/", async (RecommendationRequest request, IRunStorageService service, CancellationToken token) =>
        {
            var result = await service.RecommendAsync(request, token);
            return Results.Created($"/recommendations/{result.Recommendation.Id}", result);
        });
        recommendations.MapGet("/{id:guid}", async (Guid id, IRunStorageService service, CancellationToken token) =>
        {
            var result = await service.RecommendationAsync(id, token);
            return result is null ? (IResult)Results.NotFound() : Results.Ok(result);
        });
        app.MapDelete("/scenarios/{id:guid}", async (Guid id, string? retention, int? revision, IRunStorageService service, CancellationToken token) =>
            Results.Ok(await service.DeleteScenarioAsync(id, retention ?? "archive", revision ?? 0, token)))
            .AddEndpointFilter<ScenarioErrorFilter>();
    }
}

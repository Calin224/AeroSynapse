using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Storage;
using MediatR;

namespace AeroSynapse.API;

internal static class PassengerEndpoints
{
    public static void MapPassengerEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/scenarios/{id}/passengers").WithTags("Synthetic passengers").AddEndpointFilter<ScenarioErrorFilter>();
        group.MapGet("/settings", async (Guid id, ISender sender, CancellationToken token) => Results.Ok(await sender.Send(new GetPassengerSettingsQuery(id), token)))
            .Document<PassengerSettings>("Get persisted passenger profiles and flight-specific defaults");
        group.MapPut("/settings", async (Guid id, PassengerConfigWriteRequest request, ISender sender, CancellationToken token) =>
            Results.Ok(await sender.Send(new WritePassengerConfigCommand(id, request), token)))
            .Document<ScenarioDetails>("Atomically save synthetic passenger profiles, seed, populations and flows");
        group.MapGet("", async (Guid id, int revision, int? offset, int? limit, ISender sender, CancellationToken token) =>
            Results.Ok(await sender.Send(new PreviewPassengersQuery(id, revision, offset ?? 0, limit ?? 100), token)))
            .Document<PassengerGenerationResult>("Generate a deterministic page using the persisted revision").ProducesProblem(502);
        group.MapGet("/flows", async (Guid id, int revision, ISender sender, CancellationToken token) =>
            Results.Ok(await sender.Send(new AnalyzePassengerFlowsQuery(id, revision), token)))
            .Document<PassengerFlowReport>("Simulate alternative passenger routes and report aggregate statistics per stage and type").ProducesProblem(502);
        app.MapGet("/runs/{id}/passengers/flows", async (Guid id, ISender sender, CancellationToken token) =>
            Results.Ok(await sender.Send(new ReplayRunPassengerFlowsQuery(id), token)))
            .AddEndpointFilter<ScenarioErrorFilter>().Document<PassengerFlowReport>("Reproduce the flow report from an immutable run snapshot").ProducesProblem(502);
        app.MapGet("/runs/{id}/passengers", async (Guid id, int? offset, int? limit, ISender sender, CancellationToken token) =>
            Results.Ok(await sender.Send(new ReplayRunPassengersQuery(id, offset ?? 0, limit ?? 100), token)))
            .AddEndpointFilter<ScenarioErrorFilter>().Document<PassengerGenerationResult>("Reproduce passenger arrivals and services from an immutable run snapshot").ProducesProblem(502);
    }
}

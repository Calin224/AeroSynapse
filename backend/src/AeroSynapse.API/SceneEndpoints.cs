using AeroSynapse.Application.Management;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using MediatR;
namespace AeroSynapse.API;
internal static class SceneEndpoints
{
    public static void MapSceneEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/scenes").WithTags("Operational airports").AddEndpointFilter<ScenarioErrorFilter>();
        group.MapGet("", async (ISender sender, CancellationToken token) => Results.Ok(await sender.Send(new ListScenesQuery(), token)))
            .Document<IReadOnlyList<SceneSummary>>("List operational airport scenes");
        group.MapGet("/{id}", async (Guid id, ISender sender, CancellationToken token) => Results.Ok(await sender.Send(new GetSceneQuery(id), token)))
            .Document<SceneDetails>("Get complete operational airport layout");
        group.MapGet("/{id}/events", async (Guid id, int? limit, ISender sender, CancellationToken token) => Results.Ok(await sender.Send(new ListSceneEventsQuery(id, limit ?? 50), token)))
            .Document<IReadOnlyList<SceneEvent>>("List airport events; limit 1–100");
        group.MapGet("/{id}/runs", async (Guid id, int? limit, ISender sender, CancellationToken token) => Results.Ok(await sender.Send(new ListSceneRunsQuery(id, limit ?? 50), token)))
            .Document<IReadOnlyList<SimulationRun>>("List airport runs; limit 1–100");
    }
}

using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using MediatR;
namespace AeroSynapse.Application.Management;
public sealed record ListScenesQuery : IRequest<IReadOnlyList<SceneSummary>>;
public sealed record GetSceneQuery(Guid Id) : IRequest<SceneDetails>;
public sealed record ListSceneEventsQuery(Guid Id, int Limit) : IRequest<IReadOnlyList<SceneEvent>>;
public sealed record ListSceneRunsQuery(Guid Id, int Limit) : IRequest<IReadOnlyList<SimulationRun>>;
public sealed class SceneQueries(ISceneQueryService service) :
    IRequestHandler<ListScenesQuery, IReadOnlyList<SceneSummary>>, IRequestHandler<GetSceneQuery, SceneDetails>,
    IRequestHandler<ListSceneEventsQuery, IReadOnlyList<SceneEvent>>, IRequestHandler<ListSceneRunsQuery, IReadOnlyList<SimulationRun>>
{
    public Task<IReadOnlyList<SceneSummary>> Handle(ListScenesQuery request, CancellationToken token) => service.ListAsync(token);
    public async Task<SceneDetails> Handle(GetSceneQuery request, CancellationToken token) =>
        await service.GetAsync(request.Id, token) ?? throw new ScenarioNotFoundException("Scene was not found.");
    public async Task<IReadOnlyList<SceneEvent>> Handle(ListSceneEventsQuery request, CancellationToken token)
    {
        Limit(request.Limit); await Handle(new GetSceneQuery(request.Id), token);
        return await service.EventsAsync(request.Id, request.Limit, token);
    }
    public async Task<IReadOnlyList<SimulationRun>> Handle(ListSceneRunsQuery request, CancellationToken token)
    {
        Limit(request.Limit); await Handle(new GetSceneQuery(request.Id), token);
        return await service.RunsAsync(request.Id, request.Limit, token);
    }
    public static void Limit(int limit)
    {
        if (limit is < 1 or > 100) throw new ScenarioValidationException(new Dictionary<string, string[]> { ["limit"] = ["Must be between 1 and 100."] });
    }
}

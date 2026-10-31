using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using MediatR;
namespace AeroSynapse.Application.Management;
public sealed record StartRunCommand(Guid ScenarioId) : IRequest<SimulationRun>;
public sealed record RetryRunCommand(Guid RunId, int Revision) : IRequest<SimulationRun>;
public sealed record GetRunQuery(Guid RunId) : IRequest<RunDetails>;
public sealed record ListScenarioRunsQuery(Guid ScenarioId, int Limit) : IRequest<IReadOnlyList<SimulationRun>>;
public sealed record GetScenarioRunQuery(Guid ScenarioId, Guid RunId) : IRequest<RunDetails>;
public sealed record GetRunMetricsQuery(Guid RunId, RunResultQuery Query) : IRequest<RunResults<RunMetric>>;
public sealed record GetRunSnapshotsQuery(Guid RunId, RunResultQuery Query) : IRequest<RunResults<RunSnapshot>>;
public sealed record TransitionRunCommand(Guid RunId, RunTransitionRequest Value) : IRequest<SimulationRun>;
public sealed record AddRunMetricCommand(Guid RunId, RunMetricRequest Value) : IRequest<RunMetric>;
public sealed record AddRunSnapshotCommand(Guid RunId, RunSnapshotRequest Value) : IRequest<RunSnapshot>;
public sealed class RunOperations(IRunExecutionService execution, IScenarioService scenarios, IRunStorageService storage) :
    IRequestHandler<StartRunCommand, SimulationRun>, IRequestHandler<RetryRunCommand, SimulationRun>, IRequestHandler<GetRunQuery, RunDetails>,
    IRequestHandler<ListScenarioRunsQuery, IReadOnlyList<SimulationRun>>, IRequestHandler<GetScenarioRunQuery, RunDetails>,
    IRequestHandler<GetRunMetricsQuery, RunResults<RunMetric>>, IRequestHandler<GetRunSnapshotsQuery, RunResults<RunSnapshot>>,
    IRequestHandler<TransitionRunCommand, SimulationRun>, IRequestHandler<AddRunMetricCommand, RunMetric>, IRequestHandler<AddRunSnapshotCommand, RunSnapshot>
{
    public Task<SimulationRun> Handle(StartRunCommand request, CancellationToken token) => execution.StartAsync(request.ScenarioId, token);
    public Task<SimulationRun> Handle(RetryRunCommand request, CancellationToken token) => execution.RetryAsync(request.RunId, request.Revision, token);
    public Task<RunDetails> Handle(GetRunQuery request, CancellationToken token) => execution.GetAsync(request.RunId, token);
    public async Task<IReadOnlyList<SimulationRun>> Handle(ListScenarioRunsQuery request, CancellationToken token)
    {
        SceneQueries.Limit(request.Limit);
        if (await scenarios.GetAsync(request.ScenarioId, token) is null) throw new ScenarioNotFoundException("Scenario was not found.");
        return await scenarios.RunsAsync(request.ScenarioId, request.Limit, token);
    }
    public async Task<RunDetails> Handle(GetScenarioRunQuery request, CancellationToken token)
    {
        var result = await execution.GetAsync(request.RunId, token);
        if (result.ScenarioId != request.ScenarioId) throw new ScenarioNotFoundException("Run was not found in this scenario.");
        return result;
    }
    public Task<RunResults<RunMetric>> Handle(GetRunMetricsQuery request, CancellationToken token) => storage.MetricsAsync(request.RunId, request.Query, token);
    public Task<RunResults<RunSnapshot>> Handle(GetRunSnapshotsQuery request, CancellationToken token) => storage.SnapshotsAsync(request.RunId, request.Query, token);
    public async Task<SimulationRun> Handle(TransitionRunCommand request, CancellationToken token)
    {
        await RequireExternal(request.RunId, token); return await storage.TransitionAsync(request.RunId, request.Value, token);
    }
    public async Task<RunMetric> Handle(AddRunMetricCommand request, CancellationToken token)
    {
        await RequireExternal(request.RunId, token); return await storage.AddMetricAsync(request.RunId, request.Value, token);
    }
    public async Task<RunSnapshot> Handle(AddRunSnapshotCommand request, CancellationToken token)
    {
        await RequireExternal(request.RunId, token); return await storage.AddSnapshotAsync(request.RunId, request.Value, token);
    }
    private async Task RequireExternal(Guid id, CancellationToken token)
    {
        var run = await storage.GetAsync(id, token) ?? throw new ScenarioNotFoundException("Run was not found.");
        if (run.ExecutionRequested) throw new ScenarioConflictException("Managed runs are updated by the execution worker.");
    }
}

using AeroSynapse.Domain.DigitalTwin;

namespace AeroSynapse.Application.Storage;

public sealed record SceneSummary(Guid Id, string Code, string Name, string Description, bool IsSynthetic);
// Layout versions published before check-in areas existed have none.
public sealed record SceneDetails(SceneSummary Scene, IReadOnlyList<Terminal> Terminals,
    IReadOnlyList<Checkpoint> Checkpoints, IReadOnlyList<Gate> Gates, IReadOnlyList<StaffMember> Staff,
    IReadOnlyList<PassengerRoute> Routes, IReadOnlyList<CheckInArea>? CheckInAreas = null);

public interface ISceneQueryService
{
    Task<IReadOnlyList<SceneSummary>> ListAsync(CancellationToken cancellationToken = default);
    Task<SceneDetails?> GetAsync(Guid sceneId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SceneEvent>> EventsAsync(Guid sceneId, int limit, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SimulationRun>> RunsAsync(Guid sceneId, int limit, CancellationToken cancellationToken = default);
}

public interface ISimulationRunRepository
{
    Task<SimulationRun?> FindAsync(Guid sceneId, Guid runId, CancellationToken cancellationToken = default);
    Task AddAsync(SimulationRun run, CancellationToken cancellationToken = default);
}

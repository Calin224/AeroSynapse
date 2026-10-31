using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using Microsoft.EntityFrameworkCore;

namespace AeroSynapse.Infrastructure.Storage;

public sealed class SceneQueryService(TwinDbContext database) : ISceneQueryService
{
    public async Task<IReadOnlyList<SceneSummary>> ListAsync(CancellationToken cancellationToken = default) =>
        await database.Scenes.AsNoTracking().OrderBy(x => x.Code).Take(100)
            .Select(x => new SceneSummary(x.Id, x.Code, x.Name, x.Description, x.IsSynthetic)).ToArrayAsync(cancellationToken);

    public async Task<SceneDetails?> GetAsync(Guid sceneId, CancellationToken cancellationToken = default)
    {
        var scene = await database.Scenes.AsNoTracking().Where(x => x.Id == sceneId)
            .Select(x => new SceneSummary(x.Id, x.Code, x.Name, x.Description, x.IsSynthetic)).SingleOrDefaultAsync(cancellationToken);
        if (scene is null) return null;
        return new SceneDetails(scene,
            await database.Terminals.AsNoTracking().Where(x => x.SceneId == sceneId).OrderBy(x => x.Code).ToArrayAsync(cancellationToken),
            await database.Checkpoints.AsNoTracking().Where(x => x.SceneId == sceneId).OrderBy(x => x.Code).ToArrayAsync(cancellationToken),
            await database.Gates.AsNoTracking().Where(x => x.SceneId == sceneId).OrderBy(x => x.Code).ToArrayAsync(cancellationToken),
            await database.Staff.AsNoTracking().Where(x => x.SceneId == sceneId).OrderBy(x => x.Code).ToArrayAsync(cancellationToken),
            await database.Routes.AsNoTracking().Where(x => x.SceneId == sceneId).OrderBy(x => x.Code).ToArrayAsync(cancellationToken),
            await database.CheckInAreas.AsNoTracking().Where(x => x.SceneId == sceneId).OrderBy(x => x.Code).ToArrayAsync(cancellationToken));
    }

    public async Task<IReadOnlyList<SceneEvent>> EventsAsync(Guid sceneId, int limit, CancellationToken cancellationToken = default)
    {
        ValidateLimit(limit);
        return await database.Events.AsNoTracking().Where(x => x.SceneId == sceneId)
            .OrderByDescending(x => x.OccurredAtUtc).ThenByDescending(x => x.Id).Take(limit).ToArrayAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SimulationRun>> RunsAsync(Guid sceneId, int limit, CancellationToken cancellationToken = default)
    {
        ValidateLimit(limit);
        return await database.Runs.AsNoTracking().Where(x => x.SceneId == sceneId)
            .OrderByDescending(x => x.StartedAtUtc).ThenByDescending(x => x.Id).Take(limit).ToArrayAsync(cancellationToken);
    }

    private static void ValidateLimit(int limit)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 100.");
    }
}

public sealed class SimulationRunRepository(TwinDbContext database) : ISimulationRunRepository
{
    public Task<SimulationRun?> FindAsync(Guid sceneId, Guid runId, CancellationToken cancellationToken = default) =>
        database.Runs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == runId && x.SceneId == sceneId, cancellationToken);

    public async Task AddAsync(SimulationRun run, CancellationToken cancellationToken = default)
    {
        database.Runs.Add(run);
        await database.SaveChangesAsync(cancellationToken);
    }
}

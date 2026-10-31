using System.Data;
using System.Text.Json;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AeroSynapse.Infrastructure.Storage;

public sealed class ScenarioService(TwinDbContext database) : IScenarioService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<PublishedLayout> PublishLayoutAsync(Guid sceneId, CancellationToken cancellationToken = default)
    {
        // All queries see the same layout, even if live geometry or staff is edited concurrently.
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var layout = await new SceneQueryService(database).GetAsync(sceneId, cancellationToken)
            ?? throw new ScenarioNotFoundException("Scene was not found.");
        var version = new LayoutVersion
        {
            Id = Guid.NewGuid(), SceneId = sceneId, IsSynthetic = layout.Scene.IsSynthetic,
            Version = (await database.LayoutVersions.Where(x => x.SceneId == sceneId)
                .MaxAsync(x => (int?)x.Version, cancellationToken) ?? 0) + 1,
            CreatedAtUtc = DateTime.UtcNow, LayoutJson = Serialize(layout)
        };
        database.LayoutVersions.Add(version);
        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new PublishedLayout(Summary(version), layout);
    }

    public async Task<IReadOnlyList<LayoutVersionSummary>> LayoutVersionsAsync(Guid sceneId, CancellationToken cancellationToken = default) =>
        await database.LayoutVersions.AsNoTracking().Where(x => x.SceneId == sceneId)
            .OrderByDescending(x => x.Version).Take(100)
            .Select(x => new LayoutVersionSummary(x.Id, x.SceneId, x.Version, x.CreatedAtUtc)).ToArrayAsync(cancellationToken);

    public async Task<PublishedLayout?> GetLayoutAsync(Guid sceneId, Guid versionId, CancellationToken cancellationToken = default)
    {
        var version = await database.LayoutVersions.AsNoTracking().SingleOrDefaultAsync(x => x.SceneId == sceneId && x.Id == versionId, cancellationToken);
        return version is null ? null : new PublishedLayout(Summary(version), Deserialize<SceneDetails>(version.LayoutJson));
    }

    public async Task<IReadOnlyList<ScenarioSummary>> ListAsync(Guid? sceneId, CancellationToken cancellationToken = default)
    {
        var query = database.Scenarios.AsNoTracking().Where(x => x.DeletedAtUtc == null);
        if (sceneId.HasValue) query = query.Where(x => x.SceneId == sceneId.Value);
        var rows = await query.OrderBy(x => x.Code).ThenBy(x => x.Id).Take(100)
            .Select(x => new { Value = new ScenarioSummary(x.Id, x.Code, x.Name, x.Description, x.SceneId, x.LayoutVersionId,
                x.Revision, x.CreatedAtUtc, x.UpdatedAtUtc, x.IsSynthetic, x.DeletedAtUtc), x.DataSourceJson }).ToArrayAsync(cancellationToken);
        return rows.Select(row => row.Value with { DataSource = row.DataSourceJson is null ? null : Deserialize<DataSourceProvenance>(row.DataSourceJson) }).ToArray();
    }

    public async Task<ScenarioDetails?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var scenario = await database.Scenarios.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return scenario is null ? null : Details(scenario);
    }

    public async Task<ScenarioValidationResult> ValidateAsync(ScenarioWriteRequest request, CancellationToken cancellationToken = default)
    {
        var version = await RequireLayoutAsync(request, cancellationToken);
        ScenarioValidator.Validate(request, Deserialize<SceneDetails>(version.LayoutJson));
        return new(true);
    }

    public Task<ScenarioDetails> CreateAsync(ScenarioWriteRequest request, CancellationToken cancellationToken = default) => CreateCoreAsync(request, null, cancellationToken);
    private async Task<ScenarioDetails> CreateCoreAsync(ScenarioWriteRequest request, string? dataSource, CancellationToken cancellationToken)
    {
        var version = await RequireLayoutAsync(request, cancellationToken);
        ScenarioValidator.Validate(request, Deserialize<SceneDetails>(version.LayoutJson));
        var now = DateTime.UtcNow;
        var scenario = new Scenario
        {
            Id = Guid.NewGuid(), SceneId = request.SceneId, LayoutVersionId = version.Id,
            Code = request.Code.Trim(), Name = request.Name.Trim(), Description = request.Description,
            IsSynthetic = true, CreatedAtUtc = now, UpdatedAtUtc = now,
            ConfigurationJson = Serialize(request.Inputs), DataSourceJson = dataSource
        };
        database.Scenarios.Add(scenario);
        await SaveAsync(cancellationToken);
        return Details(scenario);
    }

    public async Task<ScenarioDetails> UpdateAsync(Guid id, ScenarioWriteRequest request, CancellationToken cancellationToken = default)
    {
        var scenario = await database.Scenarios.SingleOrDefaultAsync(x => x.Id == id && x.DeletedAtUtc == null, cancellationToken)
            ?? throw new ScenarioNotFoundException("Scenario was not found.");
        if (!request.Revision.HasValue || request.Revision <= 0)
            throw Invalid("revision", "The current positive scenario revision is required for an update.");
        if (scenario.Revision != request.Revision) throw new ScenarioConflictException("Scenario has changed. Reload it before editing.");
        if (scenario.SceneId != request.SceneId) throw Invalid("sceneId", "An existing scenario cannot be moved to a different scene.");
        var version = await RequireLayoutAsync(request, cancellationToken);
        ScenarioValidator.Validate(request, Deserialize<SceneDetails>(version.LayoutJson));
        scenario.LayoutVersionId = version.Id;
        scenario.Code = request.Code.Trim();
        scenario.Name = request.Name.Trim();
        scenario.Description = request.Description;
        scenario.ConfigurationJson = Serialize(request.Inputs);
        scenario.IsSynthetic = true;
        scenario.Revision++;
        scenario.UpdatedAtUtc = DateTime.UtcNow;
        await SaveAsync(cancellationToken);
        return Details(scenario);
    }

    public async Task<ScenarioDetails> DuplicateAsync(Guid id, DuplicateScenarioRequest request, CancellationToken cancellationToken = default)
    {
        var source = await database.Scenarios.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.DeletedAtUtc == null, cancellationToken)
            ?? throw new ScenarioNotFoundException("Scenario was not found.");
        // Deserialize/copy the value document, never attach or mutate the source aggregate.
        return await CreateCoreAsync(new ScenarioWriteRequest(request.Code, request.Name, source.Description,
            source.SceneId, source.LayoutVersionId, Deserialize<ScenarioInputs>(source.ConfigurationJson)), source.DataSourceJson, cancellationToken);
    }

    public async Task<SimulationRun> CreateRunAsync(Guid id, CancellationToken cancellationToken = default, bool executionRequested = false)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var run = await CreateRunCoreAsync(id, "planned", null, cancellationToken, executionRequested);
        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return run;
    }

    // Caller owns the transaction, allowing atomic creation with an outbox command.
    internal async Task<SimulationRun> CreateRunCoreAsync(Guid id, string status, int? revision, CancellationToken cancellationToken, bool executionRequested = false)
    {
        await database.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM twin.scenarios WHERE \"Id\" = {id} FOR UPDATE", cancellationToken);
        var scenario = await database.Scenarios.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.DeletedAtUtc == null, cancellationToken)
            ?? throw new ScenarioNotFoundException("Scenario was not found.");
        if (revision.HasValue && scenario.Revision != revision)
            throw new ScenarioConflictException("Scenario has changed. Reload it before starting a simulation.");
        var stored = Details(scenario);
        var details = stored with { Inputs = FlightScheduling.Resolve(stored.Inputs) };
        var layout = await GetLayoutAsync(scenario.SceneId, scenario.LayoutVersionId, cancellationToken)
            ?? throw new ScenarioNotFoundException("Layout version was not found.");
        ScenarioValidator.Validate(new ScenarioWriteRequest(scenario.Code, scenario.Name, scenario.Description,
            scenario.SceneId, scenario.LayoutVersionId, details.Inputs), layout.Layout);
        var now = DateTime.UtcNow;
        var run = new SimulationRun
        {
            ExecutionRequested = executionRequested,
            Id = Guid.NewGuid(), SceneId = scenario.SceneId, ScenarioId = id, ScenarioRevision = scenario.Revision,
            StartedAtUtc = now, Status = status, Seed = details.Inputs.Seed, IsSynthetic = scenario.IsSynthetic,
            ParametersJson = Serialize(new
            {
                duration_minutes = details.Inputs.DurationMinutes,
                passenger_count = details.Inputs.Populations.Sum(x => (long)x.Count), seed = details.Inputs.Seed
            }),
            InputSnapshotJson = Serialize(new RunInputSnapshot(1, now, details, layout, AeroSynapse.Application.Contracts.KpiConventions.Engine, details.Inputs.SnapshotIntervalMinutes ?? 1))
        };
        if (executionRequested || status == "pending")
        {
            var request = AeroSynapse.Application.Management.CapturedSimulation.Request(run.Id, new RunInputSnapshot(1, now, details, layout, AeroSynapse.Application.Contracts.KpiConventions.Engine, details.Inputs.SnapshotIntervalMinutes ?? 1));
            run.InputHash = AeroSynapse.Application.Management.RunFingerprint.Input(request);
            run.EngineVersion = AeroSynapse.Application.Management.RunFingerprint.Engine(request);
        }
        database.Runs.Add(run);
        return run;
    }

    public Task<SimulationRun?> GetRunAsync(Guid scenarioId, Guid runId, CancellationToken cancellationToken = default) =>
        database.Runs.AsNoTracking().SingleOrDefaultAsync(x => x.ScenarioId == scenarioId && x.Id == runId, cancellationToken);

    public async Task<IReadOnlyList<SimulationRun>> RunsAsync(Guid scenarioId, int limit, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100) throw Invalid("limit", "Must be between 1 and 100.");
        return await database.Runs.AsNoTracking().Where(x => x.ScenarioId == scenarioId)
            .OrderByDescending(x => x.StartedAtUtc).ThenByDescending(x => x.Id).Take(limit).ToArrayAsync(cancellationToken);
    }

    private async Task<LayoutVersion> RequireLayoutAsync(ScenarioWriteRequest request, CancellationToken cancellationToken)
    {
        var version = await database.LayoutVersions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.LayoutVersionId, cancellationToken);
        if (version is null) throw Invalid("layoutVersionId", "A published layout version is required.");
        if (version.SceneId != request.SceneId) throw Invalid("sceneId", "Layout version belongs to a different scene.");
        return version;
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try { await database.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException)
        {
            throw new ScenarioConflictException("Scenario has changed. Reload it before editing.");
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new ScenarioConflictException("Scenario code or layout version already exists in this scene. Reload or use a different code.");
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation })
        {
            throw new ScenarioConflictException("Referenced scene, scenario or layout version has changed. Reload before retrying.");
        }
    }

    private static ScenarioValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    private static T Deserialize<T>(string value) => JsonSerializer.Deserialize<T>(value, Json)
        ?? throw new InvalidOperationException("Stored scenario document is missing.");
    private static LayoutVersionSummary Summary(LayoutVersion version) => new(version.Id, version.SceneId, version.Version, version.CreatedAtUtc);
    private static ScenarioSummary Summary(Scenario scenario) => new(scenario.Id, scenario.Code, scenario.Name, scenario.Description,
        scenario.SceneId, scenario.LayoutVersionId, scenario.Revision, scenario.CreatedAtUtc, scenario.UpdatedAtUtc, scenario.IsSynthetic, scenario.DeletedAtUtc,
        scenario.DataSourceJson is null ? null : Deserialize<DataSourceProvenance>(scenario.DataSourceJson));
    private static ScenarioDetails Details(Scenario scenario) => new(Summary(scenario), Deserialize<ScenarioInputs>(scenario.ConfigurationJson));
}

using AeroSynapse.Domain.DigitalTwin;
using System.Text.Json.Serialization;

namespace AeroSynapse.Application.Storage;

public sealed record LayoutVersionSummary(Guid Id, Guid SceneId, int Version, DateTime CreatedAtUtc);
public sealed record PublishedLayout(LayoutVersionSummary Version, SceneDetails Layout);
public sealed record ScenarioWriteRequest(string Code, string Name, string Description,
    Guid SceneId, Guid LayoutVersionId, ScenarioInputs Inputs, int? Revision = null);
public sealed record DuplicateScenarioRequest(string Code, string Name);
public sealed record ScenarioValidationResult(bool IsValid);
public sealed record ScenarioSummary(Guid Id, string Code, string Name, string Description,
    Guid SceneId, Guid LayoutVersionId, int Revision, DateTime CreatedAtUtc, DateTime UpdatedAtUtc, bool IsSynthetic, DateTime? DeletedAtUtc = null,
    DataSourceProvenance? DataSource = null);
public sealed record ScenarioDetails(ScenarioSummary Scenario, ScenarioInputs Inputs);
// Kind: created, flight, event, staffing, passengers, routes, resources, settings, details, layout or other.
// Action: added, changed or removed. Ids name the flights or events; the audit keeps no codes or texts.
public sealed record ScenarioRevisionChange(string Kind, string Action, IReadOnlyList<Guid> Ids);
public sealed record ScenarioRevisionRun(Guid Id, string Status);
public sealed record ScenarioRevision(int Revision, DateTime RecordedAtUtc, string Actor,
    IReadOnlyList<ScenarioRevisionChange> Changes, IReadOnlyList<ScenarioRevisionRun> Runs);
public sealed record RunInputSnapshot(int SchemaVersion, DateTime CapturedAtUtc,
    ScenarioDetails Scenario, PublishedLayout Layout,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Engine = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? SnapshotIntervalMinutes = null);

public interface IScenarioService
{
    Task<PublishedLayout> PublishLayoutAsync(Guid sceneId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LayoutVersionSummary>> LayoutVersionsAsync(Guid sceneId, CancellationToken cancellationToken = default);
    Task<PublishedLayout?> GetLayoutAsync(Guid sceneId, Guid versionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ScenarioSummary>> ListAsync(Guid? sceneId, CancellationToken cancellationToken = default);
    Task<ScenarioDetails?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<ScenarioDetails> CreateAsync(ScenarioWriteRequest request, CancellationToken cancellationToken = default);
    Task<ScenarioValidationResult> ValidateAsync(ScenarioWriteRequest request, CancellationToken cancellationToken = default);
    Task<ScenarioDetails> UpdateAsync(Guid id, ScenarioWriteRequest request, CancellationToken cancellationToken = default);
    Task<ScenarioDetails> DuplicateAsync(Guid id, DuplicateScenarioRequest request, CancellationToken cancellationToken = default);
    Task<SimulationRun> CreateRunAsync(Guid id, CancellationToken cancellationToken = default, bool executionRequested = false);
    Task<SimulationRun?> GetRunAsync(Guid scenarioId, Guid runId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SimulationRun>> RunsAsync(Guid scenarioId, int limit, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ScenarioRevision>> RevisionsAsync(Guid scenarioId, int limit, CancellationToken cancellationToken = default);
}

public sealed class ScenarioValidationException(IReadOnlyDictionary<string, string[]> errors)
    : Exception("Inputs are invalid.")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
}

public sealed class ScenarioNotFoundException(string message) : Exception(message);
public sealed class ScenarioConflictException(string message) : Exception(message);

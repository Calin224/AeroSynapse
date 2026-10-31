using System.Data;
using System.Text.Json;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using AeroSynapse.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AeroSynapse.Infrastructure.Storage;

public sealed class RunStorageService(TwinDbContext database, SecretRedactor? redactor = null) : IRunStorageService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<SimulationRun?> GetAsync(Guid runId, CancellationToken cancellationToken = default) =>
        database.Runs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == runId, cancellationToken);

    public async Task<SimulationRun> TransitionAsync(Guid runId, RunTransitionRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Revision <= 0) throw Invalid("revision", "The current positive run revision is required.");
        if (request.Status is not ("running" or "completed" or "failed")) throw Invalid("status", "Must be running, completed or failed.");
        if (request.Status == "failed" && (string.IsNullOrWhiteSpace(request.FailureReason) || request.FailureReason.Length > 2000))
            throw Invalid("failureReason", "A failure reason of 1 to 2000 characters is required.");
        if (request.Status != "failed" && request.FailureReason is not null) throw Invalid("failureReason", "Only failed runs can have a failure reason.");
        if (request.FailureReason?.Contains('\0') == true) throw Invalid("failureReason", "Must not contain NUL (U+0000) characters.");
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var run = await LockRunAsync(runId, cancellationToken);
        if (await database.BatchReplications.AnyAsync(x => x.RunId == runId, cancellationToken))
            throw new ScenarioConflictException("Batch replications are updated only by their batch worker.");
        if (run.Revision != request.Revision) throw new ScenarioConflictException("Run has changed. Reload it before updating status.");
        if (await database.SimulationCommands.AnyAsync(x => x.RunId == runId, cancellationToken))
            throw new ScenarioConflictException("Engine-managed runs are transitioned by the simulation worker.");
        if (!((run.Status is "planned" or "pending" && request.Status == "running") || (run.Status == "running" && request.Status is "completed" or "failed")))
            throw new ScenarioConflictException($"Cannot transition a {run.Status} run to {request.Status}.");
        if (request.Status == "running") Input(run);
        var now = DateTime.UtcNow;
        var launched = request.Status == "running" ? now : run.LaunchedAtUtc;
        DateTime? finished = request.Status is "completed" or "failed" ? now : null;
        var reason = request.Status == "failed" ? redactor?.Redact(request.FailureReason!.Trim())
            ?? SecretRedactor.RedactPatterns(request.FailureReason!.Trim()) : null;
        await database.Runs.Where(x => x.Id == runId).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.Status, request.Status).SetProperty(x => x.LaunchedAtUtc, launched)
            .SetProperty(x => x.FinishedAtUtc, finished).SetProperty(x => x.FailureReason, reason)
            .SetProperty(x => x.Revision, run.Revision + 1), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        run.Status = request.Status;
        run.LaunchedAtUtc = launched;
        run.FinishedAtUtc = finished;
        run.FailureReason = reason;
        run.Revision++;
        return run;
    }

    public async Task<RunMetric> AddMetricAsync(Guid runId, RunMetricRequest request, CancellationToken cancellationToken = default)
    {
        RunResultValidator.Metric(request);
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var run = await WritableRunAsync(runId, cancellationToken);
        RunResultValidator.Sample(Input(run), request.RecordedAtUtc, request.ZoneType, request.ZoneId);
        var metric = new RunMetric
        {
            Id = Guid.NewGuid(), SceneId = run.SceneId, RunId = runId, IsSynthetic = run.IsSynthetic,
            RecordedAtUtc = request.RecordedAtUtc, ZoneType = request.ZoneType, ZoneId = request.ZoneId,
            Name = request.Name.Trim(), Value = request.Value, Unit = request.Unit.Trim()
        };
        database.Metrics.Add(metric);
        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return metric;
    }

    public async Task<RunSnapshot> AddSnapshotAsync(Guid runId, RunSnapshotRequest request, CancellationToken cancellationToken = default)
    {
        RunResultValidator.Snapshot(request);
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var run = await WritableRunAsync(runId, cancellationToken);
        RunResultValidator.Sample(Input(run), request.RecordedAtUtc, request.ZoneType, request.ZoneId);
        var snapshot = new RunSnapshot
        {
            Id = Guid.NewGuid(), SceneId = run.SceneId, RunId = runId, IsSynthetic = run.IsSynthetic,
            RecordedAtUtc = request.RecordedAtUtc, ZoneType = request.ZoneType, ZoneId = request.ZoneId, PayloadJson = request.PayloadJson
        };
        database.Snapshots.Add(snapshot);
        await SaveAsync(cancellationToken, "payloadJson");
        await transaction.CommitAsync(cancellationToken);
        return snapshot;
    }

    public async Task<RunResults<RunMetric>> MetricsAsync(Guid runId, RunResultQuery query, CancellationToken cancellationToken = default)
    {
        RunResultValidator.Query(query);
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var run = await RequireRunAsync(runId, cancellationToken);
        var metrics = database.Metrics.AsNoTracking().Where(x => x.RunId == runId);
        if (query.FromUtc.HasValue) metrics = metrics.Where(x => x.RecordedAtUtc >= query.FromUtc.Value);
        if (query.ToUtc.HasValue) metrics = metrics.Where(x => x.RecordedAtUtc <= query.ToUtc.Value);
        if (query.ZoneId.HasValue) metrics = metrics.Where(x => x.ZoneType == query.ZoneType && x.ZoneId == query.ZoneId.Value);
        var items = run.ResultsValid || query.IncludeInvalid
            ? await metrics.OrderBy(x => x.RecordedAtUtc).ThenBy(x => x.Id).Take(query.Limit).ToArrayAsync(cancellationToken) : [];
        return new(runId, run.Status, run.ResultsValid, run.FailureReason, items);
    }

    public async Task<RunResults<RunSnapshot>> SnapshotsAsync(Guid runId, RunResultQuery query, CancellationToken cancellationToken = default)
    {
        RunResultValidator.Query(query);
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var run = await RequireRunAsync(runId, cancellationToken);
        var snapshots = database.Snapshots.AsNoTracking().Where(x => x.RunId == runId);
        if (query.FromUtc.HasValue) snapshots = snapshots.Where(x => x.RecordedAtUtc >= query.FromUtc.Value);
        if (query.ToUtc.HasValue) snapshots = snapshots.Where(x => x.RecordedAtUtc <= query.ToUtc.Value);
        if (query.ZoneId.HasValue) snapshots = snapshots.Where(x => x.ZoneType == query.ZoneType && x.ZoneId == query.ZoneId.Value);
        var items = run.ResultsValid || query.IncludeInvalid
            ? await snapshots.OrderBy(x => x.RecordedAtUtc).ThenBy(x => x.Id).Take(query.Limit).ToArrayAsync(cancellationToken) : [];
        return new(runId, run.Status, run.ResultsValid, run.FailureReason, items);
    }

    public async Task<RecommendationDetails> RecommendAsync(RecommendationRequest request, CancellationToken cancellationToken = default)
    {
        RunResultValidator.Recommendation(request);
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        // Use a stable lock order shared with retention, so a purge cannot remove compared runs.
        var runs = new Dictionary<Guid, SimulationRun>();
        foreach (var id in request.Candidates.Select(x => x.RunId).Append(request.BaselineRunId).Order())
            runs[id] = await LockRunAsync(id, cancellationToken);
        var baseline = runs[request.BaselineRunId];
        if (!baseline.ResultsValid) throw Invalid("baselineRunId", "Baseline must be completed successfully.");
        var baselineInput = Input(baseline);
        foreach (var (candidate, index) in request.Candidates.Select((value, index) => (value, index)))
        {
            var run = runs[candidate.RunId];
            if (!run.ResultsValid) throw Invalid($"candidates[{index}].runId", "Candidate must be completed successfully.");
            var input = Input(run);
            if (run.SceneId != baseline.SceneId || input.Layout.Version.Id != baselineInput.Layout.Version.Id
                || input.Scenario.Inputs.StartsAtUtc != baselineInput.Scenario.Inputs.StartsAtUtc
                || input.Scenario.Inputs.DurationMinutes != baselineInput.Scenario.Inputs.DurationMinutes)
                throw Invalid($"candidates[{index}].runId", "Compared runs must use the same scene, layout version and simulation time window.");
        }
        var recommendation = new RunRecommendation
        {
            Id = Guid.NewGuid(), SceneId = baseline.SceneId, BaselineRunId = baseline.Id, CreatedAtUtc = DateTime.UtcNow,
            Objective = request.Objective.Trim(), BaselineScore = request.BaselineScore, Rationale = request.Rationale.Trim(),
            IsSynthetic = runs.Values.All(x => x.IsSynthetic)
        };
        var candidates = request.Candidates.Select(x => new RecommendationCandidate
        {
            Id = Guid.NewGuid(), SceneId = baseline.SceneId, RecommendationId = recommendation.Id, RunId = x.RunId,
            Rank = x.Rank, Score = x.Score, AssessmentJson = x.AssessmentJson, IsSynthetic = runs[x.RunId].IsSynthetic
        }).ToArray();
        database.Recommendations.Add(recommendation);
        database.RecommendationCandidates.AddRange(candidates);
        await SaveAsync(cancellationToken, "candidates");
        await transaction.CommitAsync(cancellationToken);
        return new(recommendation, candidates.OrderBy(x => x.Rank).ToArray());
    }

    public async Task<RecommendationDetails?> RecommendationAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var recommendation = await database.Recommendations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return recommendation is null ? null : new(recommendation, await database.RecommendationCandidates.AsNoTracking()
            .Where(x => x.RecommendationId == id).OrderBy(x => x.Rank).ToArrayAsync(cancellationToken));
    }

    public async Task<IReadOnlyList<RecommendationDetails>> RecommendationsAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await RequireRunAsync(runId, cancellationToken);
        var recommendations = await database.Recommendations.AsNoTracking()
            .Where(x => x.BaselineRunId == runId || database.RecommendationCandidates.Any(c => c.RecommendationId == x.Id && c.RunId == runId))
            .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id).Take(100).ToArrayAsync(cancellationToken);
        var ids = recommendations.Select(x => x.Id).ToArray();
        var candidates = await database.RecommendationCandidates.AsNoTracking().Where(x => ids.Contains(x.RecommendationId))
            .OrderBy(x => x.Rank).ToArrayAsync(cancellationToken);
        return recommendations.Select(x => new RecommendationDetails(x, candidates.Where(c => c.RecommendationId == x.Id).ToArray())).ToArray();
    }

    public async Task<ScenarioDeletionResult> DeleteScenarioAsync(Guid id, string retention, int revision, CancellationToken cancellationToken = default)
    {
        if (retention is not ("archive" or "restrict" or "purge")) throw Invalid("retention", "Must be archive, restrict or purge.");
        if (revision <= 0) throw Invalid("revision", "The current positive scenario revision is required.");
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await database.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM twin.scenarios WHERE \"Id\" = {id} FOR UPDATE", cancellationToken);
        var scenario = await database.Scenarios.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new ScenarioNotFoundException("Scenario was not found.");
        if (scenario.Revision != revision) throw new ScenarioConflictException("Scenario has changed. Reload before deleting.");
        if (retention == "archive")
        {
            if (scenario.DeletedAtUtc is not null) return new(id, retention, true, scenario.Revision, 0);
            var now = DateTime.UtcNow;
            await database.Scenarios.Where(x => x.Id == id).ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.DeletedAtUtc, now).SetProperty(x => x.UpdatedAtUtc, now)
                .SetProperty(x => x.Revision, scenario.Revision + 1), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(id, retention, true, scenario.Revision + 1, 0);
        }
        if (await database.NotificationCommands.AnyAsync(x => x.ScenarioId == id, cancellationToken))
            throw new ScenarioConflictException("Operational notification audit retains this scenario. Use archive to preserve coordination history.");
        var runIds = await database.Runs.AsNoTracking().Where(x => x.ScenarioId == id).Select(x => x.Id).ToArrayAsync(cancellationToken);
        if (await database.BatchVariants.AnyAsync(x => x.ScenarioId == id, cancellationToken))
            throw new ScenarioConflictException("Scenario inputs and runs are retained by a batch. Archive the scenario to preserve its seed matrix.");
        if (retention == "restrict" && runIds.Length > 0) throw new ScenarioConflictException("Scenario has retained runs. Use archive to preserve history.");
        foreach (var runId in runIds.Order())
            if ((await LockRunAsync(runId, cancellationToken)).Status is "running" or "pending")
                throw new ScenarioConflictException("A running execution prevents physical deletion. Archive the scenario or wait for it to finish.");
        if (await database.CopilotWorkflows.AnyAsync(w => w.ScenarioId == id && (w.Status == "pending" || w.Status == "running" || w.CancellationPending), cancellationToken))
            throw new ScenarioConflictException("Stop active Copilot workflows and finish cancellation before physically deleting their source run. Archive preserves history.");
        if (await database.Recommendations.AnyAsync(x => runIds.Contains(x.BaselineRunId), cancellationToken)
            || await database.RecommendationCandidates.AnyAsync(x => runIds.Contains(x.RunId), cancellationToken))
            throw new ScenarioConflictException("Compared runs are retained by recommendations. Archive the scenario to preserve comparison provenance.");
        await database.Events.Where(x => x.SimulationRunId.HasValue && runIds.Contains(x.SimulationRunId.Value)).ExecuteDeleteAsync(cancellationToken);
        await database.Metrics.Where(x => runIds.Contains(x.RunId)).ExecuteDeleteAsync(cancellationToken);
        await database.Snapshots.Where(x => runIds.Contains(x.RunId)).ExecuteDeleteAsync(cancellationToken);
        await database.Runs.Where(x => x.ScenarioId == id).ExecuteDeleteAsync(cancellationToken);
        await database.Scenarios.Where(x => x.Id == id).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(id, retention, false, null, runIds.Length);
    }

    private async Task<SimulationRun> WritableRunAsync(Guid runId, CancellationToken token)
    {
        var run = await LockRunAsync(runId, token);
        if (await database.BatchReplications.AnyAsync(x => x.RunId == runId, token))
            throw new ScenarioConflictException("Batch results are persisted only by their batch worker.");
        if (run.Status != "running") throw new ScenarioConflictException("Results can be appended only while a run is running.");
        if (await database.SimulationCommands.AnyAsync(x => x.RunId == runId, token))
            throw new ScenarioConflictException("Engine-managed results are persisted atomically by the simulation worker.");
        return run;
    }
    private async Task<SimulationRun> LockRunAsync(Guid runId, CancellationToken token)
    {
        await database.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM twin.runs WHERE \"Id\" = {runId} FOR UPDATE", token);
        return await RequireRunAsync(runId, token);
    }
    private async Task<SimulationRun> RequireRunAsync(Guid runId, CancellationToken token) =>
        await GetAsync(runId, token) ?? throw new ScenarioNotFoundException("Run was not found.");
    private static RunInputSnapshot Input(SimulationRun run) => run.InputSnapshotJson is null
        ? throw Invalid("inputSnapshotJson", "Run must have a captured input snapshot for launch, result ingestion or comparison.")
        : JsonSerializer.Deserialize<RunInputSnapshot>(run.InputSnapshotJson, Json)!;
    private static ScenarioValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });
    private async Task SaveAsync(CancellationToken token, string? jsonField = null)
    {
        try { await database.SaveChangesAsync(token); }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new ScenarioConflictException("A sample for this run, zone, timestamp and metric name, or a compared candidate already exists.");
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation })
        {
            throw new ScenarioConflictException("Referenced run or recommendation has changed. Reload before retrying.");
        }
        catch (DbUpdateException exception) when (jsonField is not null && exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.NumericValueOutOfRange })
        {
            throw Invalid(jsonField, "JSON numbers exceed the supported storage range.");
        }
    }
}

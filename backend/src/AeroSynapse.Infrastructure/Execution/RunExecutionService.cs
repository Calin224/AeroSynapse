using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using AeroSynapse.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
namespace AeroSynapse.Infrastructure.Execution;

public sealed class RunExecutionService(TwinDbContext db, IScenarioService scenarios, IRunStorageService storage,
    ISimulationEngine engine, ILogger<RunExecutionService>? log = null, AeroSynapse.Infrastructure.Security.SecretRedactor? redactor = null,
    AgentLoop? agent = null) : IRunExecutionService
{
    private readonly ILogger<RunExecutionService> logger = log ?? NullLogger<RunExecutionService>.Instance;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public Task<SimulationRun> StartAsync(Guid scenarioId, CancellationToken token) => scenarios.CreateRunAsync(scenarioId, token, executionRequested: true);
    public async Task<SimulationRun> RetryAsync(Guid runId, int revision, CancellationToken token)
    {
        if (revision <= 0) throw Invalid("revision", "The current positive run revision is required.");
        var source = await storage.GetAsync(runId, token) ?? throw new ScenarioNotFoundException("Run was not found.");
        if (await db.BatchReplications.AnyAsync(x => x.RunId == runId, token))
            throw new ScenarioConflictException("Batch replications are retained unchanged. Replay the batch to reproduce the seed matrix.");
        if (source.ScenarioId is not { } scenarioId || source.InputSnapshotJson is null)
            throw new ScenarioConflictException("Legacy runs without captured inputs cannot be retried.");
        // Lock ordering is shared with scenario retention.
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM twin.scenarios WHERE \"Id\" = {scenarioId} FOR UPDATE", token);
        var scenario = await db.Scenarios.AsNoTracking().SingleOrDefaultAsync(x => x.Id == scenarioId, token)
            ?? throw new ScenarioNotFoundException("Scenario was not found.");
        if (scenario.DeletedAtUtc is not null) throw new ScenarioConflictException("Archived scenarios cannot start new runs.");
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM twin.runs WHERE \"Id\" = {runId} FOR UPDATE", token);
        source = await storage.GetAsync(runId, token) ?? throw new ScenarioNotFoundException("Run was not found.");
        if (source.Revision != revision) throw new ScenarioConflictException("Run has changed. Reload it before retrying.");
        if (source.Status != "failed") throw new ScenarioConflictException("Only failed runs can be retried.");
        if (await db.Runs.AnyAsync(x => x.RetryOfRunId == runId, token))
            throw new ScenarioConflictException("This failure already has a retry. Consult or retry the new run.");
        var retry = new SimulationRun
        {
            Id = Guid.NewGuid(), SceneId = source.SceneId, ScenarioId = source.ScenarioId, ScenarioRevision = source.ScenarioRevision,
            InputSnapshotJson = source.InputSnapshotJson, Seed = source.Seed, ParametersJson = source.ParametersJson,
            IsSynthetic = source.IsSynthetic, StartedAtUtc = DateTime.UtcNow, ExecutionRequested = true, RetryOfRunId = runId
        };
        var request = CapturedSimulation.Request(retry.Id, Input(retry));
        retry.InputHash = RunFingerprint.Input(request);
        retry.EngineVersion = RunFingerprint.Engine(request);
        db.Runs.Add(retry); await db.SaveChangesAsync(token); await transaction.CommitAsync(token);
        return retry;
    }
    public async Task<RunDetails> GetAsync(Guid runId, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        var run = await storage.GetAsync(runId, token) ?? throw new ScenarioNotFoundException("Run was not found.");
        var metrics = run.ResultsValid ? await db.Metrics.AsNoTracking().Where(x => x.RunId == runId)
            .OrderBy(x => x.RecordedAtUtc).ThenBy(x => x.Id).ToArrayAsync(token) : [];
        var snapshots = run.ResultsValid ? await db.Snapshots.AsNoTracking().Where(x => x.RunId == runId)
            .OrderBy(x => x.RecordedAtUtc).ThenBy(x => x.Id).ToArrayAsync(token) : [];
        RunFailure? error = null;
        if (run.Status == "failed")
        {
            var reason = run.FailureReason ?? "Run failed.";
            var separator = reason.IndexOf(": ", StringComparison.Ordinal);
            error = separator > 0 && Regex.IsMatch(reason[..separator], "^[a-z][a-z0-9_]{0,63}$")
                ? new(reason[..separator], reason[(separator + 2)..]) : new("execution_failed", reason);
        }
        var correlationId = await db.SimulationCommands.Where(x => x.RunId == runId).Select(x => x.CorrelationId).SingleOrDefaultAsync(token);
        return new(run.Id, run.SceneId, run.ScenarioId, run.ScenarioRevision, run.Status, run.Revision, run.Seed,
            run.StartedAtUtc, run.LaunchedAtUtc, run.FinishedAtUtc, run.ResultsValid, run.FailureReason,
            run.InputSnapshotJson, run.ParametersJson, run.IsSynthetic, run.ExecutionRequested, run.RetryOfRunId, metrics, snapshots, error,
            RunComparisonService.Provenance(run, correlationId));
    }
    public async Task<bool> DispatchNextAsync(CancellationToken token)
    {
        var ids = await db.Runs.AsNoTracking().Where(x => x.ExecutionRequested && (x.Status == "planned" || x.Status == "running"))
            .OrderBy(x => x.StartedAtUtc).ThenBy(x => x.Id).Select(x => x.Id).Take(32).ToArrayAsync(token);
        foreach (var id in ids)
        {
            // A PostgreSQL session lease survives status commits and covers the HTTP computation.
            await using var lease = new NpgsqlConnection(db.Database.GetConnectionString());
            await lease.OpenAsync(token);
            await using var acquire = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtextextended(@id, 10))", lease);
            acquire.Parameters.AddWithValue("id", id.ToString());
            if (await acquire.ExecuteScalarAsync(token) is not true) continue;
            try { await Execute(id, token); }
            finally
            {
                await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtextextended(@id, 10))", lease);
                release.Parameters.AddWithValue("id", id.ToString());
                await release.ExecuteScalarAsync(CancellationToken.None);
            }
            return true;
        }
        return false;
    }
    public async Task ExecuteBatchRunAsync(Guid runId, AeroSynapse.Application.Simulation.ExecutionControl control, CancellationToken token)
    {
        if (!await db.BatchReplications.AnyAsync(x => x.RunId == runId && x.Status == "running", token))
            throw new ScenarioConflictException("The batch worker must claim this replication before execution.");
        await Execute(runId, token, control);
    }
    private async Task Execute(Guid id, CancellationToken token, AeroSynapse.Application.Simulation.ExecutionControl? control = null)
    {
        var run = await storage.GetAsync(id, token);
        if (run is null || (!run.ExecutionRequested && control is null) || run.Status is not ("planned" or "running")) return;
        if (run.Status == "planned")
        {
            if (control is null) run = await storage.TransitionAsync(id, new("running", run.Revision), token);
            else
            {
                await db.Runs.Where(x => x.Id == id && x.Status == "planned").ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, "running").SetProperty(x => x.LaunchedAtUtc, DateTime.UtcNow).SetProperty(x => x.Revision, run.Revision + 1), token);
                run = (await storage.GetAsync(id, token))!;
            }
        }
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["RunId"] = id, ["CorrelationId"] = id.ToString("N"), ["InputHash"] = run.InputHash ?? "legacy" });
        logger.LogInformation("Execution running run={RunId} input_hash={InputHash} engine={EngineVersion} correlationId={CorrelationId}", id, run.InputHash, run.EngineVersion, id.ToString("N"));
        AeroSynapse.Application.Simulation.EngineDiagnostics? diagnostics = null;
        try
        {
            var request = CapturedSimulation.Request(id, Input(run));
            if (agent is not null) request = await agent.PrepareAsync(run, Input(run), request, token);
            var response = control is null ? await engine.ExecuteAsync(request, token) : await engine.ExecuteControlledAsync(request, control, token);
            var resultHash = response.Status == "completed" ? RunFingerprint.Result(response) : null;
            var responseContract = response.JourneyResult is not null ? "JourneySimulationResponse" : response.KpiResult is not null ? "KpiSimulationResponse" : response.ProcessResult is not null ? "SimPySimulationResponse"
                : response.NavigationResult is null ? "SimulationResponse" : "RoutedSimulationResponse";
            var schemaErrors = VersionedContract.Validate(responseContract, JsonSerializer.SerializeToElement(response, ContractJson.Options));
            if (schemaErrors.Count != 0 || ContractCorrelation.Validate(request, response).Count != 0 || !RunDiagnostics.Valid(engine.Diagnostics, request, response, resultHash)
                || (control is not null && response.Status == "completed" && engine.Diagnostics is null)
                || response.Metrics.Select(x => (x.ZoneId, x.RecordedAtUtc)).Distinct().Count() != response.Metrics.Count
                || response.Snapshots.Select(x => (x.ZoneId, x.ElapsedMinutes)).Distinct().Count() != response.Snapshots.Count)
                throw new SimulationEngineException("engine_contract_invalid", "Simulation response does not match the captured request.");
            diagnostics = engine.Diagnostics;
            if (response.Status == "failed") throw new SimulationEngineException(response.Failure!.Code, response.Failure.Message);
            await Complete(run, request, response, resultHash!, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogError(exception, "Execution failed run={RunId} correlationId={CorrelationId}", id, id.ToString("N"));
            var failure = exception is SimulationEngineException known ? known : exception is ContractValidationException or ScenarioValidationException or JsonException
                ? new SimulationEngineException("execution_invalid", "Captured inputs or engine output are invalid.")
                : new SimulationEngineException("execution_unexpected", $"Unexpected execution failure. Inspect service logs with correlation ID {id:N}.");
            var reason = (redactor?.Redact($"{failure.Code}: {failure.Message}") ?? AeroSynapse.Infrastructure.Security.SecretRedactor.RedactPatterns($"{failure.Code}: {failure.Message}")).Replace("\0", "");
            var length = Math.Min(reason.Length, 2000);
            if (length > 0 && char.IsHighSurrogate(reason[length - 1])) length--;
            db.ChangeTracker.Clear();
            var changed = await db.Runs.Where(x => x.Id == id && x.Status == "running" && x.Revision == run.Revision)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, "failed")
                    .SetProperty(x => x.FinishedAtUtc, DateTime.UtcNow).SetProperty(x => x.FailureReason, reason[..length])
                    .SetProperty(x => x.EngineBuild, diagnostics == null ? null : diagnostics.EngineBuild)
                    .SetProperty(x => x.EngineMilliseconds, diagnostics == null ? null : (double?)diagnostics.EngineMilliseconds)
                    .SetProperty(x => x.Revision, run.Revision + 1), token);
            if (changed != 1) throw new ScenarioConflictException("Run changed during failure handling.");
        }
    }
    private async Task Complete(SimulationRun run, SimulationRequest request, SimulationResponse response, string resultHash, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var batchId = await db.BatchReplications.Where(x => x.RunId == run.Id).Select(x => (Guid?)x.BatchId).SingleOrDefaultAsync(token);
        if (batchId.HasValue)
        {
            var batch = await db.Batches.FromSqlInterpolated($"SELECT * FROM twin.simulation_batches WHERE \"Id\" = {batchId.Value} FOR UPDATE").SingleAsync(token);
            if (batch.CancellationRequestedAtUtc.HasValue || BatchService.Terminal(batch.Status))
                throw new SimulationEngineException("batch_cancelled", "The batch was stopped before this result could be committed.");
            if (DateTime.UtcNow >= batch.DeadlineAtUtc) throw new SimulationEngineException("batch_timeout", "The batch deadline expired before this result could be committed.");
            if (batch.EngineBuild is not null && batch.EngineBuild != engine.Diagnostics?.EngineBuild)
                throw new SimulationEngineException("batch_engine_changed", "The deployed engine build changed during the batch. Replay the batch on one build.");
            if (batch.EngineBuild is null) { batch.EngineBuild = engine.Diagnostics!.EngineBuild; batch.Revision++; await db.SaveChangesAsync(token); }
        }
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM twin.runs WHERE \"Id\" = {run.Id} FOR UPDATE", token);
        var current = await storage.GetAsync(run.Id, token) ?? throw new ScenarioNotFoundException("Run was not found.");
        if (current.Status != "running" || current.Revision != run.Revision) throw new ScenarioConflictException("Run changed during execution.");
        // A journey run carries the security simulation it built on; its queues are the run's checkpoint series.
        var security = response.JourneyResult?.UpstreamResult ?? response;
        if (security.KpiResult is not null) KpiResultStorage.Add(db, run, request, security.KpiResult);
        if (security.CheckInResult is not null) KpiResultStorage.AddCheckIn(db, run, request, security.CheckInResult);
        foreach (var metric in security.KpiResult is null ? security.Metrics : [])
        {
            AddMetric(metric.ZoneId, metric.RecordedAtUtc.UtcDateTime, "meanWaitMinutes", metric.MeanWaitMinutes, "minutes");
            AddMetric(metric.ZoneId, metric.RecordedAtUtc.UtcDateTime, "throughputPerMinute", metric.ThroughputPerMinute, "passengers/minute");
            AddMetric(metric.ZoneId, metric.RecordedAtUtc.UtcDateTime, "processedPassengers", metric.ProcessedPassengers, "passengers");
        }
        foreach (var snapshot in security.KpiResult is null ? security.Snapshots : [])
            db.Snapshots.Add(new RunSnapshot
            {
                Id = Guid.NewGuid(), SceneId = run.SceneId, RunId = run.Id, IsSynthetic = run.IsSynthetic,
                ZoneType = "checkpoint", ZoneId = snapshot.ZoneId, RecordedAtUtc = request.StartsAtUtc.AddMinutes(snapshot.ElapsedMinutes).UtcDateTime,
                PayloadJson = JsonSerializer.Serialize(snapshot, ContractJson.Options)
            });
        NavigationResultStorage.Add(db,run,request,response);
        ProcessResultStorage.Add(db,run,request,response);
        JourneyResultStorage.Add(db,run,request,response);
        await db.SaveChangesAsync(token);
        await db.Runs.Where(x => x.Id == run.Id).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Status, "completed")
            .SetProperty(x => x.ResultHash, resultHash)
            .SetProperty(x => x.EngineBuild, engine.Diagnostics == null ? null : engine.Diagnostics.EngineBuild)
            .SetProperty(x => x.EngineMilliseconds, engine.Diagnostics == null ? null : (double?)engine.Diagnostics.EngineMilliseconds)
            .SetProperty(x => x.FinishedAtUtc, DateTime.UtcNow).SetProperty(x => x.Revision, run.Revision + 1), token);
        await transaction.CommitAsync(token);
        logger.LogInformation("Execution completed run={RunId} result_hash={ResultHash} engine_ms={EngineMilliseconds} correlationId={CorrelationId}", run.Id, resultHash, engine.Diagnostics?.EngineMilliseconds, run.Id.ToString("N"));
        void AddMetric(Guid zoneId, DateTime time, string name, double value, string unit) => db.Metrics.Add(new RunMetric
        {
            Id = Guid.NewGuid(), SceneId = run.SceneId, RunId = run.Id, IsSynthetic = run.IsSynthetic,
            ZoneType = "checkpoint", ZoneId = zoneId, RecordedAtUtc = time, Name = name, Value = decimal.Round((decimal)value, 6), Unit = unit
        });
    }
    private static RunInputSnapshot Input(SimulationRun run) => JsonSerializer.Deserialize<RunInputSnapshot>(run.InputSnapshotJson!, Json)!;
    private static ScenarioValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });
}

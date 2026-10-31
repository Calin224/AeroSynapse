using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Simulation;
using AeroSynapse.Application.Storage;
using AeroSynapse.Application.Operations;
using AeroSynapse.Infrastructure.Operations;
using AeroSynapse.Domain.DigitalTwin;
using AeroSynapse.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AeroSynapse.Infrastructure.Simulation;

public sealed class SimulationProcessor(TwinDbContext database, IIntelligenceClient client,
    IOptions<SimulationOptions> options, ILogger<SimulationProcessor> logger, IRunProgressRecorder? progressRecorder = null,
    IOptions<OperationsOptions>? operationsOptions = null, AeroSynapse.Infrastructure.Security.SecretRedactor? redactor = null,
    AeroSynapse.Infrastructure.Execution.AgentLoop? agent = null)
{
    private readonly IRunProgressRecorder progress = progressRecorder ?? new RunProgressRecorder(database);
    public async Task<bool> ProcessNextAsync(CancellationToken token)
    {
        database.ChangeTracker.Clear();
        SimulationCommand? command;
        bool dispatch;
        await using (var transaction = await database.Database.BeginTransactionAsync(token))
        {
            var now = DateTime.UtcNow;
            command = await database.SimulationCommands.FromSqlInterpolated($"""
                SELECT * FROM twin.simulation_commands WHERE "FinishedAtUtc" IS NULL AND "NextAttemptAtUtc" <= {now}
                ORDER BY "NextAttemptAtUtc", "Id" LIMIT 1 FOR UPDATE SKIP LOCKED
                """).SingleOrDefaultAsync(token);
            if (command is null) return false;
            dispatch = command.DispatchedAtUtc is null;
            command.NextAttemptAtUtc = now.AddSeconds(options.Value.TimeoutSeconds * (options.Value.SafeRetryCount + 1) + options.Value.PollSeconds + 2);
            if (dispatch)
            {
                var run = await LockRunAsync(command.RunId, token);
                run.Status = "running";
                run.LaunchedAtUtc = now;
                run.Revision++;
                // Commit BEFORE sending. Recovery only reconciles; it never resends this POST.
                command.DispatchedAtUtc = now;
            }
            await database.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        }
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = command.CorrelationId, ["RunId"] = command.RunId });
        logger.LogInformation("Simulation {Action} run={RunId} correlationId={CorrelationId}", dispatch ? "dispatch" : "reconcile", command.RunId, command.CorrelationId);
        var request = JsonSerializer.Deserialize<SimulationRequest>(command.RequestJson, ContractJson.Options)!;
        try
        {
            if (agent is not null && await database.Runs.AsNoTracking().SingleAsync(x => x.Id == command.RunId, token) is { InputSnapshotJson: { } json } agentRun
                && JsonSerializer.Deserialize<RunInputSnapshot>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)) is { Scenario.Inputs.Agent: not null } captured)
            {
                // The agent decides before the run is sent; recorded decisions are reused when reconciling.
                request = await agent.PrepareAsync(agentRun, captured, request, token);
                await database.SimulationCommands.Where(x => x.Id == command.Id && x.FinishedAtUtc == null).ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.NextAttemptAtUtc, DateTime.UtcNow.AddSeconds(options.Value.TimeoutSeconds * (options.Value.SafeRetryCount + 1) + options.Value.PollSeconds + 2)), token);
            }
            var result = dispatch ? await EngineProgressSession.StartAsync(client, request, command.CorrelationId, progress,
                    operationsOptions?.Value.EnginePollMilliseconds ?? 1000, token)
                : await client.GetAsync(command.RunId, command.CorrelationId, token);
            await EngineProgressSession.RecordAsync(progress, result, token);
            if (result?.Result is not null)
            {
                var errors = ContractCorrelation.Validate(request, result.Result);
                if (errors.Count != 0) throw new IntelligenceException("engine_invalid_response", "Engine result does not match the captured request.");
                if (result.Result.Metrics.GroupBy(x => (x.ZoneId, x.RecordedAtUtc)).Any(x => x.Count() > 1)
                    || result.Result.Snapshots.GroupBy(x => (x.ZoneId, x.ElapsedMinutes)).Any(x => x.Count() > 1)
                    || result.Result.Metrics.Any(x => x.MeanWaitMinutes >= 1_000_000_000_000 || x.ThroughputPerMinute >= 1_000_000_000_000))
                    throw new IntelligenceException("engine_invalid_response", "Engine returned duplicate samples or values outside storage precision.");
                if (result.Result.Failure?.Message.Contains('\0') == true)
                    throw new IntelligenceException("engine_invalid_response", "Engine failure text contains an invalid character.");
                var resultHash = result.Status == "completed" ? AeroSynapse.Application.Management.RunFingerprint.Result(result.Result) : null;
                if (!AeroSynapse.Application.Management.RunDiagnostics.Valid(result.Diagnostics, request, result.Result, resultHash))
                    throw new IntelligenceException("engine_invalid_diagnostics", "Engine fingerprints or timing do not match the captured request and result.");
                await FinishAsync(command.Id, result.Result, null, token, result.Diagnostics, resultHash);
                return true;
            }
            await ReconcileAsync(command, "engine_timeout", "Engine result was not confirmed before the reconciliation deadline.", token);
        }
        catch (IntelligenceException error)
        {
            logger.LogWarning("Simulation transport error code={Code} run={RunId} correlationId={CorrelationId}", error.Code, command.RunId, command.CorrelationId);
            if (error.Transient) await ReconcileAsync(command, error.Code, error.Message, token);
            else await FinishAsync(command.Id, null, new(error.Code, error.Message), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            logger.LogError(error, "Unexpected simulation failure run={RunId} correlationId={CorrelationId}", command.RunId, command.CorrelationId);
            await FinishAsync(command.Id, null, new("execution_unexpected", $"Unexpected execution failure. Inspect service logs with correlation ID {command.CorrelationId}."), token);
        }
        return true;
    }

    private async Task ReconcileAsync(SimulationCommand command, string code, string message, CancellationToken token)
    {
        if (DateTime.UtcNow >= command.DispatchedAtUtc!.Value.AddSeconds(options.Value.ReconciliationSeconds))
        {
            await FinishAsync(command.Id, null, new(code, message), token);
            return;
        }
        await database.SimulationCommands.Where(x => x.Id == command.Id && x.FinishedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.NextAttemptAtUtc, DateTime.UtcNow.AddSeconds(options.Value.PollSeconds))
                .SetProperty(x => x.ErrorCode, code), token);
    }

    private async Task FinishAsync(Guid commandId, SimulationResponse? result, RunFailure? failure, CancellationToken token, EngineDiagnostics? diagnostics = null, string? resultHash = null)
    {
        database.ChangeTracker.Clear();
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        var command = await database.SimulationCommands.FromSqlInterpolated($"SELECT * FROM twin.simulation_commands WHERE \"Id\" = {commandId} FOR UPDATE").SingleAsync(token);
        if (command.FinishedAtUtc is not null) return;
        var run = await LockRunAsync(command.RunId, token);
        if (run.Status != "running") throw new InvalidOperationException("Engine-managed run must be running before completion.");
        failure ??= result?.Failure;
        if (failure is null && result is { Status: "completed" })
        {
            // A journey run carries the security simulation it built on; its queues are the run's checkpoint series.
            var security = result.JourneyResult?.UpstreamResult ?? result;
            foreach (var metric in security.KpiResult is null ? security.Metrics : [])
            {
                AddMetric(run, metric, "mean_wait_minutes", (decimal)metric.MeanWaitMinutes, "minutes");
                AddMetric(run, metric, "throughput_per_minute", (decimal)metric.ThroughputPerMinute, "people/minute");
                AddMetric(run, metric, "processed_passengers", metric.ProcessedPassengers, "people");
            }
            var request = JsonSerializer.Deserialize<SimulationRequest>(command.RequestJson, ContractJson.Options)!;
            if (security.KpiResult is not null) AeroSynapse.Infrastructure.Execution.KpiResultStorage.Add(database, run, request, security.KpiResult);
            if (security.CheckInResult is not null) AeroSynapse.Infrastructure.Execution.KpiResultStorage.AddCheckIn(database, run, request, security.CheckInResult);
            foreach (var snapshot in security.KpiResult is null ? security.Snapshots : [])
                database.Snapshots.Add(new RunSnapshot
                {
                    Id = Guid.NewGuid(), RunId = run.Id, SceneId = run.SceneId, IsSynthetic = run.IsSynthetic,
                    ZoneType = "checkpoint", ZoneId = snapshot.ZoneId,
                    RecordedAtUtc = request.StartsAtUtc.AddMinutes(snapshot.ElapsedMinutes).UtcDateTime,
                    PayloadJson = JsonSerializer.Serialize(snapshot, ContractJson.Options)
                });
            AeroSynapse.Infrastructure.Execution.NavigationResultStorage.Add(database,run,request,result);
            // The journey report owns the run's scene snapshot, so its security passengers are not stored twice.
            AeroSynapse.Infrastructure.Execution.ProcessResultStorage.Add(database,run,request,result);
            AeroSynapse.Infrastructure.Execution.JourneyResultStorage.Add(database,run,request,result);
            // Result triggers require running. Persist samples before updating the status, inside the same transaction.
            await database.SaveChangesAsync(token);
        }
        else failure ??= new("engine_invalid_response", "Engine did not return a completed result.");
        run.Status = failure is null ? "completed" : "failed";
        run.ResultHash = failure is null && result is not null ? resultHash ?? AeroSynapse.Application.Management.RunFingerprint.Result(result) : null;
        run.EngineBuild = diagnostics?.EngineBuild;
        run.EngineMilliseconds = diagnostics?.EngineMilliseconds;
        run.FinishedAtUtc = DateTime.UtcNow;
        var reason = failure is null ? null : redactor?.Redact($"{failure.Code}: {failure.Message}") ?? AeroSynapse.Infrastructure.Security.SecretRedactor.RedactPatterns($"{failure.Code}: {failure.Message}");
        var length = reason is null ? 0 : Math.Min(2000, reason.Length);
        if (reason is not null && length < reason.Length && char.IsHighSurrogate(reason[length - 1])) length--;
        run.FailureReason = reason is null ? null : reason[..length];
        run.Revision++;
        command.FinishedAtUtc = run.FinishedAtUtc;
        command.ErrorCode = failure?.Code;
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        logger.LogInformation("Simulation {Status} run={RunId} input_hash={InputHash} result_hash={ResultHash} engine_ms={EngineMilliseconds} correlationId={CorrelationId}", run.Status, run.Id, run.InputHash, run.ResultHash, run.EngineMilliseconds, command.CorrelationId);
    }

    private void AddMetric(SimulationRun run, SimulationMetric sample, string name, decimal value, string unit) =>
        database.Metrics.Add(new RunMetric
        {
            Id = Guid.NewGuid(), RunId = run.Id, SceneId = run.SceneId, IsSynthetic = run.IsSynthetic,
            ZoneType = "checkpoint", ZoneId = sample.ZoneId, RecordedAtUtc = sample.RecordedAtUtc.UtcDateTime,
            Name = name, Value = decimal.Round(value, 6, MidpointRounding.ToEven), Unit = unit
        });

    private Task<SimulationRun> LockRunAsync(Guid runId, CancellationToken token) =>
        database.Runs.FromSqlInterpolated($"SELECT * FROM twin.runs WHERE \"Id\" = {runId} FOR UPDATE").SingleAsync(token);
}

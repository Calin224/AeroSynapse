using System.Text.Json;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using AeroSynapse.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using AeroSynapse.Infrastructure.Audit;
using AeroSynapse.Infrastructure.Governance;
using static AeroSynapse.Infrastructure.Execution.CopilotWorkflowService;
using WorkflowJson = AeroSynapse.Infrastructure.Execution.CopilotWorkflowService;

namespace AeroSynapse.Infrastructure.Execution;

// One durable step per lease. External calls use another scope so cancellation remains observable.
public sealed class CopilotWorkflowProcessor(TwinDbContext db, IServiceScopeFactory scopes, TimeProvider clock, ILogger<CopilotWorkflowProcessor> logger)
{
    public async Task<bool> ProcessNextAsync(CancellationToken token)
    {
        db.ChangeTracker.Clear(); var now = Now(clock);
        var ids = await db.CopilotWorkflows.AsNoTracking().Where(w => (w.Status == "pending" || w.Status == "running" || w.CancellationPending) && w.NextAttemptAtUtc <= now)
            .OrderBy(w => w.NextAttemptAtUtc).ThenBy(w => w.CreatedAtUtc).Select(w => w.Id).Take(32).ToArrayAsync(token);
        foreach (var id in ids)
        {
            await using var lease = new NpgsqlConnection(db.Database.GetConnectionString()); await lease.OpenAsync(token);
            await using var acquire = new NpgsqlCommand("SELECT pg_try_advisory_lock(hashtextextended(@id, 43002))", lease);
            acquire.Parameters.AddWithValue("id", id.ToString()); if (await acquire.ExecuteScalarAsync(token) is not true) continue;
            try
            {
                using var audit = AuditContext.Begin(new("system", "system", $"workflow:{id:N}"));
                await Process(id, token);
            }
            finally
            {
                await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(hashtextextended(@id, 43002))", lease);
                release.Parameters.AddWithValue("id", id.ToString()); await release.ExecuteScalarAsync(CancellationToken.None);
            }
            return true;
        }
        return false;
    }
    private async Task Process(Guid id, CancellationToken token)
    {
        CopilotWorkflowRecord row; CopilotWorkflowStep[] steps; CopilotWorkflowStep step; CopilotPlaybook definition;
        await using (var transaction = await db.Database.BeginTransactionAsync(token))
        {
            row = await Lock(id, token);
            if (Terminal(row.Status)) { await transaction.CommitAsync(token); await CancelOwnedEvaluation(row, token); return; }
            steps = Read<CopilotWorkflowStep[]>(row.StepsJson);
            if (StopReason(row) is { } stop) { await Stop(row, steps, stop, token); await transaction.CommitAsync(token); await CancelOwnedEvaluation(row, token); return; }
            try { definition = ValidatePlan(row, steps); }
            catch (CopilotToolRejectedException error)
            { await Stop(row, steps, "failed", token, error.Code); await transaction.CommitAsync(token); await CancelOwnedEvaluation(row, token); return; }
            step = steps.First(s => s.Status != "completed");
            if (step.Tool is null)
            {
                row.Status = "completed"; row.FinishedAtUtc = Now(clock); row.Revision++;
                row.ConclusionJson = Write(CopilotWorkflowEvidence.Conclude(steps, definition.Intent == "optimize"));
                steps[step.Position] = step with { Status = "completed", Attempts = 1, StartedAtUtc = Now(clock), FinishedAtUtc = Now(clock) };
                row.StepsJson = Write(steps); await db.SaveChangesAsync(token); await transaction.CommitAsync(token); return;
            }
            if (step.Attempts >= 2 && step.Status == "running" || step.Polls >= 360)
            { await Stop(row, steps, "failed", token, "step_retry_exhausted"); await transaction.CommitAsync(token); await CancelOwnedEvaluation(row, token); return; }
            if (step.Id == "evaluate" && step.Arguments is null && (row.DeadlineAtUtc - Now(clock)).TotalSeconds < 15)
            { await Stop(row, steps, "timed_out", token, "insufficient_evaluation_budget"); await transaction.CommitAsync(token); return; }
            step = step with { Status = "running", Attempts = step.Status == "waiting" ? step.Attempts : step.Attempts + 1,
                Polls = step.Polls + (step.Id == "await-evaluation" ? 1 : 0), StartedAtUtc = step.StartedAtUtc ?? Now(clock),
                Arguments = step.Arguments ?? Arguments(row, step) };
            steps[step.Position] = step; row.StepsJson = Write(steps); row.Status = "running"; row.Revision++;
            await db.SaveChangesAsync(token); await transaction.CommitAsync(token);
        }
        using var logScope = logger.BeginScope(new Dictionary<string, object> { ["WorkflowId"] = id, ["RunId"] = row.RunId, ["CorrelationId"] = id.ToString("N") });
        logger.LogInformation("Playbook step workflow={WorkflowId} step={Step} agent={Agent} tool={Tool}", id, step.Id, step.Agent, step.Tool);
        CopilotToolResult? output = null; string? failure = null;
        using var controlled = CancellationTokenSource.CreateLinkedTokenSource(token);
        var work = ExecuteTool(row, definition, step.Agent, step.Tool!, step.Arguments!.Value, controlled.Token);
        try
        {
            while (!work.IsCompleted)
            {
                await Task.WhenAny(work, Task.Delay(200, token)); token.ThrowIfCancellationRequested();
                var current = await db.CopilotWorkflows.AsNoTracking().SingleAsync(w => w.Id == id, token);
                if (StopReason(current) is not null) controlled.Cancel();
            }
            output = await work;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            controlled.Cancel(); try { await work; } catch { } throw; // A new worker retries the same persisted arguments.
        }
        catch (OperationCanceledException) { failure = "tool_cancelled"; }
        catch (CopilotToolRejectedException error) { failure = error.Code; }
        catch (ScenarioConflictException) { failure = "workflow_context_changed"; }
        catch (ScenarioValidationException) { failure = "invalid_tool_arguments"; }
        catch (ScenarioNotFoundException) { failure = "workflow_evidence_unavailable"; }
        catch (Exception error) { failure = "tool_execution_failed"; logger.LogWarning("Playbook step failed errorType={ErrorType}", error.GetType().Name); }
        db.ChangeTracker.Clear();
        await using (var transaction = await db.Database.BeginTransactionAsync(token))
        {
            row = await Lock(id, token); steps = Read<CopilotWorkflowStep[]>(row.StepsJson); step = steps[step.Position];
            if (StopReason(row) is { } stop) await Stop(row, steps, stop, token);
            else if (failure is not null) await Stop(row, steps, "failed", token, failure);
            else if (output!.Investigation?.Observation.Status == "insufficient_data") await Stop(row, steps, "failed", token, "insufficient_observed_evidence");
            else
            {
                var waiting = step.Id == "await-evaluation" && output!.Evaluation?.Batch.Batch.Status is "pending" or "running";
                steps[step.Position] = step with { Status = waiting ? "waiting" : "completed", Output = output, FinishedAtUtc = waiting ? null : Now(clock) };
                row.StepsJson = Write(steps); row.NextAttemptAtUtc = Now(clock).AddMilliseconds(waiting ? 500 : 0); row.Revision++;
                await db.SaveChangesAsync(token);
            }
            await transaction.CommitAsync(token);
        }
        if (row.CancellationPending) await CancelOwnedEvaluation(row, token);
    }
    private CopilotPlaybook ValidatePlan(CopilotWorkflowRecord row, CopilotWorkflowStep[] steps)
    {
        var request = Read<StartCopilotWorkflow>(row.RequestJson);
        if (row.PromptVersion is not null && row.PromptVersion != CopilotPrompts.Version)
            throw new CopilotToolRejectedException("prompt_version_unavailable", "promptVersion", "The reviewed prompt version is unavailable; reproduce with a supported version after review.");
        var definition = CopilotPlaybookPolicy.Definition(request.PlaybookId, request.PlaybookVersion);
        if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(definition, WorkflowJson.Json), JsonSerializer.Deserialize<JsonElement>(row.DefinitionJson))
            || row.ManifestHash != Manifest(row, request, definition) || steps.Length != definition.Steps.Count
            || steps.Where((s, i) => s.Position != i || s.Id != definition.Steps[i].Id || s.Agent != definition.Steps[i].Agent || s.Tool != definition.Steps[i].Tool).Any())
            throw new CopilotToolRejectedException("playbook_plan_denied", "plan", "Planul diferă de versiunea permisă a playbook-ului.");
        foreach (var step in steps.Where(s => s.Tool is not null)) CopilotPlaybookPolicy.Admit(definition, step.Agent, step.Tool!);
        return definition;
    }
    private JsonElement Arguments(CopilotWorkflowRecord row, CopilotWorkflowStep step)
    {
        var request = Read<StartCopilotWorkflow>(row.RequestJson);
        object arguments = step.Id switch {
            "observe" => new CopilotInspectionArguments("Rezumă situația zonei."),
            "forecast" => new CopilotForecastArguments(request.HorizonMinutes),
            "settings" => new CopilotNoArguments(),
            "evaluate" => new CopilotEvaluationArguments(row.EvaluationId!.Value, request.Proposal!, request.CandidateCount,
                Math.Clamp((int)(row.DeadlineAtUtc - Now(clock)).TotalSeconds, 15, 120)),
            "await-evaluation" => new CopilotEvaluationReference(row.EvaluationId!.Value),
            _ => throw new CopilotToolRejectedException("playbook_plan_denied", "step", "Pas necunoscut.") };
        return JsonSerializer.SerializeToElement(arguments, WorkflowJson.Json);
    }
    private async Task<CopilotToolResult> ExecuteTool(CopilotWorkflowRecord row, CopilotPlaybook definition, string agent, string tool, JsonElement arguments, CancellationToken token)
    {
        CopilotPlaybookPolicy.Admit(definition, agent, tool);
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICopilotTools>().ExecuteAsync(row.ScenarioId, row.RunId,
            new(definition.Intent, row.ZoneId, row.Minute, new(tool, arguments)), token);
    }
    private string? StopReason(CopilotWorkflowRecord row) => row.CancellationRequested ? "cancelled" : Now(clock) >= row.DeadlineAtUtc ? "timed_out" : null;
    private async Task Stop(CopilotWorkflowRecord row, CopilotWorkflowStep[] steps, string status, CancellationToken token, string? code = null)
    {
        row.Status = status; row.StopCode = code ?? status; row.FinishedAtUtc = Now(clock); row.Revision++;
        row.ConclusionJson = Write(new CopilotWorkflowConclusion("inconclusive", status switch {
            "cancelled" => "Neconcludent: workflow anulat de operator. Nu se continuă cu alte unelte.",
            "timed_out" => "Neconcludent: bugetul workflow-ului a expirat. Rezultatele parțiale nu susțin o recomandare.",
            _ => "Neconcludent: un pas a fost refuzat, a eșuat sau nu are dovezi suficiente. Consultă codul opririi." }, null));
        row.StepsJson = Write(steps.Select(s => s.Status == "completed" ? s : s with {
            Status = s.Status == "pending" ? "skipped" : status, FinishedAtUtc = Now(clock), Code = row.StopCode }));
        row.CancellationPending = row.EvaluationId.HasValue && await db.Batches.AsNoTracking().AnyAsync(b => b.Id == row.EvaluationId, token);
        row.NextAttemptAtUtc = Now(clock); await db.SaveChangesAsync(token);
    }
    private async Task CancelOwnedEvaluation(CopilotWorkflowRecord row, CancellationToken token)
    {
        if (!row.CancellationPending || row.EvaluationId is not { } evaluationId) return;
        var pending = await db.Batches.AsNoTracking().AnyAsync(b => b.Id == evaluationId && (b.Status == "pending" || b.Status == "running"), token)
            || await db.BatchReplications.AsNoTracking().AnyAsync(r => r.BatchId == evaluationId && r.RemoteCancellationPending, token);
        if (pending)
        {
            try
            {
                // EvaluationId is generated and persisted by Start(), never supplied by callers.
                // Lifecycle cleanup must remain possible after authorization for new tool calls is revoked.
                if (!await db.CopilotWorkflows.AsNoTracking().AnyAsync(w => w.Id == row.Id && w.EvaluationId == evaluationId
                        && w.ScenarioId == row.ScenarioId && w.RunId == row.RunId, token)
                    || !await db.BatchVariants.AsNoTracking().AnyAsync(v => v.BatchId == evaluationId && v.Position == 0
                        && v.ScenarioId == row.ScenarioId, token))
                    throw new ScenarioConflictException("Evaluation does not belong to this workflow.");
                await using var scope = scopes.CreateAsyncScope();
                if (!await db.AiAgentEvents.AsNoTracking().AnyAsync(e => e.AttemptId == row.Id && e.Kind == "workflow_cleanup", token))
                    await scope.ServiceProvider.GetRequiredService<AiGovernance>().RecordAsync(new() {
                        AttemptId = row.Id, Kind = "workflow_cleanup", Status = "cancellation_requested", Tool = "cancel_evaluation",
                        ScenarioId = row.ScenarioId, RunId = row.RunId, PolicyVersion = row.PolicyVersion ?? 0 }, token);
                await scope.ServiceProvider.GetRequiredService<IBatchService>().CancelAsync(evaluationId, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) { logger.LogWarning("Workflow cancellation will retry workflow={WorkflowId} errorType={ErrorType}", row.Id, error.GetType().Name); }
        }
        await db.CopilotWorkflows.Where(w => w.Id == row.Id).ExecuteUpdateAsync(set => set.SetProperty(w => w.CancellationPending, pending)
            .SetProperty(w => w.NextAttemptAtUtc, Now(clock).AddSeconds(1)), token);
    }
    private Task<CopilotWorkflowRecord> Lock(Guid id, CancellationToken token) => db.CopilotWorkflows
        .FromSqlInterpolated($"SELECT * FROM twin.copilot_workflows WHERE \"Id\" = {id} FOR UPDATE").SingleAsync(token);
}

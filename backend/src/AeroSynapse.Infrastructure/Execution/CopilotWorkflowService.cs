using System.Security.Cryptography;
using System.Text.Json;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using AeroSynapse.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using AeroSynapse.Infrastructure.Governance;

namespace AeroSynapse.Infrastructure.Execution;

public sealed class CopilotWorkflowService(TwinDbContext db, IServiceScopeFactory scopes, ICopilotService copilot, TimeProvider clock, AiGovernance governance) : ICopilotWorkflows
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static bool Terminal(string status) => status is not ("pending" or "running");
    internal static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Json)!;
    internal static string Write<T>(T value) => JsonSerializer.Serialize(value, Json);
    internal static DateTime Now(TimeProvider clock) { var ticks = clock.GetUtcNow().UtcTicks; return new(ticks - ticks % 10, DateTimeKind.Utc); }
    internal static CopilotScope Scope(CopilotWorkflowRecord row) => new(row.ScenarioId, row.RunId, row.ZoneId, row.Minute);
    internal static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Json)));
    internal static string Manifest(CopilotWorkflowRecord row, StartCopilotWorkflow request, CopilotPlaybook definition)
    {
        var content = new { definition, scope = Scope(row), request = request with { CommandId = Guid.Empty }, row.SourceHash, row.Seed, row.EngineVersion, row.EngineBuild };
        return row.PolicyVersion.HasValue ? Hash(new { content, row.PolicyVersion, row.PromptVersion }) : Hash(content);
    }
    internal static CopilotWorkflow View(CopilotWorkflowRecord row) => new(row.Id, "1.0", Scope(row), row.Status, row.Revision,
        row.CreatedAtUtc, row.DeadlineAtUtc, row.FinishedAtUtc, row.CancellationRequested, row.CancellationPending, row.StopCode,
        row.ReplayOfId, row.ManifestHash, row.SourceHash, row.Seed, row.EngineVersion, row.EngineBuild, row.EvaluationId,
        Read<StartCopilotWorkflow>(row.RequestJson), Read<CopilotPlaybook>(row.DefinitionJson), Read<CopilotWorkflowStep[]>(row.StepsJson),
        row.ConclusionJson is null ? null : Read<CopilotWorkflowConclusion>(row.ConclusionJson), row.PolicyVersion, row.PromptVersion);

    public Task<CopilotWorkflow> StartAsync(Guid scenarioId, Guid runId, StartCopilotWorkflow request, CancellationToken token) => Start(scenarioId, runId, request, null, token);
    private async Task<CopilotWorkflow> Start(Guid scenarioId, Guid runId, StartCopilotWorkflow request, Guid? replayOf, CancellationToken token)
    {
        var definition = CopilotPlaybookPolicy.Definition(request.PlaybookId, request.PlaybookVersion);
        if (request.CommandId == Guid.Empty || request.ZoneId == Guid.Empty || request.Minute < 0 || request.HorizonMinutes is < 15 or > 30
            || request.TimeLimitSeconds is < 15 or > 180 || request.CandidateCount is < 1 or > 3)
            throw Reject("invalid_workflow_arguments", "request", "Sunt necesare ID-uri, orizont de 15–30 minute, 1–3 candidați și buget de 15–180 secunde.");
        if (definition.Intent == "diagnose" && request.Proposal is not null)
            throw Reject("playbook_tool_denied", "proposal", "Playbook-ul de diagnostic nu acceptă o propunere de realocare.");
        if (definition.Intent == "optimize" && (request.Proposal is not { } p || !double.IsFinite(p.TimeLimitSeconds)
            || p.TimeLimitSeconds is < 0.1 or > 5 || p.AvailableStaff < 0 || p.Checkpoints is null || p.Checkpoints.Count is < 1 or > 200))
            throw Reject("invalid_workflow_arguments", "proposal", "Confirmă explicit constrângerile de personal și un buget de solver de 0.1–5 secunde.");
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({request.CommandId.ToString()}, 43001))", token);
        var existing = await db.CopilotWorkflows.AsNoTracking().SingleOrDefaultAsync(w => w.Id == request.CommandId, token);
        if (existing is not null)
        {
            if (existing.ScenarioId != scenarioId || existing.RunId != runId || existing.ReplayOfId != replayOf
                || !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(Read<StartCopilotWorkflow>(existing.RequestJson), Json), JsonSerializer.SerializeToElement(request, Json)))
                throw new ScenarioConflictException("CommandId already belongs to a different workflow request.");
            return View(existing);
        }
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM twin.runs WHERE \"Id\" = {runId} FOR UPDATE", token);
        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId && r.ScenarioId == scenarioId, token)
            ?? throw new ScenarioNotFoundException("Run not found in this scenario.");
        var context = await copilot.ContextAsync(scenarioId, runId, token);
        if (!context.IsSynthetic || !context.ResultsValid || context.UnavailableReason is not null)
            throw Reject("workflow_evidence_unavailable", "runId", "Playbook-urile necesită o rulare DEMO finalizată cu snapshot valid.");
        if (!context.Zones.Any(z => z.Id == request.ZoneId) || (long)request.Minute + request.HorizonMinutes > context.DurationMinutes)
            throw Reject("invalid_workflow_arguments", "zoneId/minute", "Zona și întregul orizont trebuie să aparțină capturii rulării.");
        // Replay owns a repeatable-read transaction; use a separate context while the source run is locked.
        ReplayEnvelope source;
        await using (var sourceScope = scopes.CreateAsyncScope())
            source = await sourceScope.ServiceProvider.GetRequiredService<IReplayService>().GetAsync(runId, token);
        var data = Read<ReplayData>(source.PayloadJson);
        if (definition.Intent == "optimize")
        {
            var proposal = request.Proposal!;
            if (proposal.Revision != context.ScenarioRevision || proposal.AvailableStaff > data.Captured.Layout.Layout.Staff.Count
                || proposal.Checkpoints.Any(c => c is null || c.MinimumStaff < 0 || c.MaximumStaff < c.MinimumStaff
                    || c.PhysicalLimit < 0 || c.PhysicalLimit > data.Captured.Layout.Layout.Staff.Count || c.MaximumStaff > proposal.AvailableStaff
                    || c.ChangeCost < 0 || c.Closed && c.RequiredOpen || !context.Zones.Any(z => z.Id == c.CheckpointId))
                || proposal.Checkpoints.Select(c => c.CheckpointId).Distinct().Count() != context.Zones.Count || proposal.Checkpoints.Count != context.Zones.Count)
                throw Reject("invalid_workflow_arguments", "proposal", "Constrângerile trebuie să corespundă reviziei, zonelor și personalului disponibil din captură.");
        }
        var now = Now(clock);
        var policy = await governance.PolicyAsync(token);
        var row = new CopilotWorkflowRecord { Id = request.CommandId, SceneId = run.SceneId, ScenarioId = scenarioId, RunId = runId,
            ZoneId = request.ZoneId, Minute = request.Minute, PlaybookId = definition.Id, CreatedAtUtc = now, DeadlineAtUtc = now.AddSeconds(request.TimeLimitSeconds),
            NextAttemptAtUtc = now, RequestJson = Write(request), DefinitionJson = Write(definition), SourceHash = source.ContentHash,
            Seed = data.Captured.Scenario.Inputs.Seed, EngineVersion = run.EngineVersion, EngineBuild = run.EngineBuild, ReplayOfId = replayOf,
            PolicyVersion = policy.Version, PromptVersion = policy.PromptVersion,
            EvaluationId = definition.Intent == "optimize" ? Guid.NewGuid() : null,
            StepsJson = Write(definition.Steps.Select((s, i) => new CopilotWorkflowStep(i, s.Id, s.Agent, s.Tool, "pending", 0, 0, null, null, null, null, null))) };
        row.ManifestHash = Manifest(row, request, definition);
        db.CopilotWorkflows.Add(row); await db.SaveChangesAsync(token); await transaction.CommitAsync(token); return View(row);
    }
    public async Task<CopilotWorkflow> GetAsync(Guid scenarioId, Guid runId, Guid id, CancellationToken token) => View(await Find(scenarioId, runId, id, token));
    public async Task<IReadOnlyList<CopilotWorkflowSummary>> ListAsync(Guid scenarioId, Guid runId, CancellationToken token) =>
        await db.CopilotWorkflows.AsNoTracking().Where(w => w.ScenarioId == scenarioId && w.RunId == runId)
            .OrderByDescending(w => w.CreatedAtUtc).Take(50).Select(w => new CopilotWorkflowSummary(w.Id, w.PlaybookId, w.ZoneId, w.Minute, w.Status, w.CreatedAtUtc)).ToArrayAsync(token);
    public async Task<CopilotWorkflow> CancelAsync(Guid scenarioId, Guid runId, Guid id, CancellationToken token)
    {
        await Find(scenarioId, runId, id, token);
        await db.CopilotWorkflows.Where(w => w.Id == id && (w.Status == "pending" || w.Status == "running") && !w.CancellationRequested)
            .ExecuteUpdateAsync(set => set.SetProperty(w => w.CancellationRequested, true).SetProperty(w => w.Revision, w => w.Revision + 1)
                .SetProperty(w => w.NextAttemptAtUtc, Now(clock)), token);
        return await GetAsync(scenarioId, runId, id, token);
    }
    public async Task<CopilotWorkflow> ReplayAsync(Guid scenarioId, Guid runId, Guid id, ReplayCopilotWorkflow request, CancellationToken token)
    {
        var source = await Find(scenarioId, runId, id, token);
        if (!Terminal(source.Status) || source.CancellationPending) throw new ScenarioConflictException("Stop the workflow and finish cancellation before reproducing it.");
        return await Start(scenarioId, runId, Read<StartCopilotWorkflow>(source.RequestJson) with { CommandId = request.CommandId }, source.Id, token);
    }
    private async Task<CopilotWorkflowRecord> Find(Guid scenarioId, Guid runId, Guid id, CancellationToken token) =>
        await db.CopilotWorkflows.AsNoTracking().SingleOrDefaultAsync(w => w.Id == id && w.ScenarioId == scenarioId && w.RunId == runId, token)
        ?? throw new ScenarioNotFoundException("Workflow not found in this scenario/run.");
    private static CopilotToolRejectedException Reject(string code, string field, string message) => new(code, field, message);
}

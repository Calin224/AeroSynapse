using System.Diagnostics;
using System.Text.Json;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using AeroSynapse.Infrastructure.Audit;
using AeroSynapse.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace AeroSynapse.Infrastructure.Governance;

public sealed record AiModelLease(Guid AttemptId, AiPolicyRecord Policy, long Started);

public sealed class AiGovernance(TwinDbContext db, TimeProvider clock, ILogger<AiGovernance> logger, IServiceScopeFactory scopes)
{
    public async Task<AiPolicyRecord> PolicyAsync(CancellationToken token) =>
        await db.AiPolicies.AsNoTracking().OrderByDescending(p => p.Version).FirstOrDefaultAsync(token)
        ?? throw new CopilotToolRejectedException("ai_policy_unavailable", "policy", "No reviewed AI policy is available for this organization.", 503);

    public async Task<int> AuthorizeToolAsync(Guid scenarioId, Guid runId, string name, CancellationToken token)
    {
        var policy = await PolicyAsync(token);
        if (!CopilotToolPolicy.Catalog.Any(t => t.Name == name))
        {
            await RecordAsync(new() { Kind = "tool_gate", Status = "tool_not_allowed", Tool = "unknown", ScenarioId = scenarioId, RunId = runId, PolicyVersion = policy.Version }, token);
            throw new CopilotToolRejectedException("tool_not_allowed", "call.name", "The tool is not in the reviewed catalog.");
        }
        var allowed = JsonSerializer.Deserialize<string[]>(policy.AllowedToolsJson)!.Contains(name);
        await RecordAsync(new() { Kind = "tool_gate", Status = allowed ? "allowed" : "tool_policy_denied", Tool = CopilotToolPolicy.Catalog.Any(t => t.Name == name) ? name : "unknown",
            ScenarioId = scenarioId, RunId = runId, PolicyVersion = policy.Version }, token);
        if (!allowed) throw new CopilotToolRejectedException("tool_policy_denied", "call.name", "This tool is disabled by the organization's AI policy.", 403);
        return policy.Version;
    }

    public async Task<AiModelLease?> ReserveAsync(CopilotScope scope, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(59001)", token);
        var policy = await PolicyAsync(token);
        var day = clock.GetUtcNow().UtcDateTime.Date;
        var reservations = db.AiAgentEvents.Where(e => e.Kind == "llm_reserved" && e.RecordedAtUtc >= day);
        var count = await reservations.CountAsync(token);
        var amount = await reservations.SumAsync(e => (decimal?)e.ReservedUsd, token) ?? 0;
        var status = policy.PromptVersion != CopilotPrompts.Version ? "prompt_version_unavailable" : !policy.ModelEnabled ? "policy_disabled"
            : count >= policy.DailyCallLimit || amount + policy.CallReservationUsd > policy.DailyBudgetUsd ? "budget_exhausted" : "reserved";
        var attempt = Guid.NewGuid();
        await RecordAsync(new() { AttemptId = attempt, Kind = status == "reserved" ? "llm_reserved" : "llm_denied", Status = status,
            ScenarioId = scope.ScenarioId, RunId = scope.RunId, PolicyVersion = policy.Version, ReservedUsd = status == "reserved" ? policy.CallReservationUsd : 0 }, token);
        await transaction.CommitAsync(token);
        return status == "reserved" ? new(attempt, policy, Stopwatch.GetTimestamp()) : null;
    }

    public Task FinishAsync(AiModelLease lease, CopilotScope scope, CopilotModelResult result, CancellationToken token) => RecordAsync(new() {
        AttemptId = lease.AttemptId, Kind = "llm_result", Status = result.Status, ScenarioId = scope.ScenarioId, RunId = scope.RunId,
        PolicyVersion = lease.Policy.Version, InputTokens = result.InputTokens, OutputTokens = result.OutputTokens,
        DurationMs = Stopwatch.GetElapsedTime(lease.Started).TotalMilliseconds }, token);

    public async Task RecordAsync(AiAgentEvent row, CancellationToken token)
    {
        row.Id = Guid.NewGuid(); if (row.AttemptId == Guid.Empty) row.AttemptId = row.Id;
        row.RecordedAtUtc = clock.GetUtcNow().UtcDateTime;
        row.ActorId = AuditContext.Current.ActorId; row.CorrelationId = AuditContext.Current.CorrelationId;
        // Copilot rows take its prompt; the live agent stamps its own.
        if (row.PromptVersion == "") { row.PromptVersion = CopilotPrompts.Version; row.PromptHash = CopilotPrompts.Hash; }
        if (db.Database.CurrentTransaction is not null)
        { db.AiAgentEvents.Add(row); await db.SaveChangesAsync(token); }
        else
        {
            // Failed tool operations may leave tracked changes. Audit in a clean
            // scope so recording a failure can never flush those mutations.
            await using var scope = scopes.CreateAsyncScope();
            var journal = scope.ServiceProvider.GetRequiredService<TwinDbContext>();
            journal.AiAgentEvents.Add(row); await journal.SaveChangesAsync(token);
        }
        logger.LogInformation("AI tenant={TenantId} kind={Kind} status={Status} policy={PolicyVersion} prompt={PromptVersion} attemptId={AttemptId} elapsedMs={DurationMs} reservedUsd={ReservedUsd}",
            TenantContext.Id, row.Kind, row.Status, row.PolicyVersion, row.PromptVersion, row.AttemptId, row.DurationMs, row.ReservedUsd);
    }
}

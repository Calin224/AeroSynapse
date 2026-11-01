using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Simulation;
using AeroSynapse.Application.Storage;
using AeroSynapse.Infrastructure.Operations;
using AeroSynapse.Infrastructure.Simulation;
using AeroSynapse.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AeroSynapse.Infrastructure.Execution;

/// <summary>
/// Compares a finished agent run with the same captured day and seed simulated without the agent's decisions.
/// The comparison run has its own id derived from the run's, so the engine computes it once and serves it after.
/// </summary>
public sealed class AgentImpactService(TwinDbContext db, IIntelligenceClient engine, IOptions<SimulationOptions> settings,
    IOptions<OperationsOptions> operations)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Null while the run is unfinished or when the agent changed nothing.</summary>
    public async Task<AgentImpact?> GetAsync(Guid runId, CancellationToken token)
    {
        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId, token) ?? throw new ScenarioNotFoundException("Run was not found.");
        if (!run.ResultsValid || run.InputSnapshotJson is null) return null;
        var picked = (await db.AgentDecisions.AsNoTracking().Where(d => d.RunId == runId && d.Kind != AgentCandidates.None)
                .OrderBy(d => d.Sequence).Select(d => d.ActionJson).ToListAsync(token))
            .Select(json => JsonSerializer.Deserialize<AgentOption>(json, Json)!).ToArray();
        if (picked.Length == 0) return null;
        var captured = JsonSerializer.Deserialize<RunInputSnapshot>(run.InputSnapshotJson, Json)!;
        var names = captured.Layout.Layout.Checkpoints.ToDictionary(c => c.Id, c => string.IsNullOrWhiteSpace(c.Code) ? c.Name : c.Code);
        var stored = await db.Snapshots.AsNoTracking().Where(s => s.RunId == runId && s.ZoneType == "checkpoint")
            .Select(s => s.PayloadJson).ToListAsync(token);
        var withAgent = stored.Select(json => JsonSerializer.Deserialize<KpiSnapshot>(json, ContractJson.Options)!).ToArray();
        var withoutAgent = await ExecuteAsync(CapturedSimulation.Request(ComparisonId(runId), captured), token);
        return new(AgentImpactSummary.Changes(picked, names), AgentImpactSummary.From(withoutAgent), AgentImpactSummary.From(withAgent));
    }

    private static Guid ComparisonId(Guid runId) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"agent-impact/{runId:N}")).AsSpan(0, 16));

    public async Task<IReadOnlyList<KpiSnapshot>> ExecuteAsync(SimulationRequest request, CancellationToken token) =>
        (await SimulateAsync(request, token)).KpiResult!.Snapshots;

    /// <summary>Simulates a captured request to completion; a request id the engine already ran is served from its result.</summary>
    public async Task<SimulationResponse> SimulateAsync(SimulationRequest request, CancellationToken token)
    {
        var correlationId = request.RunId.ToString("N");
        var deadline = DateTime.UtcNow.AddSeconds(settings.Value.ReconciliationSeconds);
        EngineExecution? execution = null;
        try { execution = await engine.StartAsync(request, correlationId, token); }
        catch (IntelligenceException error) when (error.Transient) { }
        while (execution?.Result is null)
        {
            if (DateTime.UtcNow >= deadline) throw new SimulationEngineException("engine_timeout", "The comparison without the agent did not finish in time.");
            await Task.Delay(operations.Value.EnginePollMilliseconds, token);
            try { execution = await engine.GetAsync(request.RunId, correlationId, token); }
            catch (IntelligenceException error) when (error.Transient) { }
        }
        if (execution.Result.Status != "completed" || execution.Result.KpiResult is null)
            throw new SimulationEngineException("engine_failed", "The comparison without the agent could not be simulated.");
        return execution.Result;
    }
}

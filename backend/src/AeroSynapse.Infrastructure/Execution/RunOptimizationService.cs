using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Simulation;
using AeroSynapse.Application.Storage;
using AeroSynapse.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace AeroSynapse.Infrastructure.Execution;

/// <summary>
/// After a run: staffing options simulated again on the captured day and seed, then explained and ranked by the AI model.
/// Every number shown comes from the simulator; without the model the options keep their numbers and a deterministic pick.
/// </summary>
public sealed class RunOptimizationService(TwinDbContext db, AgentImpactService simulator, IAgentModel model, IMemoryCache cache, IScenarioService scenarios)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Null while the run has no valid results.</summary>
    public async Task<RunOptimization?> GetAsync(Guid runId, CancellationToken token)
    {
        // Looked up in this workspace's store first, so a cached answer never crosses workspaces.
        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId, token) ?? throw new ScenarioNotFoundException("Run was not found.");
        if (!run.ResultsValid || run.InputSnapshotJson is null) return null;
        if (cache.TryGetValue(("ai-optimization", runId), out RunOptimization? cached)) return cached;
        var result = await EvaluateAsync(runId, JsonSerializer.Deserialize<RunInputSnapshot>(run.InputSnapshotJson, Json)!, token);
        // A full answer is kept for hours; a fallback briefly, so refreshing does not re-simulate, yet the model gets another try soon.
        cache.Set(("ai-optimization", runId), result, result.ModelStatus == "chosen" ? TimeSpan.FromHours(6) : TimeSpan.FromMinutes(2));
        return result;
    }

    /// <summary>The options for a captured day, simulated and explained; no store access, so it also runs outside a request.</summary>
    public async Task<RunOptimization> EvaluateAsync(Guid runId, RunInputSnapshot captured, CancellationToken token)
    {
        var (inputs, layout) = (captured.Scenario.Inputs, captured.Layout.Layout);
        var names = layout.Checkpoints.ToDictionary(c => c.Id, c => string.IsNullOrWhiteSpace(c.Code) ? c.Name : c.Code);
        // The reference is the captured day simulated again, so options and reference differ only by the option itself.
        var request = CapturedSimulation.Request(Derived(runId, "baseline"), captured);
        var response = await simulator.SimulateAsync(request, token);
        var reference = response.KpiResult!.Snapshots;
        var desks = Desks(request, response, layout);
        // Controls with a shift plan or a staffing event are left alone, as the live agent does: a changed staff count
        // there would contradict the plan or the event (a staff reduction must stay below the staff on duty).
        var scheduled = (request.StaffSchedules ?? []).Select(s => s.CheckpointId)
            .Concat((request.Events ?? []).Where(e => e.CheckpointId is not null).Select(e => e.CheckpointId!.Value)).ToHashSet();
        var proposed = RunOptimizationPlanner.Candidates(request.Checkpoints, reference, names, scheduled, desks);
        // Only what the scenario's own people can staff is simulated; the first extra capacity it lacks, the bottleneck's, is said why.
        var issues = proposed.ToDictionary(c => c.Id, c => RunOptimizationPlanner.Staff(inputs, layout, c.StaffDelta, c.DeskDelta).Issue);
        var notice = proposed.FirstOrDefault(c => c.Kind is "add" or "desk") is { } first ? issues[first.Id] : null;
        var candidates = proposed.Where(c => issues[c.Id] is null).ToArray();
        var simulated = (await Task.WhenAll(candidates.Select(async candidate =>
        {
            try
            {
                var option = await simulator.SimulateAsync(RunOptimizationPlanner.Apply(request, Derived(runId, candidate.Id), candidate), token);
                return (Candidate: candidate, Result: (AgentImpactSide?)AgentImpactSummary.From(option.KpiResult!.Snapshots, option.CheckInResult?.Snapshots));
            }
            // One option the engine cannot simulate is left out; the others still answer.
            catch (Exception failure) when (failure is IntelligenceException or SimulationEngineException) { return (Candidate: candidate, Result: null); }
        }))).Where(s => s.Result is not null).Select(s => (s.Candidate, Result: s.Result!)).ToArray();
        var baseline = AgentImpactSummary.From(reference, response.CheckInResult?.Snapshots);
        var controls = RunOptimizationPlanner.Controls(request.Checkpoints, reference, names);

        // Zone names as the run summary shows them, check-in included, so the panel names the same busiest moment.
        var places = names.Concat((layout.CheckInAreas ?? []).Select(a => KeyValuePair.Create(a.Id, string.IsNullOrWhiteSpace(a.Code) ? a.Name : a.Code)))
            .GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.First().Value);
        var brief = RunOptimizationPlanner.Brief(baseline, simulated, OptimizationBottleneck.Of(reference, response.CheckInResult?.Snapshots, places));
        // The model gets the brief without its summary: words to rephrase, never doubles to compare.
        var advice = simulated.Length == 0 ? new OptimizationAdvice("no_options", model.Status.Model)
            : await model.AdviseAsync(new { brief.Bottleneck, brief.Recommendation, brief.Options }, simulated.Select(s => s.Candidate.Id).ToArray(), token);
        return RunOptimizationPlanner.Compose(baseline, controls, simulated, brief, advice, places.Values.Distinct().ToArray()) with
            { CheckIn = desks, Notice = notice };
    }

    /// <summary>
    /// Writes an option into the run's scenario as a new revision through the ordinary scenario update. Refused once the
    /// scenario moved past the simulated revision: the option was measured against that version only.
    /// </summary>
    public async Task<ScenarioDetails> ApplyAsync(Guid runId, IReadOnlyDictionary<Guid, int>? staffDelta, int deskDelta, CancellationToken token)
    {
        staffDelta ??= new Dictionary<Guid, int>();
        if (staffDelta.Count == 0 && deskDelta == 0)
            throw new ScenarioValidationException(new Dictionary<string, string[]> { ["staffDelta"] = ["Alege o variantă de personal."] });
        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId, token) ?? throw new ScenarioNotFoundException("Run was not found.");
        if (run.InputSnapshotJson is null) throw new ScenarioConflictException("Simularea nu are un scenariu capturat.");
        var captured = JsonSerializer.Deserialize<RunInputSnapshot>(run.InputSnapshotJson, Json)!.Scenario.Scenario;
        var current = await scenarios.GetAsync(captured.Id, token);
        if (current is null || current.Scenario.DeletedAtUtc is not null) throw new ScenarioNotFoundException("Scenario was not found.");
        if (current.Scenario.Revision != captured.Revision)
            throw new ScenarioConflictException("Scenariul s-a modificat după această simulare.");
        var layout = await scenarios.GetLayoutAsync(current.Scenario.SceneId, current.Scenario.LayoutVersionId, token)
            ?? throw new ScenarioNotFoundException("Captured layout was not found.");
        var (inputs, issue) = RunOptimizationPlanner.Staff(current.Inputs, layout.Layout, staffDelta, deskDelta);
        if (issue is not null) throw new ScenarioValidationException(new Dictionary<string, string[]> { ["staffDelta"] = [issue] });
        var s = current.Scenario;
        return await scenarios.UpdateAsync(s.Id, new(s.Code, s.Name, s.Description, s.SceneId, s.LayoutVersionId, inputs!, s.Revision), token);
    }

    // The desks as the reference simulated them; null without check-in or with a desk plan, which the desk count does not decide.
    private static OptimizationDesks? Desks(SimulationRequest request, SimulationResponse response, SceneDetails layout)
    {
        if (request.CheckIn is not { DeskSteps: null } checkIn || layout.CheckInAreas?.SingleOrDefault(a => a.Id == checkIn.ZoneId) is not { } area) return null;
        var rows = response.CheckInResult?.Snapshots ?? [];
        return new(string.IsNullOrWhiteSpace(area.Code) ? area.Name : area.Code, checkIn.StaffCount, area.DeskCount,
            rows.MaxBy(r => r.Minute)?.P95WaitMinutes, rows.Count == 0 ? 0 : rows.Max(r => r.QueuedPassengers));
    }

    private static Guid Derived(Guid runId, string option) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"ai-optimization/v3/{runId:N}/{option}")).AsSpan(0, 16));
}

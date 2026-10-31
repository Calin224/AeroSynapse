using System.Data;
using System.Text.Json;
using AeroSynapse.Application.Operations;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using AeroSynapse.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace AeroSynapse.Infrastructure.Operations;

public sealed class OperationsReader(TwinDbContext db) : IOperationsReader
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<long> CursorAsync(Guid scenarioId, CancellationToken token)
    {
        if (!await db.Scenarios.AnyAsync(x => x.Id == scenarioId, token)) throw new ScenarioNotFoundException("Scenario was not found.");
        return await db.OperationsCursors.Where(x => x.ScenarioId == scenarioId).Select(x => (long?)x.Sequence).SingleOrDefaultAsync(token) ?? 0;
    }

    public async Task<ScenarioOperationsState> StateAsync(Guid scenarioId, Guid? runId, long? afterSequence, CancellationToken token)
    {
        if (afterSequence < 0) throw new ScenarioValidationException(new Dictionary<string, string[]> { ["afterSequence"] = ["Must be nonnegative."] });
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        var sequence = await CursorAsync(scenarioId, token);
        var runs = await db.Runs.AsNoTracking().Where(x => x.ScenarioId == scenarioId).OrderByDescending(x => x.StartedAtUtc).ThenBy(x => x.Id).Take(20).ToListAsync(token);
        var selected = runId.HasValue ? await db.Runs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == runId && x.ScenarioId == scenarioId, token) : runs.FirstOrDefault();
        if (runId.HasValue && selected is null) throw new ScenarioNotFoundException("Run was not found in this scenario.");
        var ids = runs.Select(x => x.Id).Append(selected?.Id ?? Guid.Empty).ToArray();
        var progress = await db.Progress.AsNoTracking().Where(x => ids.Contains(x.RunId)).ToDictionaryAsync(x => x.RunId, token);
        var minimum = await db.OperationsEvents.Where(x => x.ScenarioId == scenarioId).Select(x => (long?)x.Sequence).MinAsync(token);
        var optimization = await LatestOptimization(scenarioId, token);
        var reset = afterSequence.HasValue && (afterSequence > sequence || (minimum.HasValue && afterSequence < minimum - 1));
        return new(scenarioId, sequence, reset, runs.Select(Describe).ToArray(), selected is null ? null : Describe(selected), optimization);

        OperationsRun Describe(SimulationRun run)
        {
            progress.TryGetValue(run.Id, out var value);
            var zones = value is null ? [] : JsonSerializer.Deserialize<ProgressZone[]>(value.ZonesJson, Json)!;
            return new(run.Id, run.Status, run.Revision, run.ResultsValid, value?.SimulationMinute,
                value is null ? null : new(zones.Take(100).ToArray(), zones.Length));
        }
    }

    public async Task<IReadOnlyList<OperationsMessage>> EventsAsync(Guid scenarioId, long afterSequence, int limit, CancellationToken token)
    {
        if (limit is < 1 or > 64 || afterSequence < 0) throw new ArgumentOutOfRangeException(nameof(limit));
        var payloads = await db.OperationsEvents.AsNoTracking().Where(x => x.ScenarioId == scenarioId && x.Sequence > afterSequence)
            .OrderBy(x => x.Sequence).Take(limit).Select(x => x.PayloadJson).ToArrayAsync(token);
        return payloads.Select(x => JsonSerializer.Deserialize<OperationsMessage>(x, Json)!).ToArray();
    }

    private async Task<OptimizationSummary?> LatestOptimization(Guid scenarioId, CancellationToken token)
    {
        var localRuns = db.Runs.Where(x => x.ScenarioId == scenarioId).Select(x => x.Id);
        var recommendation = await db.Recommendations.AsNoTracking().Where(x => localRuns.Contains(x.BaselineRunId)
            || db.RecommendationCandidates.Any(candidate => candidate.RecommendationId == x.Id && localRuns.Contains(candidate.RunId)))
            .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id).FirstOrDefaultAsync(token);
        if (recommendation is null) return null;
        var candidates = await db.RecommendationCandidates.AsNoTracking().Where(x => x.RecommendationId == recommendation.Id).OrderBy(x => x.Rank).Select(x => x.RunId).ToArrayAsync(token);
        var local = await localRuns.Where(x => candidates.Contains(x) || x == recommendation.BaselineRunId).ToArrayAsync(token);
        var recommended = candidates.FirstOrDefault();
        return new(recommendation.Id, local.Contains(recommendation.BaselineRunId) ? recommendation.BaselineRunId : null,
            local.Contains(recommended) ? recommended : null, local.Length);
    }
}

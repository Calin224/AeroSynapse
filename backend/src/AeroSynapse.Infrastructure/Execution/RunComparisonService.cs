using System.Data;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using AeroSynapse.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace AeroSynapse.Infrastructure.Execution;

public sealed class RunComparisonService(TwinDbContext db) : IRunComparisonService
{
    public static RunProvenance Provenance(SimulationRun run, string? correlationId = null) => new(
        RunFingerprint.Version, run.InputHash, run.ResultsValid ? run.ResultHash : null, run.EngineVersion,
        run.EngineBuild, run.EngineMilliseconds, run.LaunchedAtUtc is { } launched && run.FinishedAtUtc is { } finished
            ? (finished - launched).TotalMilliseconds : null, correlationId ?? run.Id.ToString("N"));

    public async Task<RunComparison> CompareAsync(Guid baselineId, Guid candidateId, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        var baselineRun = await Require(baselineId, token); var candidateRun = await Require(candidateId, token);
        var baseline = await Describe(baselineRun, token);
        var candidate = await Describe(candidateRun, token);
        var compatibility = RunComparisonPolicy.Assess(baselineRun, candidateRun);
        var left = await FinalMetrics(baseline, token);
        var right = await FinalMetrics(candidate, token);
        var keys = left.Keys.Union(right.Keys).OrderBy(x => x.ZoneType, StringComparer.Ordinal).ThenBy(x => x.ZoneId).ThenBy(x => x.Name, StringComparer.Ordinal)
            .ThenBy(x => x.Unit, StringComparer.Ordinal).ToArray();
        var metrics = keys.Take(100).Select(key =>
        {
            left.TryGetValue(key, out var av); right.TryGetValue(key, out var bv);
            return RunComparisonPolicy.Difference(key.ZoneId, key.Name, key.Unit, av, bv, compatibility.Comparable, key.ZoneType);
        }).ToArray();
        if (compatibility.Comparable && keys.Any(key => key.Name == "waitSampleCount" && left.TryGetValue(key, out var av) && right.TryGetValue(key, out var bv) && av.Value != bv.Value))
            compatibility = compatibility with { Issues = [.. compatibility.Issues, new("wait_population_differs", "Mediile de așteptare includ doar serviciile terminate; numărul acestor pasageri diferă. Compară și waitSampleCount și pasagerii procesați.", "warning")] };
        var a = baseline.Provenance; var b = candidate.Provenance;
        return new(baseline, candidate, Equal(a.InputHash, b.InputHash),
            a.EngineVersion is null || b.EngineVersion is null || a.EngineBuild is null || b.EngineBuild is null
                ? null : a.EngineVersion == b.EngineVersion && a.EngineBuild == b.EngineBuild,
            baseline.ResultsValid && candidate.ResultsValid ? Equal(a.ResultHash, b.ResultHash) : null,
            metrics, keys.Length > 100, compatibility);
    }

    private static bool? Equal(string? left, string? right) => left is null || right is null ? null : left == right;
    private async Task<SimulationRun> Require(Guid id, CancellationToken token) =>
        await db.Runs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token) ?? throw new ScenarioNotFoundException("Run was not found.");
    private async Task<ComparedRun> Describe(SimulationRun run, CancellationToken token)
    {
        var correlation = await db.SimulationCommands.Where(x => x.RunId == run.Id).Select(x => x.CorrelationId).SingleOrDefaultAsync(token);
        return new(run.Id, run.Status, run.ResultsValid, run.ScenarioId, run.ScenarioRevision, Provenance(run, correlation), run.FailureReason);
    }

    private async Task<Dictionary<(string ZoneType, Guid ZoneId, string Name, string Unit), RunMetric>> FinalMetrics(ComparedRun run, CancellationToken token)
    {
        if (!run.ResultsValid) return [];
        var rows = await db.Metrics.AsNoTracking().Where(x => x.RunId == run.Id)
            .GroupBy(x => new { x.ZoneType, x.ZoneId, x.Name, x.Unit })
            .Select(group => group.OrderByDescending(x => x.RecordedAtUtc).ThenBy(x => x.Id).First()).ToArrayAsync(token);
        return rows.ToDictionary(x => (x.ZoneType, x.ZoneId, x.Name, x.Unit), x => x);
    }
}

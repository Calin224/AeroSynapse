using System.Data;
using System.Text.Json;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using AeroSynapse.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace AeroSynapse.Infrastructure.Execution;

internal sealed class BatchReader(TwinDbContext db)
{
    public async Task<BatchDetails> GetAsync(Guid id, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        return await ReadAsync(id, token);
    }

    internal async Task<BatchDetails> ReadAsync(Guid id, CancellationToken token)
    {
        var batch = await db.Batches.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token)
            ?? throw new ScenarioNotFoundException("Batch was not found.");
        var seeds = JsonSerializer.Deserialize<int[]>(batch.SeedsJson)!;
        var variants = await db.BatchVariants.AsNoTracking().Where(x => x.BatchId == id).OrderBy(x => x.Position).ToArrayAsync(token);
        var pairs = await (from replica in db.BatchReplications.AsNoTracking()
                           join run in db.Runs.AsNoTracking() on replica.RunId equals run.Id
                           where replica.BatchId == id orderby replica.SeedIndex, replica.VariantId select new { Replica = replica, Run = run }).ToArrayAsync(token);
        var replications = pairs.Select(x => new BatchReplicationDetails(x.Replica.Id, x.Replica.VariantId, x.Replica.SeedIndex,
            x.Replica.Seed, x.Run.Id, x.Replica.Status, x.Run.Status, x.Replica.Status == "completed" && x.Run.ResultsValid,
            x.Run.FailureReason, RunComparisonService.Provenance(x.Run), x.Replica.RemoteCancellationPending)).ToArray();
        var valid = replications.Where(r => r.ResultsValid).Select(r => r.RunId).ToArray();
        var rows = valid.Length == 0 ? [] : await db.Metrics.AsNoTracking().Where(m => valid.Contains(m.RunId))
            .GroupBy(m => new { m.RunId, m.ZoneId, m.Name, m.Unit })
            .Select(group => group.OrderByDescending(m => m.RecordedAtUtc).ThenBy(m => m.Id).First()).ToArrayAsync(token);
        var keys = rows.Select(m => (m.ZoneId, m.Name, m.Unit)).Distinct().OrderBy(k => k.ZoneId)
            .ThenBy(k => k.Name, StringComparer.Ordinal).ThenBy(k => k.Unit, StringComparer.Ordinal).ToArray();
        var values = rows.ToDictionary(m => (m.RunId, m.ZoneId, m.Name, m.Unit), m => (double)m.Value);
        var metrics = new List<BatchMetricStatistics>(); var comparisons = new List<PairedMetricStatistics>();
        var byVariantSeed = replications.ToDictionary(r => (r.VariantId, r.Seed));
        foreach (var key in keys.Take(100))
        {
            foreach (var variant in variants)
            {
                var samples = replications.Where(r => r.VariantId == variant.Id && r.ResultsValid)
                    .Select(r => values.TryGetValue((r.RunId, key.ZoneId, key.Name, key.Unit), out var value) ? (double?)value : null)
                    .Where(v => v.HasValue).Select(v => v!.Value).ToArray();
                metrics.Add(new(variant.Id, key.ZoneId, key.Name, key.Unit, MonteCarloStatistics.Describe(samples), seeds.Length - samples.Length));
                if (variant.Position == 0) continue;
                var deltas = new List<double>(); var included = new List<int>(); var excluded = new List<ExcludedPair>();
                foreach (var seed in seeds)
                {
                    var baseline = byVariantSeed[(variants[0].Id, seed)]; var candidate = byVariantSeed[(variant.Id, seed)];
                    string? reason = !baseline.ResultsValid ? "baseline_" + baseline.Status : !candidate.ResultsValid ? "candidate_" + candidate.Status : null;
                    if (reason is null && (baseline.Provenance.EngineBuild is null || candidate.Provenance.EngineBuild is null)) reason = "engine_build_unknown";
                    if (reason is null && (baseline.Provenance.EngineVersion != candidate.Provenance.EngineVersion || baseline.Provenance.EngineBuild != candidate.Provenance.EngineBuild)) reason = "engine_version_mismatch";
                    var hasBaseline = values.TryGetValue((baseline.RunId, key.ZoneId, key.Name, key.Unit), out var a);
                    var hasCandidate = values.TryGetValue((candidate.RunId, key.ZoneId, key.Name, key.Unit), out var b);
                    if (reason is null && (!hasBaseline || !hasCandidate)) reason = "metric_unavailable";
                    if (reason is null) { deltas.Add(b - a); included.Add(seed); } else excluded.Add(new(seed, reason));
                }
                comparisons.Add(new(variant.Id, key.ZoneId, key.Name, key.Unit, MonteCarloStatistics.Describe(deltas), included, excluded));
            }
        }
        return new("1.0", BatchService.Summary(batch), seeds, variants.Select(v =>
        {
            var snapshot = BatchService.Snapshot(v.InputSnapshotJson);
            var engine = snapshot.Scenario.Inputs.PassengerGeneration?.Flow is not null ? "flow-1.0" : snapshot.Engine ?? "legacy-1.0";
            return new BatchVariantDetails(v.Id, v.Position, v.ScenarioId, v.ScenarioRevision, v.Name, snapshot.Layout.Version.Id, engine);
        }).ToArray(), replications, metrics, comparisons, keys.Length > 100);
    }
}

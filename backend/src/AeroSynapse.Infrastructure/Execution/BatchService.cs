using System.Text.Json;
using System.Text.Json.Nodes;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using AeroSynapse.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using MediatR;

namespace AeroSynapse.Infrastructure.Execution;

public sealed partial class BatchService(TwinDbContext db, IScenarioService scenarios, ISender sender, IStaffingPlanner staffingPlanner) : IBatchService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private async Task<ScenarioDetails> CarryDataSource(ScenarioDetails source, ScenarioDetails target, CancellationToken token)
    {
        if (source.Scenario.DataSource is null) return target;
        var provenance = JsonSerializer.Serialize(source.Scenario.DataSource, Json);
        var entity = await db.Scenarios.SingleAsync(row => row.Id == target.Scenario.Id, token);
        entity.DataSourceJson = provenance;
        await db.SaveChangesAsync(token);
        return target with { Scenario = target.Scenario with { DataSource = source.Scenario.DataSource } };
    }
    internal static bool Terminal(string status) => status is not ("pending" or "running");
    internal static BatchSummary Summary(SimulationBatch b) => new(b.Id, b.Name, b.Status, b.Revision, b.CreatedAtUtc,
        b.DeadlineAtUtc, b.StartedAtUtc, b.FinishedAtUtc, b.CancellationRequestedAtUtc.HasValue, b.TimeLimitSeconds, b.ReplayOfBatchId, b.FailureReason, b.EngineBuild);
    internal static RunInputSnapshot Snapshot(string value) => JsonSerializer.Deserialize<RunInputSnapshot>(value, Json)!;
    internal static ScenarioValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });

    public async Task<BatchDetails> CreateAsync(CreateBatchRequest request, CancellationToken token)
    {
        Validate(request.CommandId, request.Name, request.TimeLimitSeconds);
        if (request.Seeds is null || request.Seeds.Count is < 1 or > 25 || request.Seeds.Any(x => x < 0)
            || request.Seeds.Distinct().Count() != request.Seeds.Count)
            throw Invalid("seeds", "Supply 1–25 distinct explicit integer seeds between 0 and 2147483647, in replication order.");
        if (request.Baseline is null || request.Candidates is null || request.Candidates.Count > 4 || request.Candidates.Any(x => x is null))
            throw Invalid("candidates", "Supply a baseline and at most four candidate scenarios.");
        var references = new[] { request.Baseline }.Concat(request.Candidates).ToArray();
        if (references.Any(x => x.ScenarioId == Guid.Empty || x.Revision <= 0) || references.Select(x => x.ScenarioId).Distinct().Count() != references.Length)
            throw Invalid("scenarios", "Scenario IDs must be non-empty and distinct, with current positive revisions.");
        var identity = JsonSerializer.Serialize(request, Json);
        await using (var transaction = await db.Database.BeginTransactionAsync(token))
        {
            if (!await Exists(request.CommandId, identity, token))
            {
                await LockScenarios(references.Select(x => x.ScenarioId), token);
                var now = DateTime.UtcNow;
                var snapshots = new List<RunInputSnapshot>();
                foreach (var reference in references)
                {
                    var value = await scenarios.GetAsync(reference.ScenarioId, token) ?? throw new ScenarioNotFoundException("Scenario was not found.");
                    if (value.Scenario.DeletedAtUtc.HasValue) throw new ScenarioConflictException("Archived scenarios cannot create batches.");
                    if (value.Scenario.Revision != reference.Revision) throw new ScenarioConflictException("A scenario has changed. Reload before starting the batch.");
                    value = value with { Inputs = FlightScheduling.Resolve(value.Inputs) };
                    var layout = await scenarios.GetLayoutAsync(value.Scenario.SceneId, value.Scenario.LayoutVersionId, token)
                        ?? throw new ScenarioNotFoundException("Captured layout was not found.");
                    snapshots.Add(new(1, now, value, layout, KpiConventions.Engine, value.Inputs.SnapshotIntervalMinutes ?? 1));
                }
                Add(request.CommandId, request.Name, request.TimeLimitSeconds, request.Seeds, snapshots, identity, null, now);
                await db.SaveChangesAsync(token);
            }
            await transaction.CommitAsync(token);
        }
        return await GetAsync(request.CommandId, token);
    }

    public async Task<BatchDetails> ReplayAsync(Guid batchId, ReplayBatchRequest request, CancellationToken token)
    {
        Validate(request.CommandId, request.Name, request.TimeLimitSeconds);
        var identity = JsonSerializer.Serialize(new { replayOfBatchId = batchId, request.CommandId, request.Name, request.TimeLimitSeconds }, Json);
        await using (var transaction = await db.Database.BeginTransactionAsync(token))
        {
            if (!await Exists(request.CommandId, identity, token))
            {
                var source = await db.Batches.AsNoTracking().SingleOrDefaultAsync(x => x.Id == batchId, token)
                    ?? throw new ScenarioNotFoundException("Source batch was not found.");
                var variants = await db.BatchVariants.AsNoTracking().Where(x => x.BatchId == batchId).OrderBy(x => x.Position).ToArrayAsync(token);
                var scenarioIds = variants.Select(x => x.ScenarioId).ToArray();
                await LockScenarios(scenarioIds, token);
                if (await db.Scenarios.AnyAsync(x => scenarioIds.Contains(x.Id) && x.DeletedAtUtc != null, token))
                    throw new ScenarioConflictException("Archived scenarios cannot create replays; the original batch remains retained.");
                Add(request.CommandId, request.Name, request.TimeLimitSeconds, JsonSerializer.Deserialize<int[]>(source.SeedsJson)!,
                    variants.Select(v => Snapshot(v.InputSnapshotJson)).ToArray(), identity, batchId, DateTime.UtcNow);
                await db.SaveChangesAsync(token);
            }
            await transaction.CommitAsync(token);
        }
        return await GetAsync(request.CommandId, token);
    }

    private void Add(Guid id, string name, int limit, IReadOnlyList<int> seeds, IReadOnlyList<RunInputSnapshot> snapshots,
        string identity, Guid? replayOf, DateTime now)
    {
        var requests = snapshots.Select(snapshot => CapturedSimulation.Request(Guid.NewGuid(), snapshot)).ToArray();
        if (requests.Any(r => r.StartsAtUtc != requests[0].StartsAtUtc || r.DurationMinutes != requests[0].DurationMinutes || r.TimeZone != requests[0].TimeZone))
            throw Invalid("candidates", "All variants must use the same UTC start, time zone and simulated horizon.");
        if (requests.Select(RunFingerprint.Engine).Distinct().Count() != 1)
            throw Invalid("candidates", "Paired comparison requires the same effective engine version for every variant.");
        if (requests.Sum(r => (long)r.PassengerCount) * seeds.Count > 2_000_000)
            throw Invalid("seeds", "The batch may model at most 2000000 passenger-replications.");
        var samples = requests.Sum(r => (long)r.Checkpoints.Count * (r.Engine == KpiConventions.Engine && r.PassengerGeneration?.Flow is null
            ? KpiConventions.Minutes(r.DurationMinutes, r.SnapshotIntervalMinutes!.Value).Length : 1));
        if (samples * seeds.Count > 100_000) throw Invalid("seeds", "The batch may capture at most 100000 zone snapshots; reduce replications or snapshot frequency.");
        db.Batches.Add(new SimulationBatch { Id = id, IsSynthetic = true, Name = name.Trim(), TimeLimitSeconds = limit,
            CreatedAtUtc = now, DeadlineAtUtc = now.AddSeconds(limit), RequestJson = identity, SeedsJson = JsonSerializer.Serialize(seeds), ReplayOfBatchId = replayOf });
        foreach (var (snapshot, position) in snapshots.Select((value, index) => (value, index)))
        {
            var variant = new BatchVariant { Id = Guid.NewGuid(), IsSynthetic = true, BatchId = id, Position = position,
                ScenarioId = snapshot.Scenario.Scenario.Id, ScenarioRevision = snapshot.Scenario.Scenario.Revision,
                Name = snapshot.Scenario.Scenario.Name, InputSnapshotJson = JsonSerializer.Serialize(snapshot, Json) };
            db.BatchVariants.Add(variant);
            foreach (var (seed, index) in seeds.Select((value, index) => (value, index)))
            {
                var captured = snapshot with { Scenario = snapshot.Scenario with { Inputs = snapshot.Scenario.Inputs with { Seed = seed } } };
                var runId = Guid.NewGuid(); var request = CapturedSimulation.Request(runId, captured);
                db.Runs.Add(new SimulationRun { Id = runId, SceneId = snapshot.Scenario.Scenario.SceneId, ScenarioId = variant.ScenarioId,
                    ScenarioRevision = variant.ScenarioRevision, IsSynthetic = true, StartedAtUtc = now, Seed = seed,
                    InputSnapshotJson = JsonSerializer.Serialize(captured, Json), InputHash = RunFingerprint.Input(request), EngineVersion = RunFingerprint.Engine(request),
                    ParametersJson = JsonSerializer.Serialize(new { duration_minutes = request.DurationMinutes, passenger_count = request.PassengerCount, seed }) });
                db.BatchReplications.Add(new BatchReplication { Id = Guid.NewGuid(), IsSynthetic = true, BatchId = id, VariantId = variant.Id,
                    RunId = runId, Seed = seed, SeedIndex = index });
            }
        }
    }

    private async Task<bool> Exists(Guid id, string identity, CancellationToken token)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({id.ToString()}, 26001))", token);
        var previous = await db.Batches.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
        if (previous is null) return false;
        var stored = JsonNode.Parse(previous.RequestJson)!;
        if (stored["kind"]?.GetValue<string>() == "staffing-evaluation/v1") stored = stored["command"]!;
        if (!JsonNode.DeepEquals(stored, JsonNode.Parse(identity)))
            throw new ScenarioConflictException("CommandId already identifies a different batch request.");
        return true;
    }
    private async Task LockScenarios(IEnumerable<Guid> ids, CancellationToken token)
    {
        foreach (var id in ids.Distinct().Order())
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM twin.scenarios WHERE \"Id\" = {id} FOR UPDATE", token);
    }
    private static void Validate(Guid id, string name, int limit)
    {
        if (id == Guid.Empty) throw Invalid("commandId", "A non-empty UUID is required for idempotency.");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 160 || name.Contains('\0')) throw Invalid("name", "Supply 1–160 characters without NUL.");
        if (limit is < 1 or > 3600) throw Invalid("timeLimitSeconds", "Supply a wall-clock limit of 1–3600 seconds, including queue time.");
    }

    public Task<BatchDetails> GetAsync(Guid batchId, CancellationToken token) => new BatchReader(db).GetAsync(batchId, token);
    public async Task<IReadOnlyList<BatchSummary>> ListAsync(Guid? scenarioId, CancellationToken token)
    {
        var values = await db.Batches.AsNoTracking().Where(b => !scenarioId.HasValue || db.BatchVariants.Any(v => v.BatchId == b.Id && v.ScenarioId == scenarioId))
            .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.Id).Take(50).ToArrayAsync(token);
        return values.Select(Summary).ToArray();
    }
    public async Task<BatchDetails> CancelAsync(Guid batchId, CancellationToken token)
    {
        await using (var transaction = await db.Database.BeginTransactionAsync(token))
        {
            var batch = await db.Batches.FromSqlInterpolated($"SELECT * FROM twin.simulation_batches WHERE \"Id\" = {batchId} FOR UPDATE").SingleOrDefaultAsync(token)
                ?? throw new ScenarioNotFoundException("Batch was not found.");
            if (!Terminal(batch.Status) && !batch.CancellationRequestedAtUtc.HasValue)
            {
                batch.CancellationRequestedAtUtc = DateTime.UtcNow; batch.Revision++;
                await db.SaveChangesAsync(token);
            }
            await transaction.CommitAsync(token);
        }
        return await GetAsync(batchId, token);
    }
}

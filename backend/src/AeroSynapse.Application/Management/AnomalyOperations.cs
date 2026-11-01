using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Simulation;
using AeroSynapse.Application.Storage;
using MediatR;

namespace AeroSynapse.Application.Management;

// Screen a completed run against the Monte Carlo replications of a batch baseline.
public sealed record AnomalyRequest(Guid BatchId, AnomalySettings Settings);
public sealed record AnomalyReviewRequest(Guid CommandId, Guid BatchId, AnomalySettings Settings, Guid CheckpointId, int OnsetMinute,
    string Decision, string Actor, string Reason = "");
public sealed record AnomalyReviewDetails(Guid Id, Guid RunId, Guid BatchId, string SettingsKey, AnomalySettings Settings,
    string DetectorVersion, Guid CheckpointId, int OnsetMinute, string Decision, string Actor, string Reason, DateTime RecordedAtUtc);
public sealed record AnomalyExcludedRun(Guid RunId, int Seed, string Reason);
public sealed record AnomalyReport(string ContractVersion, Guid RunId, Guid BatchId, string BatchName, Guid BaselineScenarioId,
    int BaselineScenarioRevision, string SettingsKey, AnomalyDetectionResult Detection, IReadOnlyList<AnomalyExcludedRun> Excluded,
    IReadOnlyList<AnomalyReviewDetails> Reviews, bool IsSynthetic = true);
public sealed record AnomalyReviewRecord(Guid CommandId, Guid SceneId, Guid RunId, Guid BatchId, string SettingsKey, AnomalySettings Settings,
    string DetectorVersion, Guid CheckpointId, int OnsetMinute, string Decision, string Actor, string Reason, AnomalyEpisode Episode);

public sealed record DetectAnomaliesQuery(Guid RunId, AnomalyRequest Request) : IRequest<AnomalyReport>;
public sealed record ReviewAnomalyCommand(Guid RunId, AnomalyReviewRequest Request) : IRequest<AnomalyReviewDetails>;
public sealed record ListAnomalyReviewsQuery(Guid RunId) : IRequest<IReadOnlyList<AnomalyReviewDetails>>;
public sealed record EvaluateAnomaliesQuery(AnomalySettings Settings) : IRequest<JsonElement>;
public sealed record AnomalyCaseQuery(string CaseKey, AnomalySettings Settings) : IRequest<JsonElement>;

public interface IAnomalyReviewStore
{
    Task<IReadOnlyList<AnomalyReviewDetails>> ListAsync(Guid runId, CancellationToken token);
    Task<AnomalyReviewDetails> RecordAsync(AnomalyReviewRecord record, CancellationToken token);
}

public sealed class AnomalyOperations(IRunStorageService runs, IBatchService batches, ISender sender, IAnomalyDetector detector,
    IAnomalyReviewStore reviews) : IRequestHandler<DetectAnomaliesQuery, AnomalyReport>, IRequestHandler<ReviewAnomalyCommand, AnomalyReviewDetails>,
    IRequestHandler<ListAnomalyReviewsQuery, IReadOnlyList<AnomalyReviewDetails>>, IRequestHandler<EvaluateAnomaliesQuery, JsonElement>,
    IRequestHandler<AnomalyCaseQuery, JsonElement>
{
    public const int MinReplications = 5;

    public async Task<AnomalyReport> Handle(DetectAnomaliesQuery query, CancellationToken token)
    {
        var screening = await ScreenAsync(query.RunId, query.Request.BatchId, query.Request.Settings, token);
        var current = (await reviews.ListAsync(query.RunId, token))
            .Where(item => item.BatchId == query.Request.BatchId && item.SettingsKey == screening.SettingsKey).ToArray();
        return screening.Report with { Reviews = current };
    }

    // A decision is accepted only for an alert the detector actually raises under the stated settings.
    public async Task<AnomalyReviewDetails> Handle(ReviewAnomalyCommand command, CancellationToken token)
    {
        var request = command.Request;
        if (request.CommandId == Guid.Empty) throw Invalid("commandId", "A nonempty command ID is required.");
        if (request.Decision is not ("confirmed" or "rejected")) throw Invalid("decision", "Use confirmed or rejected.");
        if (string.IsNullOrWhiteSpace(request.Actor) || request.Actor.Trim().Length > 160 || request.Actor.Contains('\0'))
            throw Invalid("actor", "Supply an analyst name of 1–160 characters.");
        if (request.Reason is null || request.Reason.Length > 2000 || request.Reason.Contains('\0'))
            throw Invalid("reason", "Reason must contain at most 2000 characters without NUL.");
        var screening = await ScreenAsync(command.RunId, request.BatchId, request.Settings, token);
        var episode = screening.Report.Detection.Checkpoints.SingleOrDefault(row => row.CheckpointId == request.CheckpointId)?
            .Episodes.SingleOrDefault(item => item.OnsetMinute == request.OnsetMinute)
            ?? throw new ScenarioConflictException("No alert starts at this minute for this checkpoint under the stated settings.");
        return await reviews.RecordAsync(new(request.CommandId, screening.SceneId, command.RunId, request.BatchId, screening.SettingsKey,
            screening.Report.Detection.Settings, screening.Report.Detection.DetectorVersion, request.CheckpointId, request.OnsetMinute,
            request.Decision, request.Actor.Trim(), request.Reason.Trim(), episode), token);
    }

    public async Task<IReadOnlyList<AnomalyReviewDetails>> Handle(ListAnomalyReviewsQuery query, CancellationToken token)
    {
        _ = await runs.GetAsync(query.RunId, token) ?? throw new ScenarioNotFoundException("Run was not found.");
        return await reviews.ListAsync(query.RunId, token);
    }

    public async Task<JsonElement> Handle(EvaluateAnomaliesQuery query, CancellationToken token) =>
        await detector.EvaluateAsync(Read(query.Settings, 1), token);

    public async Task<JsonElement> Handle(AnomalyCaseQuery query, CancellationToken token) =>
        await detector.CaseAsync(Read(query.Settings, 1), query.CaseKey, token)
            ?? throw new ScenarioNotFoundException("Synthetic test case was not found.");

    public static string SettingsKey(AnomalySettings settings) =>
        RunFingerprint.Hash(JsonSerializer.SerializeToElement(settings, ContractJson.Options), "anomaly-settings");

    private sealed record Screening(AnomalyReport Report, string SettingsKey, Guid SceneId);

    private async Task<Screening> ScreenAsync(Guid runId, Guid batchId, AnomalySettings value, CancellationToken token)
    {
        var settings = ContractJson.Read<AnomalySettings>(JsonSerializer.SerializeToElement(value, ContractJson.Options), "AnomalySettings");
        var run = await runs.GetAsync(runId, token) ?? throw new ScenarioNotFoundException("Run was not found.");
        if (!run.ResultsValid) throw new ScenarioConflictException("Anomaly screening needs a completed run with valid results.");
        if (!run.IsSynthetic) throw new ScenarioConflictException("Anomaly screening is limited to synthetic demonstration runs.");
        if (run.InputSnapshotJson is null) throw new ScenarioNotFoundException("This historical run has no captured inputs.");
        var captured = JsonSerializer.Deserialize<RunInputSnapshot>(run.InputSnapshotJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        if (captured.Engine != KpiConventions.Engine || captured.Scenario.Inputs.PassengerGeneration?.Flow is not null)
            throw new ScenarioConflictException("Anomaly screening requires captured KPI data without alternative passenger flows.");
        var simulation = CapturedSimulation.Request(run.Id, captured);
        var batch = await batches.GetAsync(batchId, token);
        if (!batch.IsSynthetic) throw new ScenarioConflictException("The normal pattern must come from a synthetic batch.");
        var baseline = batch.Variants.Single(item => item.Position == 0);
        var replications = batch.Replications.Where(item => item.VariantId == baseline.Id).OrderBy(item => item.SeedIndex).ToArray();
        var excluded = replications.Where(item => item.RunId == run.Id || !item.ResultsValid)
            .Select(item => new AnomalyExcludedRun(item.RunId, item.Seed, item.RunId == run.Id ? "observed_run" : item.Status)).ToArray();
        var included = replications.Where(item => item.RunId != run.Id && item.ResultsValid).ToArray();
        if (included.Length < MinReplications)
            throw new ScenarioConflictException($"The baseline needs at least {MinReplications} completed replications; this batch has {included.Length} usable.");

        var observed = await KpisAsync(run.Id, token);
        var grid = KpiConventions.Minutes(simulation.DurationMinutes, observed.IntervalMinutes);
        var keys = simulation.Checkpoints.Select(cp => cp.CheckpointId).Order().ToArray();
        var series = new Dictionary<Guid, Dictionary<Guid, AnomalySeries>> { [run.Id] = Series(observed, grid, keys) ?? throw Incomparable(run.Id) };
        foreach (var item in included)
        {
            var kpis = await KpisAsync(item.RunId, token);
            series[item.RunId] = (kpis.IntervalMinutes == observed.IntervalMinutes ? Series(kpis, grid, keys) : null) ?? throw Incomparable(item.RunId);
        }
        var request = new AnomalyDetectionRequest("1.0", "aggregate_synthetic", run.Id, observed.IntervalMinutes, grid, settings, simulation,
            included.Select(item => new AnomalyReplicationRun(item.RunId, item.Seed)).ToArray(),
            keys.Select(key => new AnomalyCheckpointInput(key, series[run.Id][key], included.Select(item => series[item.RunId][key]).ToArray())).ToArray());
        request = ContractJson.Read<AnomalyDetectionRequest>(JsonSerializer.SerializeToElement(request, ContractJson.Options), "AnomalyDetectionRequest");
        var result = await detector.DetectAsync(request, token);
        var key = SettingsKey(settings);
        return new(new("1.0", run.Id, batch.Batch.Id, batch.Batch.Name, baseline.ScenarioId, baseline.ScenarioRevision, key, result, excluded, []),
            key, run.SceneId);
    }

    private async Task<KpiResult> KpisAsync(Guid runId, CancellationToken token) =>
        await sender.Send(new GetRunKpisQuery(runId, Limit: KpiConventions.MaxSnapshots), token);

    // Aggregate counts per checkpoint on the captured grid; null when the run is not comparable.
    private static Dictionary<Guid, AnomalySeries>? Series(KpiResult kpis, IReadOnlyList<int> grid, IReadOnlyList<Guid> keys)
    {
        var rows = kpis.Snapshots.GroupBy(row => row.ZoneId).ToDictionary(group => group.Key, group => group.OrderBy(row => row.Minute).ToArray());
        if (kpis.HasMore || !rows.Keys.ToHashSet().SetEquals(keys) || rows.Values.Any(items => !items.Select(row => row.Minute).SequenceEqual(grid)))
            return null;
        return keys.ToDictionary(key => key, key => new AnomalySeries(rows[key].Select(row => row.QueuedPassengers).ToArray(),
            // Cumulative arrivals: people moved on to another control's queue still arrived here.
            rows[key].Select(row => row.ArrivedPassengers + (row.TransferredOutPassengers ?? 0)).ToArray(), rows[key].Select(row => row.EffectiveCapacity ?? row.Capacity).ToArray()));
    }

    private static AnomalySettings Read(AnomalySettings settings, int interval)
    {
        var value = ContractJson.Read<AnomalySettings>(JsonSerializer.SerializeToElement(settings, ContractJson.Options), "AnomalySettings");
        var errors = new List<ContractFieldError>();
        AnomalyValidation.Settings(value, interval, "", errors);
        return errors.Count == 0 ? value : throw new ContractValidationException(errors);
    }

    private static ScenarioConflictException Incomparable(Guid runId) =>
        new($"Run {runId} is not comparable with the screened run: horizon, KPI cadence or checkpoints differ.");
    private static ScenarioValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });
}

namespace AeroSynapse.Application.Management;

public sealed record BatchScenarioReference(Guid ScenarioId, int Revision);
public sealed record CreateBatchRequest(Guid CommandId, string Name, BatchScenarioReference Baseline,
    IReadOnlyList<BatchScenarioReference> Candidates, IReadOnlyList<int> Seeds, int TimeLimitSeconds = 60);
public sealed record ReplayBatchRequest(Guid CommandId, string Name, int TimeLimitSeconds = 60);
// CheckpointId names the post to restaff: a security checkpoint, or the scenario's check-in area.
public sealed record CreateWhatIfRequest(Guid CommandId, int Revision, string Name, Guid CheckpointId,
    int StaffCount, int TimeLimitSeconds = 60);
public sealed record BatchSummary(Guid Id, string Name, string Status, int Revision, DateTime CreatedAtUtc,
    DateTime DeadlineAtUtc, DateTime? StartedAtUtc, DateTime? FinishedAtUtc, bool CancellationRequested,
    int TimeLimitSeconds, Guid? ReplayOfBatchId, string? FailureReason, string? EngineBuild = null);
public sealed record BatchVariantDetails(Guid Id, int Position, Guid ScenarioId, int ScenarioRevision,
    string Name, Guid LayoutVersionId, string EngineVersion);
public sealed record BatchReplicationDetails(Guid Id, Guid VariantId, int SeedIndex, int Seed, Guid RunId,
    string Status, string RunStatus, bool ResultsValid, string? FailureReason, RunProvenance Provenance, bool RemoteCancellationPending);
public sealed record SampleStatistics(int Count, double? Mean, double? StandardDeviation, double? Minimum,
    double? Maximum, double? Median, double? P05, double? P95);
public sealed record BatchMetricStatistics(Guid VariantId, Guid ZoneId, string Name, string Unit,
    SampleStatistics Values, int MissingOrUnsuccessfulCount);
public sealed record ExcludedPair(int Seed, string Reason);
public sealed record PairedMetricStatistics(Guid CandidateVariantId, Guid ZoneId, string Name, string Unit,
    SampleStatistics Differences, IReadOnlyList<int> IncludedSeeds, IReadOnlyList<ExcludedPair> ExcludedSeeds);
public sealed record BatchDetails(string ContractVersion, BatchSummary Batch, IReadOnlyList<int> Seeds,
    IReadOnlyList<BatchVariantDetails> Variants, IReadOnlyList<BatchReplicationDetails> Replications,
    IReadOnlyList<BatchMetricStatistics> Metrics, IReadOnlyList<PairedMetricStatistics> Comparisons,
    bool MetricsTruncated, bool IsSynthetic = true);

public interface IBatchService
{
    Task<StaffingEvaluationDetails> CreateStaffingEvaluationAsync(Guid scenarioId, CreateStaffingEvaluationRequest request, CancellationToken token);
    Task<StaffingEvaluationDetails> GetStaffingEvaluationAsync(Guid batchId, CancellationToken token);
    Task<IReadOnlyList<BatchSummary>> ListStaffingEvaluationsAsync(Guid scenarioId, CancellationToken token);
    Task<BatchDetails> CreateWhatIfAsync(Guid scenarioId, CreateWhatIfRequest request, CancellationToken token);
    Task<BatchDetails> CreateAsync(CreateBatchRequest request, CancellationToken token);
    Task<BatchDetails> ReplayAsync(Guid batchId, ReplayBatchRequest request, CancellationToken token);
    Task<BatchDetails> GetAsync(Guid batchId, CancellationToken token);
    Task<IReadOnlyList<BatchSummary>> ListAsync(Guid? scenarioId, CancellationToken token);
    Task<BatchDetails> CancelAsync(Guid batchId, CancellationToken token);
}

public static class MonteCarloStatistics
{
    // Descriptive statistics across replications. No normality assumption or fabricated confidence interval.
    public static SampleStatistics Describe(IEnumerable<double> source)
    {
        var values = source.Order().ToArray();
        if (values.Any(value => !double.IsFinite(value))) throw new ArgumentException("Samples must be finite.");
        if (values.Length == 0) return new(0, null, null, null, null, null, null, null);
        double mean = 0, m2 = 0; var count = 0;
        foreach (var value in values) { count++; var delta = value - mean; mean += delta / count; m2 += delta * (value - mean); }
        double Quantile(double probability) => values[Math.Max(0, (int)Math.Ceiling(probability * count) - 1)];
        return new(count, mean, count < 2 ? null : Math.Sqrt(Math.Max(0, m2 / (count - 1))), values[0], values[^1],
            values[count / 2] / 2 + values[(count - 1) / 2] / 2, Quantile(.05), Quantile(.95));
    }
}

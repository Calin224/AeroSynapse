using System.Text.Json;

namespace AeroSynapse.Application.Contracts;

// Aggregate synthetic counts per checkpoint only; no passenger or person enters these contracts.
public sealed record AnomalySettings(double UpperQuantile, int WindowMinutes, int MinExceedances, double MinExcessPassengers);
public sealed record AnomalySeries(IReadOnlyList<int> Queued, IReadOnlyList<int> Arrived, IReadOnlyList<int> Staff);
public sealed record AnomalyReplicationRun(Guid RunId, int Seed);
public sealed record AnomalyCheckpointInput(Guid CheckpointId, AnomalySeries Observed, IReadOnlyList<AnomalySeries> Replications);
public sealed record AnomalyDetectionRequest(string ContractVersion, string DataScope, Guid RunId, int IntervalMinutes,
    IReadOnlyList<int> Minutes, AnomalySettings Settings, SimulationRequest Simulation,
    IReadOnlyList<AnomalyReplicationRun> ReplicationRuns, IReadOnlyList<AnomalyCheckpointInput> Checkpoints);
public sealed record AnomalyContributions(double Demand, double Capacity, double Backlog);
public sealed record AnomalyExplanationInputs(double ArrivalsObserved, double ArrivalsExpected, double StaffMinutesObserved,
    double StaffMinutesExpected, double BacklogObserved, double BacklogExpected, double ServiceRatePerStaffMinute);
public sealed record AnomalyExplanation(int LookbackStartMinute, AnomalyContributions Contributions, double Explained,
    double ObservedExcess, double Residual, AnomalyExplanationInputs Inputs);
public sealed record AnomalyEpisode(int OnsetMinute, int TriggerMinute, int EndMinute, int PeakMinute, double PeakObserved,
    double PeakMedian, double PeakUpper, double MinPValue, int ExceedingSamples, AnomalyExplanation? Explanation);
public sealed record AnomalyCheckpointResult(Guid CheckpointId, double ServiceRatePerStaffMinute, IReadOnlyList<double> Observed,
    IReadOnlyList<double> Median, IReadOnlyList<double> Lower, IReadOnlyList<double> Upper, IReadOnlyList<double> PValue,
    IReadOnlyList<bool> Exceeds, IReadOnlyList<AnomalyEpisode> Episodes);
public sealed record AnomalyNullCalibration(int ReplicationCount, int ReplicationsWithAlerts, int Alerts, double AlertsPerReplicationHour, string Method);
public sealed record AnomalyDetectionResult(string ContractVersion, Guid RunId, string Kind, string DataScope, string DetectorVersion,
    AnomalySettings Settings, int IntervalMinutes, IReadOnlyList<int> Minutes, IReadOnlyList<AnomalyReplicationRun> ReplicationRuns,
    int ReplicationCount, double MinimumPValue, int WindowSamples, IReadOnlyList<string> Assumptions,
    AnomalyNullCalibration NullCalibration, IReadOnlyList<AnomalyCheckpointResult> Checkpoints);

public interface IAnomalyDetector
{
    Task<AnomalyDetectionResult> DetectAsync(AnomalyDetectionRequest request, CancellationToken token);
    Task<JsonElement> EvaluateAsync(AnomalySettings settings, CancellationToken token);
    Task<JsonElement?> CaseAsync(AnomalySettings settings, string caseKey, CancellationToken token);
}
public sealed class AnomalyDetectorUnavailableException() : Exception("Anomaly screening is unavailable. Check the intelligence service and retry.");

// Mirrors intelligence/app/anomaly; the backend re-derives every statistic it can instead of trusting the service.
public static class AnomalyValidation
{
    public const double Tolerance = 1e-6;

    public static int WindowSamples(AnomalySettings settings, int interval) => Math.Max(1, settings.WindowMinutes / interval);

    public static void Settings(AnomalySettings settings, int interval, string path, List<ContractFieldError> errors)
    {
        if (settings.MinExceedances > WindowSamples(settings, interval))
            errors.Add(VersionedContract.Error(path + "/settings/minExceedances", "out_of_range"));
    }

    public static void Request(AnomalyDetectionRequest value, string path, List<ContractFieldError> errors)
    {
        var simulation = value.Simulation;
        errors.AddRange(VersionedContract.Validate("KpiSimulationRequest", JsonSerializer.SerializeToElement(simulation, ContractJson.Options))
            .Select(error => error with { Field = path + "/simulation" + error.Field }));
        Settings(value.Settings, value.IntervalMinutes, path, errors);
        if (simulation.RunId != value.RunId || simulation.SnapshotIntervalMinutes != value.IntervalMinutes)
            errors.Add(VersionedContract.Error(path + "/simulation", "invalid_value"));
        if (!value.Minutes.SequenceEqual(KpiConventions.Minutes(simulation.DurationMinutes, value.IntervalMinutes)))
            errors.Add(VersionedContract.Error(path + "/minutes", "invalid_value"));
        var runs = value.ReplicationRuns;
        if (runs.Select(run => run.RunId).Distinct().Count() != runs.Count || runs.Select(run => run.Seed).Distinct().Count() != runs.Count
            || runs.Any(run => run.RunId == value.RunId))
            errors.Add(VersionedContract.Error(path + "/replicationRuns", "duplicate_id"));
        var keys = value.Checkpoints.Select(row => row.CheckpointId).ToArray();
        if (keys.Distinct().Count() != keys.Length || !keys.ToHashSet().SetEquals(simulation.Checkpoints.Select(cp => cp.CheckpointId)))
            errors.Add(VersionedContract.Error(path + "/checkpoints", "invalid_value"));
        foreach (var (row, index) in value.Checkpoints.Select((row, index) => (row, index)))
        {
            var field = $"{path}/checkpoints/{index}";
            if (row.Replications.Count != runs.Count) errors.Add(VersionedContract.Error(field + "/replications", "invalid_value"));
            if (new[] { row.Observed }.Concat(row.Replications).Any(series =>
                    series.Queued.Count != value.Minutes.Count || series.Arrived.Count != value.Minutes.Count || series.Staff.Count != value.Minutes.Count
                    || series.Arrived.Zip(series.Arrived.Skip(1)).Any(pair => pair.Second < pair.First)))
                errors.Add(VersionedContract.Error(field, "invalid_value"));
        }
    }

    public static bool ValidResult(AnomalyDetectionResult value)
    {
        var width = WindowSamples(value.Settings, value.IntervalMinutes);
        if (value.ReplicationCount != value.ReplicationRuns.Count || Math.Abs(value.MinimumPValue - 1d / (value.ReplicationCount + 1)) > 1e-12
            || value.WindowSamples != width || value.Settings.MinExceedances > width
            || value.NullCalibration.ReplicationCount != value.ReplicationCount || value.NullCalibration.ReplicationsWithAlerts > value.ReplicationCount
            || value.Minutes.Zip(value.Minutes.Skip(1)).Any(pair => pair.Second <= pair.First)
            || value.Checkpoints.Select(row => row.CheckpointId).Distinct().Count() != value.Checkpoints.Count) return false;
        foreach (var row in value.Checkpoints)
        {
            if (new[] { row.Observed.Count, row.Median.Count, row.Lower.Count, row.Upper.Count, row.PValue.Count, row.Exceeds.Count }.Any(count => count != value.Minutes.Count))
                return false;
            foreach (var episode in row.Episodes)
                if (episode.Explanation is { } explanation && (
                        Math.Abs(explanation.Contributions.Demand + explanation.Contributions.Capacity + explanation.Contributions.Backlog - explanation.Explained) > Tolerance
                        || Math.Abs(explanation.ObservedExcess - explanation.Explained - explanation.Residual) > Tolerance
                        || explanation.LookbackStartMinute > episode.PeakMinute)) return false;
        }
        return true;
    }

    // Synthetic test-set reports (FP/FN inspection) stay JSON passthrough; their counts must still add up.
    private static readonly string[] Envelope = ["status", "contractVersion", "dataScope"];
    private static readonly Dictionary<string, string[]> Ready = new()
    {
        ["AnomalyEvaluationResult"] = ["testSetVersion", "testSetHash", "settings", "windowSamples", "replicationsPerScenario", "scenarios",
            "confusion", "recall", "falsePositiveRate", "precision", "medianDelayMinutes", "preOnsetAlerts", "byKind", "byImpact", "nullCheck", "sweep", "cases"],
        ["AnomalyCaseResult"] = ["caseKey", "label", "outcome", "delayMinutes", "preOnsetAlerts", "detection"],
    };

    // A ready report carries every field; a warming or unavailable one carries only its status (mirrors contract.py).
    public static void Report(string definition, JsonElement value, string path, List<ContractFieldError> errors)
    {
        var names = value.EnumerateObject().Select(item => item.Name).ToHashSet();
        if (value.GetProperty("status").GetString() != "ready")
        {
            if (names.Except(Envelope).Any()) errors.Add(VersionedContract.Error(path, "invalid_result"));
            return;
        }
        var missing = Ready[definition].Where(name => !names.Contains(name)).ToArray();
        errors.AddRange(missing.Select(name => VersionedContract.Error(path + "/" + name, "required")));
        if (missing.Length > 0) return;
        static bool Negative(JsonElement item) => item.GetProperty("label").GetProperty("kind").GetString() == "normal";
        static bool Rejection(JsonElement item) => item.GetProperty("outcome").GetString() is "false_positive" or "true_negative";
        if (definition == "AnomalyCaseResult")
        {
            if (Negative(value) != Rejection(value)
                || !ValidResult(value.GetProperty("detection").Deserialize<AnomalyDetectionResult>(ContractJson.Options)!))
                errors.Add(VersionedContract.Error(path, "invalid_result"));
            return;
        }
        var settings = value.GetProperty("settings").Deserialize<AnomalySettings>(ContractJson.Options)!;
        Settings(settings, 1, path, errors);
        var cases = value.GetProperty("cases").EnumerateArray().ToArray();
        var confusion = value.GetProperty("confusion").EnumerateObject().ToArray();
        if (confusion.Sum(item => item.Value.GetInt32()) != cases.Length || value.GetProperty("windowSamples").GetInt32() != WindowSamples(settings, 1)
            || confusion.Any(item => cases.Count(entry => entry.GetProperty("outcome").GetString() == item.Name) != item.Value.GetInt32())
            || cases.Any(entry => Negative(entry) != Rejection(entry)))
            errors.Add(VersionedContract.Error(path, "invalid_result"));
    }

    // Conventions: nearest rank ceil(p·n) and the mean of the two central values.
    private static double NearestRank(double[] sorted, double probability) => sorted[Math.Max(0, (int)Math.Ceiling(probability * sorted.Length) - 1)];
    private static double Median(double[] sorted) => sorted[sorted.Length / 2] / 2 + sorted[(sorted.Length - 1) / 2] / 2;

    public sealed record Screen(double[] Median, double[] Lower, double[] Upper, double[] PValue, bool[] Exceeds,
        IReadOnlyList<(int Onset, int Trigger, int End, int Peak)> Episodes);

    public static Screen Compute(IReadOnlyList<int> observed, IReadOnlyList<IReadOnlyList<int>> replications, AnomalySettings settings, int interval)
    {
        var count = observed.Count;
        double[] median = new double[count], lower = new double[count], upper = new double[count], pValue = new double[count];
        var exceeds = new bool[count];
        for (var index = 0; index < count; index++)
        {
            var column = replications.Select(rep => (double)rep[index]).Order().ToArray();
            median[index] = Median(column); lower[index] = NearestRank(column, 1 - settings.UpperQuantile); upper[index] = NearestRank(column, settings.UpperQuantile);
            pValue[index] = (1d + column.Count(value => value >= observed[index])) / (column.Length + 1);
            exceeds[index] = observed[index] > upper[index] && observed[index] - median[index] >= settings.MinExcessPassengers;
        }
        var width = WindowSamples(settings, interval);
        var triggered = Enumerable.Range(0, count).Select(index => exceeds[Math.Max(0, index - width + 1)..(index + 1)].Count(item => item) >= settings.MinExceedances).ToArray();
        var episodes = new List<(int, int, int, int)>();
        for (var index = 0; index < count; index++)
        {
            if (!triggered[index]) continue;
            var start = index;
            while (index + 1 < count && triggered[index + 1]) index++;
            var onset = Enumerable.Range(Math.Max(0, start - width + 1), start - Math.Max(0, start - width + 1) + 1).First(position => exceeds[position]);
            var peak = onset;
            for (var position = onset + 1; position <= index; position++)
                if (observed[position] - median[position] > observed[peak] - median[peak]) peak = position;
            episodes.Add((onset, start, index, peak));
        }
        return new(median, lower, upper, pValue, exceeds, episodes);
    }

    public static AnomalyNullCalibration NullCalibration(AnomalyDetectionRequest request)
    {
        var count = request.ReplicationRuns.Count;
        int alerted = 0, alerts = 0;
        for (var held = 0; held < count; held++)
        {
            var found = request.Checkpoints.Sum(row => Compute(row.Replications[held].Queued,
                row.Replications.Where((_, index) => index != held).Select(rep => rep.Queued).ToArray(), request.Settings, request.IntervalMinutes).Episodes.Count);
            alerted += found > 0 ? 1 : 0;
            alerts += found;
        }
        var hours = (request.Minutes[^1] - request.Minutes[0]) / 60d;
        return new(count, alerted, alerts, hours > 0 ? alerts / (count * hours) : 0, "leave_one_out");
    }

    // The result must be exactly what the request implies: envelope, p-values, exceedances, episodes and calibration.
    public static bool Correlates(AnomalyDetectionRequest request, AnomalyDetectionResult result)
    {
        if (!ValidResult(result) || result.RunId != request.RunId || result.Settings != request.Settings || result.IntervalMinutes != request.IntervalMinutes
            || !result.Minutes.SequenceEqual(request.Minutes) || !result.ReplicationRuns.SequenceEqual(request.ReplicationRuns)
            || result.NullCalibration != NullCalibration(request)
            || !result.Checkpoints.Select(row => row.CheckpointId).SequenceEqual(request.Checkpoints.Select(row => row.CheckpointId).Order()))
            return false;
        var width = WindowSamples(request.Settings, request.IntervalMinutes);
        foreach (var row in result.Checkpoints)
        {
            var input = request.Checkpoints.Single(item => item.CheckpointId == row.CheckpointId);
            var expected = Compute(input.Observed.Queued, input.Replications.Select(rep => rep.Queued).ToArray(), request.Settings, request.IntervalMinutes);
            if (!row.Observed.SequenceEqual(input.Observed.Queued.Select(value => (double)value)) || !row.Median.SequenceEqual(expected.Median)
                || !row.Lower.SequenceEqual(expected.Lower) || !row.Upper.SequenceEqual(expected.Upper) || !row.PValue.SequenceEqual(expected.PValue)
                || !row.Exceeds.SequenceEqual(expected.Exceeds) || row.Episodes.Count != expected.Episodes.Count) return false;
            foreach (var (episode, (onset, trigger, end, peak)) in row.Episodes.Zip(expected.Episodes))
            {
                var span = Enumerable.Range(onset, end - onset + 1).ToArray();
                if (episode.OnsetMinute != request.Minutes[onset] || episode.TriggerMinute != request.Minutes[trigger] || episode.EndMinute != request.Minutes[end]
                    || episode.PeakMinute != request.Minutes[peak] || episode.PeakObserved != input.Observed.Queued[peak]
                    || episode.PeakMedian != expected.Median[peak] || episode.PeakUpper != expected.Upper[peak]
                    || episode.ExceedingSamples != span.Count(position => expected.Exceeds[position])
                    || episode.MinPValue != span.Min(position => expected.PValue[position])
                    || episode.Explanation is { } explanation && explanation.LookbackStartMinute != request.Minutes[Math.Max(0, onset - width)])
                    return false;
            }
        }
        return true;
    }
}

using System.Text.Json;

namespace AeroSynapse.Application.Contracts;

public sealed record QueueForecastOptions(int FromMinute, int HorizonMinutes, int? QueueThresholdPassengers);
public sealed record QueueForecastLiveOptions(int HorizonMinutes, int? QueueThresholdPassengers);
public sealed record QueueForecastBacklog(Guid CheckpointId, int QueuedPassengers, int NotArrivedPassengers);
public sealed record QueueForecastRequest(SimulationRequest Simulation, QueueForecastOptions Options, IReadOnlyList<QueueForecastBacklog> Backlogs,
    string ObservationSource = "captured_snapshot");
public sealed record QueueForecastPoint(int Minute, double Arrivals, double Capacity, double QueuePassengers, int AvailableStaff);
public sealed record QueueForecastAlert(int Minute, double QueuePassengers, int ThresholdPassengers);
public sealed record QueueForecastMitigation(int? ExtraStaff, double? PeakQueuePassengers);
public sealed record QueueForecastSeries(Guid CheckpointId, int InitialQueuePassengers, IReadOnlyList<QueueForecastPoint> Points, QueueForecastAlert? Alert,
    QueueForecastMitigation? Mitigation, double ServiceRatePerStaffMinute, string ServiceRateSource);
public sealed record QueueForecastResult(string ContractVersion, Guid RunId, string Kind, string Formula, int FromMinute, int ToMinute,
    int StepMinutes, DateTimeOffset PeriodStartUtc, DateTimeOffset PeriodEndUtc, string QueueUnit, string FlowUnit, string TimeUnit,
    int? QueueThresholdPassengers, string ArrivalSource, IReadOnlyList<QueueForecastSeries> Checkpoints, string ObservationSource);

public interface IQueueForecaster
{
    Task<QueueForecastResult> ForecastAsync(QueueForecastRequest request, CancellationToken token);
}
public sealed class QueueForecasterUnavailableException() : Exception("Queue forecasting is unavailable. Check the intelligence service and retry.");

public static class QueueForecastValidation
{
    public static void Request(QueueForecastRequest value, string path, List<ContractFieldError> errors)
    {
        var simulation = value.Simulation;
        errors.AddRange(VersionedContract.Validate("KpiSimulationRequest", JsonSerializer.SerializeToElement(simulation, ContractJson.Options))
            .Select(error => error with { Field = path + "/simulation" + error.Field }));
        if (simulation.PassengerGeneration?.Flow is not null) errors.Add(VersionedContract.Error(path + "/simulation/passengerGeneration/flow", "invalid_value"));
        if (value.Options.FromMinute + value.Options.HorizonMinutes > simulation.DurationMinutes)
            errors.Add(VersionedContract.Error(path + "/options/fromMinute", "out_of_range"));
        if (value.Backlogs.Select(row => row.CheckpointId).Distinct().Count() != value.Backlogs.Count)
            errors.Add(VersionedContract.Error(path + "/backlogs", "duplicate_id"));
        if (!value.Backlogs.Select(row => row.CheckpointId).ToHashSet().SetEquals(simulation.Checkpoints.Select(cp => cp.CheckpointId)))
            errors.Add(VersionedContract.Error(path + "/backlogs", "invalid_value"));
        var population = simulation.PassengerCount + (simulation.Events?.Sum(item => item.PassengerCount ?? 0) ?? 0);
        if (value.Backlogs.Sum(row => (long)row.QueuedPassengers + row.NotArrivedPassengers) > population)
            errors.Add(VersionedContract.Error(path + "/backlogs", "out_of_range"));
    }

    public static bool ValidResult(QueueForecastResult value)
    {
        var horizon = value.ToMinute - value.FromMinute;
        if (horizon is < 15 or > 30 || value.PeriodEndUtc - value.PeriodStartUtc != TimeSpan.FromMinutes(horizon)
            || value.Checkpoints.Select(row => row.CheckpointId).Distinct().Count() != value.Checkpoints.Count) return false;
        foreach (var row in value.Checkpoints)
        {
            if (row.Points.Count != horizon) return false;
            double queue = row.InitialQueuePassengers;
            QueueForecastAlert? alert = null;
            foreach (var (point, index) in row.Points.Select((point, index) => (point, index)))
            {
                queue = Math.Max(0, queue + point.Arrivals - point.Capacity);
                if (point.Minute != value.FromMinute + index + 1 || !double.IsFinite(queue) || point.QueuePassengers != queue
                    || point.Capacity != point.AvailableStaff * row.ServiceRatePerStaffMinute) return false;
                if (value.QueueThresholdPassengers is { } threshold && queue > threshold && alert is null)
                    alert = new(point.Minute, queue, threshold);
            }
            if (alert != row.Alert || row.Mitigation != Mitigate(row, value.QueueThresholdPassengers)) return false;
        }
        return true;
    }

    public const int MaxExtraStaff = 10000;

    // Mirrors the Python search: the smallest extra staffing on staffed lanes that keeps every estimated minute within the threshold.
    public static QueueForecastMitigation? Mitigate(QueueForecastSeries row, int? threshold)
    {
        if (threshold is not { } limit) return null;
        if (Peak(row, MaxExtraStaff) > limit) return new(null, null);
        int low = 0, high = MaxExtraStaff;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (Peak(row, middle) <= limit) high = middle; else low = middle + 1;
        }
        return new(low, Peak(row, low));
    }

    private static double Peak(QueueForecastSeries row, int extra)
    {
        double queue = row.InitialQueuePassengers, peak = 0;
        foreach (var point in row.Points)
        {
            queue = Math.Max(0, queue + point.Arrivals - (point.AvailableStaff > 0 ? (point.AvailableStaff + extra) * row.ServiceRatePerStaffMinute : 0));
            peak = Math.Max(peak, queue);
        }
        return peak;
    }

    public static bool Correlates(QueueForecastRequest request, QueueForecastResult result)
    {
        var timeline = new EventTimeline(request.Simulation);
        return ValidResult(result)
        && result.RunId == request.Simulation.RunId && result.FromMinute == request.Options.FromMinute
        && result.ToMinute == result.FromMinute + request.Options.HorizonMinutes
        && result.PeriodStartUtc == request.Simulation.StartsAtUtc.AddMinutes(result.FromMinute)
        && result.PeriodEndUtc == request.Simulation.StartsAtUtc.AddMinutes(result.ToMinute)
        && result.QueueThresholdPassengers == request.Options.QueueThresholdPassengers
        && result.ObservationSource == request.ObservationSource
        && result.ArrivalSource == (request.Simulation.PassengerGeneration is null ? "configured_arrival_rate" : "planned_passenger_profiles")
        && result.Checkpoints.Select(row => row.CheckpointId).ToHashSet().SetEquals(request.Backlogs.Select(row => row.CheckpointId))
        && result.Checkpoints.All(row => row.InitialQueuePassengers == request.Backlogs.Single(item => item.CheckpointId == row.CheckpointId).QueuedPassengers)
        && result.Checkpoints.All(row => row.Points.All(point => point.AvailableStaff == timeline.Capacity(row.CheckpointId, point.Minute - 1)))
        && result.Checkpoints.Sum(row => row.Points.Sum(point => point.Arrivals)) <= request.Simulation.PassengerCount
            + (request.Simulation.Events?.Sum(item => item.PassengerCount ?? 0) ?? 0) + 1e-8;
    }
}

// Research predictor. The deterministic baseline it carries must equal the validated forecast.
public sealed record QueuePredictor(string Kind, string ModelVersion, string? Estimator, string? DatasetVersion, string? DatasetHash,
    string? TrainingDataSource, string? Formula, string? FallbackReason);
public sealed record QueuePredictionInterval(double LowerPassengers, double UpperPassengers, double NominalCoverage);
public sealed record QueuePredictionCheckpoint(Guid CheckpointId, double DeterministicQueuePassengers, double PredictedQueuePassengers,
    QueuePredictionInterval? PredictionInterval, double? ExceedanceProbability);
public sealed record QueuePredictionResult(string ContractVersion, Guid RunId, string Kind, string Validity, int FromMinute, int ToMinute,
    int? QueueThresholdPassengers, string ObservationSource, QueuePredictor Predictor, IReadOnlyList<QueuePredictionCheckpoint> Checkpoints);

public interface IQueuePredictor
{
    Task<QueuePredictionResult> PredictAsync(QueueForecastRequest request, CancellationToken token);
}

// Mirrors intelligence/app/forecast/contract.py.
public static class QueuePredictionValidation
{
    public const string DeterministicVersion = "us036-fluid-1.0";

    public static bool ValidResult(QueuePredictionResult value)
    {
        var predictor = value.Predictor;
        var horizon = value.ToMinute - value.FromMinute;
        if (horizon is < 15 or > 30 || value.Checkpoints.Select(row => row.CheckpointId).Distinct().Count() != value.Checkpoints.Count) return false;
        string?[] provenance = [predictor.Estimator, predictor.DatasetVersion, predictor.DatasetHash, predictor.TrainingDataSource];
        if (predictor.Kind == "deterministic")
            return predictor.ModelVersion == DeterministicVersion && predictor.Formula is not null && predictor.FallbackReason is not null
                && provenance.All(item => item is null)
                && value.Checkpoints.All(row => row.PredictedQueuePassengers == row.DeterministicQueuePassengers
                    && row.PredictionInterval is null && row.ExceedanceProbability is null);
        return predictor.ModelVersion != DeterministicVersion && predictor.Formula is null && predictor.FallbackReason is null
            && provenance.All(item => item is not null)
            && value.Checkpoints.All(row => row.PredictionInterval is { } interval && interval.LowerPassengers <= interval.UpperPassengers
                && row.ExceedanceProbability.HasValue == value.QueueThresholdPassengers.HasValue);
    }

    public static bool Correlates(QueueForecastRequest request, QueueForecastResult forecast, QueuePredictionResult prediction) =>
        ValidResult(prediction) && prediction.RunId == request.Simulation.RunId && prediction.RunId == forecast.RunId
        && prediction.FromMinute == forecast.FromMinute && prediction.ToMinute == forecast.ToMinute
        && prediction.QueueThresholdPassengers == request.Options.QueueThresholdPassengers
        && prediction.ObservationSource == request.ObservationSource && prediction.Checkpoints.Count == forecast.Checkpoints.Count
        && forecast.Checkpoints.All(row => prediction.Checkpoints.SingleOrDefault(item => item.CheckpointId == row.CheckpointId) is { } match
            && match.DeterministicQueuePassengers == row.Points[^1].QueuePassengers);
}

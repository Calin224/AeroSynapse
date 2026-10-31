using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroSynapse.Application.Contracts;

namespace AeroSynapse.Infrastructure.Simulation;

public sealed class PassengerGeneratorClient(HttpClient http) : IPassengerGenerator
{
    public Task<PassengerGenerationResult> GenerateAsync(PassengerGenerationRequest request, int offset, int limit, CancellationToken token) =>
        PostAsync<PassengerGenerationResult>($"passengers/v1/generate?offset={offset}&limit={limit}", request, result =>
        {
            var modeled = request.Batches.Sum(batch => batch.ModeledCount);
            var configured = request.Flights.Sum(flight => flight.PassengerCapacity);
            var returned = Math.Min(limit, Math.Max(0, modeled - offset));
            if (!result.IsSynthetic || result.Seed != request.Seed || result.GeneratorVersion != request.GeneratorVersion
                || result.ModeledCount != modeled || result.ConfiguredCount != configured || result.HasSubset != (modeled != configured)
                || result.Offset != offset || result.ReturnedCount != returned || result.HasMore != (offset + returned < modeled)
                || result.Passengers is null || result.Batches is null || result.CheckpointDemand is null
                || result.Passengers.Any(passenger => passenger is null) || result.Batches.Any(batch => batch is null)
                || result.CheckpointDemand.Any(checkpoint => checkpoint is null)
                || result.ReturnedCount != result.Passengers.Count || result.Batches.Count != request.Batches.Count
                || result.CheckpointDemand.Count != request.Checkpoints.Count
                || !Fingerprint(result.Fingerprint) || result.Rng != "PCG64" || string.IsNullOrWhiteSpace(result.NumpyVersion))
                return false;
            var flights = request.Flights.ToDictionary(flight => flight.FlightId);
            var batches = request.Batches.ToDictionary(batch => batch.FlightId);
            var checkpoints = request.Checkpoints.ToDictionary(checkpoint => checkpoint.CheckpointId);
            return result.Batches.Select(batch => batch.FlightId).Distinct().Count() == result.Batches.Count
                && result.CheckpointDemand.Select(item => item.CheckpointId).Distinct().Count() == checkpoints.Count
                && !result.CheckpointDemand.Any(item => !checkpoints.TryGetValue(item.CheckpointId, out var checkpoint)
                    || item.StaffCount != checkpoint.StaffCount || item.ModeledCount < 0 || item.ModeledCount > modeled
                    || item.PeakArrivalsPerMinute < 0 || item.PeakArrivalsPerMinute > item.ModeledCount
                    || item.ArrivalsPerQuarterHour is not { } quarters || quarters.Count != (request.DurationMinutes + 14) / 15
                    || quarters.Any(count => count < 0) || quarters.Sum() != item.ModeledCount
                    || item.ModeledCount > 0 && quarters.Max() < item.PeakArrivalsPerMinute
                    || !double.IsFinite(item.MeanServiceMinutes) || item.MeanServiceMinutes < 0
                    || !double.IsFinite(item.EstimatedCapacityPerMinute) || item.EstimatedCapacityPerMinute < 0
                    || (item.ModeledCount == 0 ? item.PeakArrivalsPerMinute != 0 || item.MeanServiceMinutes != 0
                        : item.PeakArrivalsPerMinute == 0 || item.MeanServiceMinutes == 0))
                && !result.Batches.Any(batch => batch is null || !batch.IsSynthetic || !flights.TryGetValue(batch.FlightId, out var flight)
                    || batch.ConfiguredCount != flight.PassengerCapacity || batch.ModeledCount != batches[batch.FlightId].ModeledCount
                    || batch.UnmodeledCount != batch.ConfiguredCount - batch.ModeledCount || batch.CountMode != batches[batch.FlightId].CountMode)
                && !result.Passengers.Any(passenger => passenger is null || !passenger.IsSynthetic
                    || !flights.TryGetValue(passenger.FlightId, out var flight) || !flight.Allows(passenger.CheckpointId, passenger.RouteId)
                    || string.IsNullOrWhiteSpace(passenger.Id)
                    || !double.IsFinite(passenger.ArrivalMinute) || passenger.ArrivalMinute < batches[passenger.FlightId].Arrival.Minimum
                    || passenger.ArrivalMinute > batches[passenger.FlightId].Arrival.Maximum
                    || !Duration(passenger.ServiceMinutes) || !Duration(passenger.TravelMinutes)
                    || (request.Flow is null) != (passenger.Segment is null) || (request.Flow is null) != (passenger.Stages is null)
                    || (request.Flow is null) != (passenger.TransferMinutes is null)
                    || passenger.TransferMinutes is { } transfer && (!double.IsFinite(transfer) || transfer is < 0 or > 240)
                    || passenger.Stages is { } stages && (stages.Count == 0 || stages.Any(stage => stage is null || !Duration(stage.ServiceMinutes))))
                && result.Passengers.Select(passenger => passenger.Id).Distinct().Count() == result.Passengers.Count;
        }, token);

    public Task<PassengerFlowReport> AnalyzeFlowsAsync(PassengerGenerationRequest request, CancellationToken token) =>
        PostAsync<PassengerFlowReport>("passengers/v1/flows", request, report =>
        {
            var modeled = request.Batches.Sum(batch => batch.ModeledCount);
            var segments = request.Flow?.Segments.Select(segment => segment.Kind).ToHashSet() ?? ["standard"];
            return report.IsSynthetic && report.Seed == request.Seed && report.GeneratorVersion == request.GeneratorVersion
                && report.DurationMinutes == request.DurationMinutes && report.ModeledCount == modeled && Fingerprint(report.Fingerprint)
                && report.Stages is not null && report.Segments is not null
                && report.Stages.All(stage => stage is not null && stage.ProcessedPassengers >= 0 && stage.QueuedPassengers >= 0
                    && stage.ProcessedPassengers + stage.QueuedPassengers <= stage.ArrivedPassengers
                    && new[] { stage.MeanWaitMinutes, stage.MaxWaitMinutes, stage.BlockedMinutes }.All(double.IsFinite))
                && report.Segments.All(segment => segment is not null && segments.Contains(segment.Segment)
                    && new[] { segment.MeanJourneyMinutes, segment.MaxJourneyMinutes, segment.MeanQueueMinutes, segment.MeanClosureDelayMinutes }.All(double.IsFinite))
                && report.Segments.Select(segment => segment.Segment).Distinct().Count() == segments.Count
                && report.Segments.Count == segments.Count && report.Segments.Sum(segment => segment.Passengers) == modeled;
        }, token);

    private async Task<T> PostAsync<T>(string path, PassengerGenerationRequest request, Func<T, bool> consistent, CancellationToken token)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(path, request, ContractJson.Options, token);
            if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
            {
                var errors = await response.Content.ReadFromJsonAsync<ContractErrorResponse>(ContractJson.Options, token);
                if (errors is not null) throw new ContractValidationException(errors.Errors);
            }
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<T>(ContractJson.Options, token) ?? throw new JsonException("Missing passenger result.");
            return consistent(result) ? result : throw new JsonException("Inconsistent passenger result.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException)
        {
            throw new PassengerGeneratorUnavailableException("Passenger generation could not be completed. Check the intelligence service and retry.");
        }
    }

    private static bool Fingerprint(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    private static bool Duration(double minutes) => double.IsFinite(minutes) && minutes is > 0 and <= 240;
}

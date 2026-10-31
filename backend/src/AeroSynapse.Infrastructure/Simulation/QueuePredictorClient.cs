using System.Net.Http.Json;
using System.Text.Json;
using AeroSynapse.Application.Contracts;

namespace AeroSynapse.Infrastructure.Simulation;

public sealed class QueuePredictorClient(HttpClient http) : IQueuePredictor
{
    public async Task<QueuePredictionResult> PredictAsync(QueueForecastRequest request, CancellationToken token)
    {
        try
        {
            using var response = await http.PostAsJsonAsync("simulation/queue-prediction", request, ContractJson.Options, token);
            response.EnsureSuccessStatusCode();
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
            QueuePredictionResult result;
            try { result = ContractJson.Read<QueuePredictionResult>(document.RootElement, "QueuePredictionResult"); }
            catch (ContractValidationException failure) { throw new JsonException("Invalid queue prediction.", failure); }
            if (result.RunId != request.Simulation.RunId) throw new JsonException("Uncorrelated queue prediction.");
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception failure) when (failure is JsonException or HttpRequestException or OperationCanceledException)
        { throw new QueueForecasterUnavailableException(); }
    }
}

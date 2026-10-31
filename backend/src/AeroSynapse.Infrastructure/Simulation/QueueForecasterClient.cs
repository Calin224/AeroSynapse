using System.Net.Http.Json;
using System.Text.Json;
using AeroSynapse.Application.Contracts;

namespace AeroSynapse.Infrastructure.Simulation;

public sealed class QueueForecasterClient(HttpClient http) : IQueueForecaster
{
    public async Task<QueueForecastResult> ForecastAsync(QueueForecastRequest request, CancellationToken token)
    {
        try
        {
            using var response = await http.PostAsJsonAsync("simulation/queue-forecast", request, ContractJson.Options, token);
            response.EnsureSuccessStatusCode();
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
            QueueForecastResult result;
            try { result = ContractJson.Read<QueueForecastResult>(document.RootElement, "QueueForecastResult"); }
            catch (ContractValidationException failure) { throw new JsonException("Invalid queue forecast.", failure); }
            if (!QueueForecastValidation.Correlates(request, result)) throw new JsonException("Uncorrelated queue forecast.");
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception failure) when (failure is JsonException or HttpRequestException or OperationCanceledException)
        { throw new QueueForecasterUnavailableException(); }
    }
}

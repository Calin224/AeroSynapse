using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Simulation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AeroSynapse.Infrastructure.Simulation;

public sealed class IntelligenceClient(HttpClient http, IOptions<SimulationOptions> options,
    ILogger<IntelligenceClient> logger) : IIntelligenceClient
{
    public async Task<EngineExecution> StartAsync(SimulationRequest request, string correlationId, CancellationToken token) =>
        (await SendAsync(HttpMethod.Post, "simulation/runs", request.RunId, correlationId, request, token))!;

    public Task<EngineExecution?> GetAsync(Guid runId, string correlationId, CancellationToken token) =>
        SendAsync(HttpMethod.Get, $"simulation/runs/{runId}", runId, correlationId, null, token);
    public async Task<EngineExecution> StartControlledAsync(SimulationRequest request, ExecutionControl control, string correlationId, CancellationToken token) =>
        (await SendAsync(HttpMethod.Post, "simulation/runs", request.RunId, correlationId, request, token, control))!;
    public async Task CancelAsync(Guid runId, string correlationId, CancellationToken token)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"simulation/runs/{runId}/cancel");
        message.Headers.Add("X-Correlation-ID", correlationId);
        try
        {
            using var response = await http.SendAsync(message, token);
            if (!response.IsSuccessStatusCode) throw new IntelligenceException("engine_cancel_unavailable", "Remote cancellation was not acknowledged.", true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (HttpRequestException) { throw new IntelligenceException("engine_cancel_unavailable", "Remote cancellation could not reach the engine.", true); }
    }

    private async Task<EngineExecution?> SendAsync(HttpMethod method, string path, Guid runId,
        string correlationId, SimulationRequest? payload, CancellationToken token, ExecutionControl? control = null)
    {
        var attempts = method == HttpMethod.Get ? options.Value.SafeRetryCount + 1 : 1;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var message = new HttpRequestMessage(method, path);
                message.Headers.Add("X-Correlation-ID", correlationId);
                if (control is not null) message.Headers.Add("X-Execution-Timeout-Ms", control.TimeoutMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (payload is not null) message.Content = JsonContent.Create(payload, options: ContractJson.Options);
                using var response = await http.SendAsync(message, token);
                logger.LogInformation("Intelligence {Method} run={RunId} status={StatusCode} correlationId={CorrelationId}",
                    method, runId, (int)response.StatusCode, correlationId);
                if (method == HttpMethod.Get && response.StatusCode == HttpStatusCode.NotFound) return null;
                if (!response.IsSuccessStatusCode)
                {
                    var transient = response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
                    throw new IntelligenceException(response.StatusCode switch
                    {
                        HttpStatusCode.Conflict => "engine_conflict",
                        HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => "engine_validation",
                        _ => "engine_unavailable"
                    }, "Intelligence rejected the simulation request.", transient);
                }
                var result = await response.Content.ReadFromJsonAsync<EngineExecution>(ContractJson.Options, token);
                if (result is null || result.RunId != runId || result.Status is not ("pending" or "running" or "completed" or "failed")
                    || ((result.Status is "completed" or "failed") != (result.Result is not null))
                    || (result.Result is not null && result.Result.Status != result.Status))
                    throw new IntelligenceException("engine_invalid_response", "Intelligence returned an inconsistent response.");
                if (result.Result is not null)
                    ContractJson.Read<SimulationResponse>(JsonSerializer.SerializeToElement(result.Result, ContractJson.Options), "SimulationResponse");
                if (result.Progress is { } progress && (!double.IsFinite(progress.SimulationMinute) || progress.SimulationMinute is < 0 or > 10080
                    || progress.Zones is null || progress.Zones.Count > 1000 || progress.Zones.Any(x => x.ZoneId == Guid.Empty
                        || x.QueuedPassengers < 0 || x.ProcessedPassengers < 0 || x.NotArrivedPassengers < 0)))
                    throw new IntelligenceException("engine_invalid_response", "Intelligence returned invalid progress.");
                return result;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException or IntelligenceException or JsonException or ContractValidationException)
            {
                var mapped = error switch
                {
                    IntelligenceException known => known,
                    OperationCanceledException => new IntelligenceException("engine_timeout", "Intelligence request timed out; reconciling the existing run.", true),
                    HttpRequestException => new IntelligenceException("engine_network", "Intelligence could not be reached; reconciling the existing run.", true),
                    _ => new IntelligenceException("engine_invalid_response", "Intelligence returned an invalid result.")
                };
                if (!mapped.Transient || attempt + 1 >= attempts) throw mapped;
                await Task.Delay(TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt)), token);
            }
        }
    }
}

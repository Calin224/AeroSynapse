using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroSynapse.Application.Contracts;

namespace AeroSynapse.Infrastructure.Simulation;

// Every answer is schema-checked; detections are also re-derived from the request before use.
public sealed class AnomalyDetectorClient(HttpClient http) : IAnomalyDetector
{
    public async Task<AnomalyDetectionResult> DetectAsync(AnomalyDetectionRequest request, CancellationToken token)
    {
        var payload = await PostAsync("anomaly/detect", request, "AnomalyDetectionResult", token)
            ?? throw new AnomalyDetectorUnavailableException();
        var result = payload.Deserialize<AnomalyDetectionResult>(ContractJson.Options)!;
        return AnomalyValidation.Correlates(request, result) ? result : throw new AnomalyDetectorUnavailableException();
    }

    public async Task<JsonElement> EvaluateAsync(AnomalySettings settings, CancellationToken token) =>
        await PostAsync("anomaly/evaluation", new { settings }, "AnomalyEvaluationResult", token) ?? throw new AnomalyDetectorUnavailableException();

    public async Task<JsonElement?> CaseAsync(AnomalySettings settings, string caseKey, CancellationToken token) =>
        await PostAsync("anomaly/evaluation/case", new { settings, caseKey }, "AnomalyCaseResult", token, allowMissing: true);

    private async Task<JsonElement?> PostAsync(string path, object body, string definition, CancellationToken token, bool allowMissing = false)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(path, body, ContractJson.Options, token);
            if (allowMissing && response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
            if (VersionedContract.Validate(definition, document.RootElement).Count > 0) throw new JsonException("Invalid anomaly answer.");
            return document.RootElement.Clone();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception failure) when (failure is JsonException or HttpRequestException or OperationCanceledException or NotSupportedException)
        { throw new AnomalyDetectorUnavailableException(); }
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Simulation;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using AeroSynapse.Infrastructure.Simulation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace AeroSynapse.Management.Tests;

public sealed class QueuePredictionApiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static string Example(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "forecast", "v1", "examples", name + ".json"));
    private static QueueForecastRequest Request => JsonSerializer.Deserialize<QueueForecastRequest>(Example("forecast-request"), ContractJson.Options)!;
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken token) => send(message, token); }

    // Wraps the real Python client so a test can forge or upgrade its answer before the handler validates it.
    private sealed class Rewriting(QueuePredictorClient inner, Func<QueuePredictionResult, QueuePredictionResult> rewrite) : IQueuePredictor
    {
        public async Task<QueuePredictionResult> PredictAsync(QueueForecastRequest request, CancellationToken token) =>
            rewrite(await inner.PredictAsync(request, token));
    }

    private static Action<IServiceCollection> Rewrite(Func<QueuePredictionResult, QueuePredictionResult> rewrite) => services =>
    {
        services.RemoveAll<IQueuePredictor>();
        services.AddHttpClient<QueuePredictorClient>(client => client.BaseAddress = new((Environment.GetEnvironmentVariable("SIMULATION_TEST_URL") ?? "http://intelligence:8000").TrimEnd('/') + "/"));
        services.AddScoped<IQueuePredictor>(provider => new Rewriting(provider.GetRequiredService<QueuePredictorClient>(), rewrite));
    };

    private static QueuePredictionResult Upgrade(QueuePredictionResult value) => value with
    {
        Predictor = new("ml", "us038-residual-cqr-calibrated-2.0+0123456789abcdef", "hgb_shallow", "us038-synthetic-2.0", new string('a', 64),
            "synthetic_simpy_simulation", null, null),
        Checkpoints = value.Checkpoints.Select(row => row with
        {
            PredictedQueuePassengers = row.DeterministicQueuePassengers + 1, PredictionInterval = new(0, row.DeterministicQueuePassengers + 5, 0.8),
            ExceedanceProbability = value.QueueThresholdPassengers is null ? null : 0.5
        }).ToArray()
    };

    private static async Task<(ManagementHost Host, Guid Run)> Completed(Action<IServiceCollection>? services = null)
    {
        var host = await ManagementHost.CreateAsync(realEngine: true, services: services);
        var source = await StaffingEvaluationTests.Create(host);
        using var started = await host.Client.PostAsync($"/scenarios/{source.Scenario.Id}/runs", null, Token);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var run = (await started.Content.ReadFromJsonAsync<SimulationRun>(Json, Token))!;
        Assert.True(await host.Dispatch(Token));
        return (host, run.Id);
    }

    [Fact]
    public async Task Disabled_predictor_returns_the_validated_deterministic_forecast_with_its_provenance()
    {
        var (host, run) = await Completed();
        await using var _ = host;
        using var forecastResponse = await host.Client.PostAsJsonAsync($"/runs/{run}/queue-forecast", new QueueForecastOptions(5, 15, 3), Json, Token);
        var forecast = (await forecastResponse.Content.ReadFromJsonAsync<QueueForecastResult>(ContractJson.Options, Token))!;
        using var response = await host.Client.PostAsJsonAsync($"/runs/{run}/queue-prediction", new QueueForecastOptions(5, 15, 3), Json, Token);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Token));
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(Json, Token);
        Assert.Empty(VersionedContract.Validate("QueuePredictionResult", payload));
        var prediction = payload.Deserialize<QueuePredictionResult>(ContractJson.Options)!;
        Assert.Equal("deterministic", prediction.Predictor.Kind);
        Assert.Equal("predictor_disabled", prediction.Predictor.FallbackReason);
        Assert.Equal(QueuePredictionValidation.DeterministicVersion, prediction.Predictor.ModelVersion);
        Assert.Equal("captured_snapshot", prediction.ObservationSource);
        Assert.StartsWith("synthetic_only", prediction.Validity);
        foreach (var row in forecast.Checkpoints)
            Assert.Equal(row.Points[^1].QueuePassengers, prediction.Checkpoints.Single(item => item.CheckpointId == row.CheckpointId).PredictedQueuePassengers);
    }

    [Fact]
    public async Task A_valid_model_answer_passes_through_but_a_forged_baseline_is_rejected()
    {
        var (host, run) = await Completed(Rewrite(Upgrade));
        await using (host)
        {
            using var response = await host.Client.PostAsJsonAsync($"/runs/{run}/queue-prediction", new QueueForecastOptions(5, 15, 3), Json, Token);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Token));
            var prediction = (await response.Content.ReadFromJsonAsync<QueuePredictionResult>(ContractJson.Options, Token))!;
            Assert.Equal("ml", prediction.Predictor.Kind); Assert.Equal("synthetic_simpy_simulation", prediction.Predictor.TrainingDataSource);
            Assert.All(prediction.Checkpoints, row => Assert.Equal(0.5, row.ExceedanceProbability));
        }
        var forged = await Completed(Rewrite(value => Upgrade(value) with
            { Checkpoints = Upgrade(value).Checkpoints.Select(row => row with { DeterministicQueuePassengers = row.DeterministicQueuePassengers + 7 }).ToArray() }));
        await using (forged.Host)
        {
            using var response = await forged.Host.Client.PostAsJsonAsync($"/runs/{forged.Run}/queue-prediction", new QueueForecastOptions(5, 15, 3), Json, Token);
            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        }
    }

    [Fact]
    public async Task Prediction_requests_follow_the_forecast_rules_for_runs_and_periods()
    {
        var (host, run) = await Completed();
        await using var _ = host;
        using var missing = await host.Client.PostAsJsonAsync($"/runs/{Guid.NewGuid()}/queue-prediction", new QueueForecastOptions(0, 15, null), Json, Token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var invalid = await host.Client.PostAsJsonAsync($"/runs/{run}/queue-prediction", new QueueForecastOptions(0, 14, null), Json, Token);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        using var notLive = await host.Client.PostAsJsonAsync($"/runs/{run}/queue-prediction/live", new QueueForecastLiveOptions(15, null), Json, Token);
        Assert.Equal(HttpStatusCode.Conflict, notLive.StatusCode);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("provenance")]
    [InlineData("unavailable")]
    [InlineData("malformed")]
    public async Task Client_rejects_invalid_or_unavailable_predictions(string kind)
    {
        var result = JsonNode.Parse(Example("prediction-response"))!;
        if (kind == "run") result["runId"] = Guid.NewGuid().ToString();
        if (kind == "provenance") result["predictor"]!["datasetHash"] = null;
        var payload = kind == "malformed" ? "{" : result.ToJsonString();
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(kind == "unavailable" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            { Content = new StringContent(payload, Encoding.UTF8, "application/json") }))) { BaseAddress = new("http://intelligence/") };
        await Assert.ThrowsAsync<QueueForecasterUnavailableException>(() => new QueuePredictorClient(http).PredictAsync(Request, Token));
    }

    [Fact]
    public async Task Client_posts_the_forecast_request_to_the_prediction_endpoint()
    {
        using var http = new HttpClient(new Handler(async (message, token) =>
        {
            Assert.Equal("/simulation/queue-prediction", message.RequestUri!.AbsolutePath);
            Assert.Equal(JsonSerializer.Serialize(Request, ContractJson.Options), await message.Content!.ReadAsStringAsync(token));
            return new(HttpStatusCode.OK) { Content = new StringContent(Example("prediction-response"), Encoding.UTF8, "application/json") };
        })) { BaseAddress = new("http://intelligence/") };
        var result = await new QueuePredictorClient(http).PredictAsync(Request, Token);
        Assert.Equal("ml", result.Predictor.Kind);
    }
}

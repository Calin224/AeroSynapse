using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Infrastructure.Simulation;
using Xunit;

namespace AeroSynapse.Management.Tests;

public sealed class QueueForecastClientTests
{
    private static string Example(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "forecast", "v1", "examples", name + ".json"));
    private static QueueForecastRequest Request => JsonSerializer.Deserialize<QueueForecastRequest>(Example("forecast-request"), ContractJson.Options)!;
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken token) => send(message, token); }

    [Fact]
    public async Task Only_a_correlated_mathematically_valid_forecast_is_returned()
    {
        using var http = new HttpClient(new Handler(async (message, token) =>
        {
            Assert.Equal("/simulation/queue-forecast", message.RequestUri!.AbsolutePath);
            Assert.Equal(JsonSerializer.Serialize(Request, ContractJson.Options), await message.Content!.ReadAsStringAsync(token));
            return new(HttpStatusCode.OK) { Content = new StringContent(Example("forecast-response"), Encoding.UTF8, "application/json") };
        })) { BaseAddress = new("http://intelligence/") };
        var result = await new QueueForecasterClient(http).ForecastAsync(Request, Token);
        Assert.True(QueueForecastValidation.Correlates(Request, result));
    }

    [Theory]
    [InlineData("run")]
    [InlineData("queue")]
    [InlineData("alert")]
    [InlineData("unavailable")]
    [InlineData("malformed")]
    [InlineData("null")]
    public async Task Invalid_or_unavailable_results_fail_without_fabricating_an_estimate(string kind)
    {
        var result = JsonNode.Parse(Example("forecast-response"))!;
        if (kind == "run") result["runId"] = Guid.NewGuid().ToString();
        if (kind == "queue") result["checkpoints"]![0]!["points"]![0]!["queuePassengers"] = -1;
        if (kind == "alert") result["checkpoints"]![0]!["alert"] = null;
        var payload = kind switch { "malformed" => "{", "null" => "null", _ => result.ToJsonString() };
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(kind == "unavailable" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            { Content = new StringContent(payload, Encoding.UTF8, "application/json") }))) { BaseAddress = new("http://intelligence/") };
        await Assert.ThrowsAsync<QueueForecasterUnavailableException>(() => new QueueForecasterClient(http).ForecastAsync(Request, Token));
    }

    [Fact]
    public async Task Caller_cancellation_is_preserved()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        using var http = new HttpClient(new Handler((_, token) => Task.FromCanceled<HttpResponseMessage>(token))) { BaseAddress = new("http://intelligence/") };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new QueueForecasterClient(http).ForecastAsync(Request, cancellation.Token));
    }
}

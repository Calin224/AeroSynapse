using System.Net;
using System.Text;
using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Infrastructure.Simulation;
using Xunit;

namespace AeroSynapse.Management.Tests;

public sealed class ResourcePlannerClientTests
{
    private static string Example(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts", "resources", "v1", "examples", name + ".json"));
    private static ResourceSolveRequest Request => JsonSerializer.Deserialize<ResourceSolveRequest>(Example("resource-request"), ContractJson.Options)!;
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken token) => send(message, token); }
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Sends_the_captured_request_to_the_versioned_solver_and_returns_only_a_correlated_valid_result()
    {
        using var http = new HttpClient(new Handler(async (message, token) =>
        {
            Assert.Equal("/optimization/resources/v1/solve", message.RequestUri!.AbsolutePath);
            Assert.Equal(JsonSerializer.Serialize(Request, ContractJson.Options), await message.Content!.ReadAsStringAsync(token));
            return new(HttpStatusCode.OK) { Content = new StringContent(Example("resource-response"), Encoding.UTF8, "application/json") };
        })) { BaseAddress = new("http://intelligence/") };
        var result = await new ResourcePlannerClient(http).SolveAsync(Request, Token);
        Assert.True(ResourceValidation.Valid(Request, result));
    }

    [Theory]
    [InlineData("score")]
    [InlineData("scenario")]
    [InlineData("unavailable")]
    [InlineData("malformed")]
    [InlineData("null")]
    public async Task Untrusted_or_unavailable_responses_are_errors_without_a_fallback_assignment(string kind)
    {
        var body = JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(Example("resource-response"))!;
        if (kind == "score") body["score"] = 0;
        if (kind == "scenario") body["constraints"]!["scenarioId"] = Guid.NewGuid().ToString();
        var payload = kind switch { "malformed" => "{", "null" => "null", _ => body.ToJsonString() };
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(kind == "unavailable" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            { Content = new StringContent(payload, Encoding.UTF8, "application/json") }))) { BaseAddress = new("http://intelligence/") };
        await Assert.ThrowsAsync<ResourcePlannerUnavailableException>(() => new ResourcePlannerClient(http).SolveAsync(Request, Token));
    }

    [Fact]
    public async Task Caller_cancellation_is_preserved()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        using var http = new HttpClient(new Handler((_, token) => Task.FromCanceled<HttpResponseMessage>(token))) { BaseAddress = new("http://intelligence/") };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ResourcePlannerClient(http).SolveAsync(Request, cancellation.Token));
    }
}

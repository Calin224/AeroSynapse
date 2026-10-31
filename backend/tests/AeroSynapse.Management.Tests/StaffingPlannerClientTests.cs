using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Infrastructure.Simulation;
using Xunit;

namespace AeroSynapse.Management.Tests;

public sealed class StaffingPlannerClientTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static string Example(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "staffing", name + ".json"));
    private static StaffingSolveRequest Request => JsonSerializer.Deserialize<StaffingSolveRequest>(Example("staffing-request"), ContractJson.Options)!;

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private static HttpResponseMessage Response(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static HttpClient Client(HttpMessageHandler handler) => new(handler) { BaseAddress = new Uri("http://intelligence/") };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Correlated_optimal_or_feasible_incumbents_are_forwarded_with_their_actual_status(bool feasible)
    {
        var body = JsonNode.Parse(Example("staffing-response"))!;
        if (feasible)
        {
            body["status"] = "feasible"; body["solverStatus"] = "FEASIBLE";
            body["timedOut"] = true; body["bestBound"] = 0;
        }
        using var http = Client(new Handler(async (request, token) =>
        {
            Assert.Equal("/optimization/staffing/v1/solve", request.RequestUri!.PathAndQuery);
            var sent = JsonSerializer.Deserialize<StaffingSolveRequest>(await request.Content!.ReadAsStringAsync(token), ContractJson.Options)!;
            Assert.Equal(JsonSerializer.Serialize(Request, ContractJson.Options), JsonSerializer.Serialize(sent, ContractJson.Options));
            return Response(HttpStatusCode.OK, body.ToJsonString());
        }));
        var result = await new StaffingPlannerClient(http).SolveAsync(Request, Token);
        Assert.Equal(feasible ? "feasible" : "optimal", result.Status);
        Assert.Equal(feasible, result.TimedOut); Assert.Equal(40, result.Score);
        Assert.True(StaffingValidation.Valid(Request, result));
    }

    [Theory]
    [InlineData("score")]
    [InlineData("scenario")]
    [InlineData("fake-timeout")]
    [InlineData("null")]
    [InlineData("malformed")]
    [InlineData("service-error")]
    [InlineData("empty-validation")]
    public async Task Invalid_or_unavailable_service_results_never_become_staffing_proposals(string kind)
    {
        var body = JsonNode.Parse(Example("staffing-response"))!;
        var status = HttpStatusCode.OK;
        switch (kind)
        {
            case "score": body["score"] = 0; break;
            case "scenario": body["constraints"]!["scenarioId"] = Guid.NewGuid().ToString(); break;
            case "fake-timeout": body["status"] = "timeout"; body["solverStatus"] = "UNKNOWN"; body["timedOut"] = true; break;
            case "service-error": status = HttpStatusCode.ServiceUnavailable; break;
            case "empty-validation": status = HttpStatusCode.UnprocessableEntity; break;
        }
        var payload = kind switch { "null" => "null", "malformed" => "{", "empty-validation" =>
            JsonSerializer.Serialize(VersionedContract.Response([]), ContractJson.Options), _ => body.ToJsonString() };
        using var http = Client(new Handler((_, _) => Task.FromResult(Response(status, payload))));
        await Assert.ThrowsAsync<StaffingPlannerUnavailableException>(() => new StaffingPlannerClient(http).SolveAsync(Request, Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transport_timeouts_report_unavailability_and_caller_cancellation_is_preserved(bool callerCancelled)
    {
        using var cancellation = new CancellationTokenSource();
        if (callerCancelled) cancellation.Cancel();
        using var http = Client(new Handler((_, token) => throw new OperationCanceledException(token)));
        var planner = new StaffingPlannerClient(http);
        if (callerCancelled) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => planner.SolveAsync(Request, cancellation.Token));
        else await Assert.ThrowsAsync<StaffingPlannerUnavailableException>(() => planner.SolveAsync(Request, cancellation.Token));
    }

    [Fact]
    public async Task Solver_validation_errors_preserve_the_field_diagnostics()
    {
        var errors = new[] { VersionedContract.Error("/checkpoints/0/physicalLimit", "out_of_range") };
        using var http = Client(new Handler((_, _) => Task.FromResult(Response(HttpStatusCode.UnprocessableEntity,
            JsonSerializer.Serialize(VersionedContract.Response(errors), ContractJson.Options)))));
        var failure = await Assert.ThrowsAsync<ContractValidationException>(() => new StaffingPlannerClient(http).SolveAsync(Request, Token));
        Assert.Equal(errors, failure.Errors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Candidate_search_results_must_correlate_with_the_original_constraints(bool mismatch)
    {
        var request = JsonSerializer.Deserialize<StaffingCandidateSearchRequest>(Example("staffing-candidates-request"), ContractJson.Options)!;
        var response = JsonNode.Parse(Example("staffing-candidates-response"))!;
        if (mismatch) response["request"]!["constraints"]!["scenarioId"] = Guid.NewGuid().ToString();
        using var http = Client(new Handler((message, _) =>
        {
            Assert.Equal("/optimization/staffing/v1/candidates", message.RequestUri!.PathAndQuery);
            return Task.FromResult(Response(HttpStatusCode.OK, response.ToJsonString()));
        }));
        if (mismatch) await Assert.ThrowsAsync<StaffingPlannerUnavailableException>(() => new StaffingPlannerClient(http).GenerateCandidatesAsync(request, Token));
        else Assert.Equal(4, (await new StaffingPlannerClient(http).GenerateCandidatesAsync(request, Token)).Candidates.Count);
    }
}

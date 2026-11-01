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
using AeroSynapse.Infrastructure.Storage;
using AeroSynapse.Infrastructure.Simulation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AeroSynapse.Management.Tests;

public sealed class ManagementTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Guid SceneId = SyntheticScenarioSeeder.SceneId;

    private static async Task<ScenarioWriteRequest> Request(ManagementHost host) => (await host.DraftAsync()).Request;
    private static Task<ScenarioDetails> Create(ManagementHost host, ScenarioWriteRequest request) => host.PostScenarioAsync(request);
    private static async Task<SimulationRun> Start(ManagementHost host, Guid id)
    {
        using var result = await host.Client.PostAsync($"/scenarios/{id}/runs", null, Token);
        Assert.Equal(HttpStatusCode.Accepted, result.StatusCode);
        var run = (await result.Content.ReadFromJsonAsync<SimulationRun>(Json, Token))!;
        Assert.Equal($"/runs/{run.Id}", result.Headers.Location!.OriginalString);
        return run;
    }
    private static Task<RunDetails?> Read(ManagementHost host, Guid id) => host.Client.GetFromJsonAsync<RunDetails>($"/runs/{id}", Json, Token);
    private static void SameSnapshot(string? expected, string? actual) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected!), JsonNode.Parse(actual!)), "Captured JSON values must be identical; PostgreSQL jsonb normalizes formatting.");

    [Fact]
    public async Task Management_and_idempotent_commands_keep_separate_worker_ownership_and_both_complete()
    {
        await using var host = await ManagementHost.CreateAsync();
        var request = await Request(host);
        var scenario = await Create(host, request with { Inputs = request.Inputs with { TimeZone = "Europe/Bucharest" } });
        var managed = await Start(host, scenario.Scenario.Id);
        Assert.True(managed.ExecutionRequested);
        var key = Guid.NewGuid();
        using var command = new HttpRequestMessage(HttpMethod.Post, $"/scenarios/{scenario.Scenario.Id}/simulations")
        {
            Content = JsonContent.Create(new StartSimulationRequest(1), options: Json)
        };
        command.Headers.Add("Idempotency-Key", key.ToString());
        command.Headers.Add("X-Correlation-ID", "merge-coexistence");
        using var reply = await host.Client.SendAsync(command, Token);
        Assert.Equal(HttpStatusCode.Accepted, reply.StatusCode);
        var queued = (await reply.Content.ReadFromJsonAsync<SimulationExecution>(Json, Token))!;
        Assert.Equal("pending", queued.Status);
        Assert.False((await Read(host, queued.RunId))!.ExecutionRequested);

        Assert.True(await host.Dispatch(Token));
        Assert.Equal(managed.Id, host.Engine.LastRequest!.RunId);
        Assert.Equal("completed", (await Read(host, managed.Id))!.Status);
        Assert.Equal("pending", (await Read(host, queued.RunId))!.Status);

        await using var scope = host.Services.CreateAsyncScope();
        var transport = new RecordingIntelligenceClient(host.Engine);
        var processor = new SimulationProcessor(scope.ServiceProvider.GetRequiredService<TwinDbContext>(), transport,
            Options.Create(new SimulationOptions()), NullLogger<SimulationProcessor>.Instance);
        Assert.True(await processor.ProcessNextAsync(Token));
        Assert.Equal(queued.RunId, host.Engine.LastRequest!.RunId);
        Assert.Equal("Europe/Bucharest", host.Engine.LastRequest.TimeZone);
        Assert.Equal("merge-coexistence", transport.CorrelationId);
        Assert.Equal("completed", (await Read(host, queued.RunId))!.Status);
        Assert.False(await host.Dispatch(Token));
        Assert.False(await processor.ProcessNextAsync(Token));
    }

    private sealed class RecordingIntelligenceClient(RecordingEngine engine) : IIntelligenceClient
    {
        public string? CorrelationId { get; private set; }
        public async Task<EngineExecution> StartAsync(SimulationRequest request, string correlationId, CancellationToken token)
        {
            CorrelationId = correlationId;
            return new(request.RunId, "completed", await engine.ExecuteAsync(request, token));
        }
        public Task<EngineExecution?> GetAsync(Guid runId, string correlationId, CancellationToken token) =>
            throw new InvalidOperationException("Completed commands must not be reconciled again.");
    }

    [Fact]
    public async Task Create_get_and_list_preserve_complete_configuration_and_reject_invalid_updates()
    {
        await using var host = await ManagementHost.CreateAsync();
        var request = await Request(host);
        var created = await Create(host, request);
        var loaded = (await host.Client.GetFromJsonAsync<ScenarioDetails>($"/scenarios/{created.Scenario.Id}", Json, Token))!;
        Assert.Equal(JsonSerializer.Serialize(request.Inputs, Json), JsonSerializer.Serialize(loaded.Inputs, Json));
        Assert.Single((await host.Client.GetFromJsonAsync<ScenarioSummary[]>("/scenarios", Json, Token))!);
        using var duplicate = await host.Client.PostAsJsonAsync("/scenarios", request, Json, Token);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using var invalid = await host.Client.PutAsJsonAsync($"/scenarios/{created.Scenario.Id}", request with { Revision = 1, Name = " " }, Json, Token);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("name", (await invalid.Content.ReadFromJsonAsync<JsonElement>(Json, Token)).GetProperty("errors").ToString());
        using var stale = await host.Client.PutAsJsonAsync($"/scenarios/{created.Scenario.Id}", request with { Revision = 5 }, Json, Token);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        loaded = (await host.Client.GetFromJsonAsync<ScenarioDetails>($"/scenarios/{created.Scenario.Id}", Json, Token))!;
        Assert.Equal(1, loaded.Scenario.Revision);
        Assert.Equal(request.Name, loaded.Scenario.Name);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"code\":")]
    [InlineData("{\"code\":\"x\",\"unknown\":1}")]
    public async Task Bad_JSON_and_missing_required_fields_return_400_without_generic_500(string body)
    {
        await using var host = await ManagementHost.CreateAsync();
        using var result = await host.Client.PostAsync("/scenarios", new StringContent(body, Encoding.UTF8, "application/json"), Token);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Equal("invalid_request", (await result.Content.ReadFromJsonAsync<JsonElement>(Json, Token)).GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("/scenarios/{id}")]
    [InlineData("/runs/{id}")]
    [InlineData("/scenarios/{id}/flights")]
    [InlineData("/scenarios/{id}/events")]
    [InlineData("/scenarios/{id}/runs")]
    [InlineData("/scenarios/{id}/revisions")]
    [InlineData("/scenes/{id}/events")]
    [InlineData("/scenes/{id}/layout-versions")]
    public async Task Absent_parent_resources_return_404(string path)
    {
        await using var host = await ManagementHost.CreateAsync();
        using var result = await host.Client.GetAsync(path.Replace("{id}", Guid.NewGuid().ToString()), Token);
        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
    }

    [Fact]
    public async Task Invalid_limits_identifiers_and_time_zones_are_controlled()
    {
        await using var host = await ManagementHost.CreateAsync();
        using var limit = await host.Client.GetAsync($"/scenes/{SceneId}/events?limit=0", Token);
        Assert.Equal(HttpStatusCode.BadRequest, limit.StatusCode);
        using var badId = await host.Client.GetAsync("/scenarios/not-a-guid", Token);
        Assert.Equal(HttpStatusCode.BadRequest, badId.StatusCode);
        var request = await Request(host);
        using var badZone = await host.Client.PostAsJsonAsync("/scenarios", request with { Inputs = request.Inputs with { TimeZone = "Mars/Olympus" } }, Json, Token);
        Assert.Equal(HttpStatusCode.BadRequest, badZone.StatusCode);
        var numericString = JsonSerializer.SerializeToNode(request, Json)!;
        numericString["inputs"]!["durationMinutes"] = "60";
        using var wrongType = await host.Client.PostAsJsonAsync("/scenarios", numericString, Json, Token);
        Assert.Equal(HttpStatusCode.BadRequest, wrongType.StatusCode);
    }

    [Theory]
    [InlineData("{\"note\":\"\\u0000\"}")]
    [InlineData("{\"note\":\"\\ud800\"}")]
    public async Task Invalid_event_payloads_do_not_reach_PostgreSQL_as_generic_500(string payload)
    {
        await using var host = await ManagementHost.CreateAsync();
        var request = await Request(host);
        using var result = await host.Client.PostAsJsonAsync("/scenarios", request with
            { Inputs = request.Inputs with { Events = [request.Inputs.Events[0] with { PayloadJson = payload }] } }, Json, Token);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
    }

    [Fact]
    public async Task Flights_and_events_use_aggregate_validation_and_revision_conflicts()
    {
        await using var host = await ManagementHost.CreateAsync();
        var scenario = await Create(host, await Request(host));
        var id = scenario.Scenario.Id;
        var flights = (await host.Client.GetFromJsonAsync<ScenarioItems<ScenarioFlight>>($"/scenarios/{id}/flights", Json, Token))!;
        Assert.Single(flights.Items);
        var addedFlight = scenario.Inputs.Flights[0] with { Id = Guid.NewGuid(), Code = "AS-NEW", GateId = SyntheticScenarioSeeder.Id(202) };
        using var add = await host.Client.PostAsJsonAsync($"/scenarios/{id}/flights", new FlightWriteRequest(1, addedFlight), Json, Token);
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);
        using var stale = await host.Client.PutAsJsonAsync($"/scenarios/{id}/flights/{addedFlight.Id}", new FlightWriteRequest(1, addedFlight), Json, Token);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var invalid = await host.Client.DeleteAsync($"/scenarios/{id}/flights/{scenario.Inputs.Flights[0].Id}?revision=2", Token);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode); // It is referenced by a flow/event.
        using var deleted = await host.Client.DeleteAsync($"/scenarios/{id}/flights/{addedFlight.Id}?revision=2", Token);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        var addedEvent = scenario.Inputs.Events[0] with { Id = Guid.NewGuid(), Type = "operator-note" };
        using var eventAdd = await host.Client.PostAsJsonAsync($"/scenarios/{id}/events", new EventWriteRequest(3, addedEvent), Json, Token);
        Assert.Equal(HttpStatusCode.Created, eventAdd.StatusCode);
        using var eventUpdate = await host.Client.PutAsJsonAsync($"/scenarios/{id}/events/{addedEvent.Id}", new EventWriteRequest(4, addedEvent with { PayloadJson = "{\"note\":\"updated\"}" }), Json, Token);
        Assert.Equal(HttpStatusCode.OK, eventUpdate.StatusCode);
        using var eventDelete = await host.Client.DeleteAsync($"/scenarios/{id}/events/{addedEvent.Id}?revision=5", Token);
        Assert.Equal(HttpStatusCode.OK, eventDelete.StatusCode);
        var events = (await host.Client.GetFromJsonAsync<ScenarioItems<ScenarioInputEvent>>($"/scenarios/{id}/events", Json, Token))!;
        Assert.Equal(6, events.Revision);
        Assert.Single(events.Items);
    }

    [Fact]
    public async Task Run_executes_only_captured_inputs_after_scenario_and_live_layout_mutate()
    {
        await using var host = await ManagementHost.CreateAsync();
        var request = await Request(host);
        var scenario = await Create(host, request);
        var run = await Start(host, scenario.Scenario.Id);
        Assert.Equal("planned", run.Status);
        var pending = (await Read(host, run.Id))!;
        Assert.False(pending.ResultsValid); Assert.Empty(pending.Metrics); Assert.Empty(pending.Snapshots);
        using var updated = await host.Client.PutAsJsonAsync($"/scenarios/{scenario.Scenario.Id}", request with
            { Revision = 1, Inputs = request.Inputs with { Seed = 99, DurationMinutes = 120 } }, Json, Token);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TwinDbContext>();
            await db.Checkpoints.Where(x => x.SceneId == SceneId).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.CapacityPerMinute, 999), Token);
        }
        Assert.True(await host.Dispatch(Token));
        var done = (await Read(host, run.Id))!;
        Assert.Equal("completed", done.Status); Assert.True(done.ResultsValid);
        SameSnapshot(run.InputSnapshotJson, done.InputSnapshotJson);
        Assert.Equal(42, host.Engine.LastRequest!.Seed); Assert.Equal(60, host.Engine.LastRequest.DurationMinutes);
        Assert.DoesNotContain(host.Engine.LastRequest.Checkpoints, x => x.ServiceRatePerMinute == 999);
        Assert.NotEmpty(done.Metrics); Assert.NotEmpty(done.Snapshots); Assert.Null(done.Error);
        var metrics = (await host.Client.GetFromJsonAsync<RunResults<RunMetric>>($"/runs/{run.Id}/metrics?limit=100", Json, Token))!;
        Assert.True(metrics.IsValid); Assert.Equal(done.Metrics.Count, metrics.Items.Count);
        using var forbidden = await host.Client.PatchAsJsonAsync($"/runs/{run.Id}/status", new RunTransitionRequest("failed", done.Revision, "overwrite"), Json, Token);
        Assert.Equal(HttpStatusCode.Conflict, forbidden.StatusCode);
    }

    [Fact]
    public async Task Failed_run_has_readable_error_and_retry_preserves_snapshot_with_one_child_per_failure()
    {
        await using var host = await ManagementHost.CreateAsync();
        var request = await Request(host);
        var scenario = await Create(host, request);
        var run = await Start(host, scenario.Scenario.Id);
        host.Engine.Behavior = (_, _) => throw new SimulationEngineException("engine_unavailable", "Simulation service is unavailable.");
        await host.Dispatch(Token);
        var failed = (await Read(host, run.Id))!;
        Assert.Equal("failed", failed.Status); Assert.Equal("engine_unavailable", failed.Error!.Code);
        Assert.Empty(failed.Metrics); Assert.Empty(failed.Snapshots); Assert.False(failed.ResultsValid);
        using var changed = await host.Client.PutAsJsonAsync($"/scenarios/{scenario.Scenario.Id}", request with
            { Revision = 1, Inputs = request.Inputs with { Seed = 99 } }, Json, Token);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        using var stale = await host.Client.PostAsJsonAsync($"/runs/{run.Id}/retry", new RevisionRequest(1), Json, Token);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => host.Client.PostAsJsonAsync($"/runs/{run.Id}/retry", new RevisionRequest(failed.Revision), Json, Token)));
        try
        {
            Assert.Single(attempts, x => x.StatusCode == HttpStatusCode.Accepted);
            Assert.Single(attempts, x => x.StatusCode == HttpStatusCode.Conflict);
            var retry = (await attempts.Single(x => x.StatusCode == HttpStatusCode.Accepted).Content.ReadFromJsonAsync<SimulationRun>(Json, Token))!;
            Assert.NotEqual(run.Id, retry.Id); Assert.Equal(run.Id, retry.RetryOfRunId);
            SameSnapshot(run.InputSnapshotJson, retry.InputSnapshotJson); Assert.Equal(42, retry.Seed); Assert.Equal(1, retry.ScenarioRevision);
            host.Engine.Behavior = null; await host.Dispatch(Token);
            Assert.Equal("completed", (await Read(host, retry.Id))!.Status);
            Assert.Equal("failed", (await Read(host, run.Id))!.Status);
            using var purged = await host.Client.DeleteAsync($"/scenarios/{scenario.Scenario.Id}?retention=purge&revision=2", Token);
            Assert.Equal(HttpStatusCode.OK, purged.StatusCode);
            using var absent = await host.Client.GetAsync($"/runs/{retry.Id}", Token);
            Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        }
        finally { foreach (var attempt in attempts) attempt.Dispose(); }
    }

    [Fact]
    public async Task Cancellation_leaves_durable_running_job_and_recovery_uses_the_same_run()
    {
        await using var host = await ManagementHost.CreateAsync();
        var scenario = await Create(host, await Request(host));
        var run = await Start(host, scenario.Scenario.Id);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Engine.Behavior = async (_, token) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException(); };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var dispatch = host.Dispatch(cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await dispatch);
        Assert.Equal("running", (await Read(host, run.Id))!.Status);
        host.Engine.Behavior = null;
        await host.Dispatch(Token);
        Assert.Equal("completed", (await Read(host, run.Id))!.Status);
    }

    [Fact]
    public async Task Competing_dispatchers_never_compute_or_write_the_same_run_twice()
    {
        await using var host = await ManagementHost.CreateAsync();
        var scenario = await Create(host, await Request(host));
        var run = await Start(host, scenario.Scenario.Id);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<SimulationResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Engine.Behavior = (request, _) => { entered.SetResult(); return release.Task; };
        var first = host.Dispatch(Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        try { Assert.False(await host.Dispatch(Token)); }
        finally
        {
            var request = host.Engine.LastRequest!;
            release.SetResult(RecordingEngine.Response(request));
        }
        Assert.True(await first);
        var metrics = (await Read(host, run.Id))!.Metrics;
        Assert.Equal(host.Engine.LastRequest!.Checkpoints.Count, metrics.Count(metric => metric.Name == "processedPassengers"));
        Assert.Equal(metrics.Count, metrics.Select(metric => (metric.ZoneId, metric.Name)).Distinct().Count());
    }

    [Fact]
    public async Task Wrong_engine_correlation_fails_without_persisting_results()
    {
        await using var host = await ManagementHost.CreateAsync();
        var scenario = await Create(host, await Request(host));
        var run = await Start(host, scenario.Scenario.Id);
        host.Engine.Behavior = (request, _) => Task.FromResult(new SimulationResponse("1.0", Guid.NewGuid(), "completed", 60, [], [], null));
        await host.Dispatch(Token);
        var result = (await Read(host, run.Id))!;
        Assert.Equal("failed", result.Status); Assert.Equal("engine_contract_invalid", result.Error!.Code);
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TwinDbContext>();
        Assert.False(await db.Metrics.AnyAsync(x => x.RunId == run.Id, Token));
        Assert.False(await db.Snapshots.AnyAsync(x => x.RunId == run.Id, Token));
    }

    [Fact]
    public async Task OpenAPI_documents_required_bodies_results_and_status_codes()
    {
        await using var host = await ManagementHost.CreateAsync();
        var document = await host.Client.GetFromJsonAsync<JsonElement>("/openapi/v1.json", Json, Token);
        var paths = document.GetProperty("paths");
        foreach (var path in new[] { "/api/airports", "/scenes", "/scenarios", "/scenarios/{id}/flights", "/scenarios/{id}/events", "/scenarios/{id}/runs", "/runs/{runId}", "/runs/{runId}/snapshots", "/runs/{runId}/retry" })
            Assert.True(paths.TryGetProperty(path, out _), path);
        var scenario = paths.GetProperty("/scenarios").GetProperty("post");
        Assert.True(scenario.GetProperty("requestBody").GetProperty("required").GetBoolean());
        foreach (var status in new[] { "201", "400", "422", "404", "409" }) Assert.True(scenario.GetProperty("responses").TryGetProperty(status, out _));
        Assert.True(paths.GetProperty("/scenarios/{id}/runs").GetProperty("post").GetProperty("responses").TryGetProperty("202", out _));
    }

    [Fact]
    public async Task Hosted_worker_computes_with_real_FastAPI_and_persists_results()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SIMULATION_TEST_URL")), "Set SIMULATION_TEST_URL or run the Compose management suite.");
        await using var host = await ManagementHost.CreateAsync(worker: true, realEngine: true);
        var scenario = await Create(host, await Request(host));
        var run = await Start(host, scenario.Scenario.Id);
        RunDetails? done = null;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        do
        {
            done = await Read(host, run.Id);
            if (done!.Status is "completed" or "failed") break;
            await Task.Delay(100, Token);
        } while (DateTime.UtcNow < deadline);
        Assert.Equal("completed", done!.Status);
        Assert.NotEmpty(done.Metrics); Assert.NotEmpty(done.Snapshots); Assert.Null(done.Error);
        SameSnapshot(run.InputSnapshotJson, done.InputSnapshotJson);
    }
}

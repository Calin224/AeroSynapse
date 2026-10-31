using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Simulation;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using AeroSynapse.Infrastructure.Execution;
using AeroSynapse.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace AeroSynapse.Management.Tests;

public sealed class BatchTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static async Task<(ScenarioDetails Baseline, ScenarioDetails Candidate)> Scenarios(ManagementHost host)
    {
        var template = (await host.DraftAsync()).Request;
        return (await host.PostScenarioAsync(template with { Code = "batch-baseline", Name = "batch-baseline" }),
            await host.PostScenarioAsync(template with { Code = "batch-candidate", Name = "batch-candidate" }));
    }
    private static CreateBatchRequest Request((ScenarioDetails Baseline, ScenarioDetails Candidate) scenarios, int limit = 60) =>
        new(Guid.NewGuid(), "Monte Carlo DEMO", new(scenarios.Baseline.Scenario.Id, 1), [new(scenarios.Candidate.Scenario.Id, 1)], [42, 7, 99], limit);
    private static async Task<BatchDetails> Create(ManagementHost host, CreateBatchRequest request)
    {
        using var response = await host.Client.PostAsJsonAsync("/simulation-batches", request, Json, Token);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal($"/simulation-batches/{request.CommandId}", response.Headers.Location!.OriginalString);
        return (await response.Content.ReadFromJsonAsync<BatchDetails>(Json, Token))!;
    }
    private static async Task<bool> Step(ManagementHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<BatchProcessor>().ProcessNextAsync(Token);
    }
    private static async Task Drain(ManagementHost host)
    { for (var index = 0; index < 40; index++) if (!await Step(host)) return; Assert.Fail("Batch queue did not drain."); }
    private static Task<BatchDetails?> Read(ManagementHost host, Guid id) => host.Client.GetFromJsonAsync<BatchDetails>($"/simulation-batches/{id}", Json, Token);

    [Fact]
    public async Task Seed_matrix_is_frozen_idempotent_and_replay_ignores_later_scenario_edits()
    {
        await using var host = await ManagementHost.CreateAsync(); var scenarios = await Scenarios(host); var request = Request(scenarios);
        var replies = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Create(host, request)));
        var batch = replies[0]; Assert.Equal([42, 7, 99], batch.Seeds); Assert.Equal(6, batch.Replications.Count);
        Assert.All(batch.Variants, variant => Assert.Equal(request.Seeds, batch.Replications.Where(r => r.VariantId == variant.Id).OrderBy(r => r.SeedIndex).Select(r => r.Seed)));
        Assert.False(await host.Dispatch(Token)); // Regular worker cannot consume batch-owned runs.
        using var conflict = await host.Client.PostAsJsonAsync("/simulation-batches", request with { Seeds = [1] }, Json, Token);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var source = scenarios.Baseline;
        using var edited = await host.Client.PutAsJsonAsync($"/scenarios/{source.Scenario.Id}", new ScenarioWriteRequest(source.Scenario.Code,
            "Edited after capture", source.Scenario.Description, source.Scenario.SceneId, source.Scenario.LayoutVersionId, source.Inputs with { Seed = 123 }, 1), Json, Token);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        await Drain(host); batch = (await Read(host, batch.Batch.Id))!;
        Assert.Equal("completed", batch.Batch.Status); Assert.All(batch.Replications, r => Assert.True(r.ResultsValid));
        Assert.All(batch.Metrics, row => Assert.Equal(3, row.Values.Count));
        Assert.All(batch.Comparisons, row => { Assert.Equal(3, row.Differences.Count); Assert.Equal(0, row.Differences.Mean); Assert.Empty(row.ExcludedSeeds); });
        var replayRequest = new ReplayBatchRequest(Guid.NewGuid(), "Original captured matrix");
        using var replayReply = await host.Client.PostAsJsonAsync($"/simulation-batches/{batch.Batch.Id}/replay", replayRequest, Json, Token);
        Assert.Equal(HttpStatusCode.Accepted, replayReply.StatusCode);
        var replay = (await replayReply.Content.ReadFromJsonAsync<BatchDetails>(Json, Token))!;
        Assert.All(replay.Variants, row => Assert.Equal(1, row.ScenarioRevision)); Assert.Equal(batch.Seeds, replay.Seeds);
        await Drain(host); replay = (await Read(host, replay.Batch.Id))!;
        foreach (var original in batch.Replications)
        {
            var position = batch.Variants.Single(v => v.Id == original.VariantId).Position;
            var variantId = replay.Variants.Single(v => v.Position == position).Id;
            var repeated = replay.Replications.Single(r => r.VariantId == variantId && r.Seed == original.Seed);
            Assert.NotEqual(original.RunId, repeated.RunId); Assert.Equal(original.Provenance.InputHash, repeated.Provenance.InputHash);
            Assert.Equal(original.Provenance.ResultHash, repeated.Provenance.ResultHash);
        }
    }

    [Fact]
    public async Task Failed_replications_and_missing_pairs_are_visible_and_not_counted_as_zero()
    {
        await using var host = await ManagementHost.CreateAsync(); var scenarios = await Scenarios(host);
        host.Engine.Behavior = (request, _) => Task.FromResult(request.Seed == 7 && request.ScenarioId == scenarios.Candidate.Scenario.Id
            ? new SimulationResponse("1.0", request.RunId, "failed", 0, [], [], new("synthetic_failure", "Deliberate replication failure."))
            : RecordingEngine.Response(request));
        var batch = await Create(host, Request(scenarios)); await Drain(host); batch = (await Read(host, batch.Batch.Id))!;
        Assert.Equal("completed_with_failures", batch.Batch.Status);
        var failed = Assert.Single(batch.Replications, r => r.Status == "failed"); Assert.False(failed.ResultsValid); Assert.Contains("synthetic_failure", failed.FailureReason!);
        Assert.All(batch.Comparisons, row => { Assert.Equal(2, row.Differences.Count); Assert.Equal([42, 99], row.IncludedSeeds); Assert.Equal(7, Assert.Single(row.ExcludedSeeds).Seed); });
        var variant = batch.Variants.Single(v => v.Position == 1);
        Assert.All(batch.Metrics.Where(row => row.VariantId == variant.Id), row => { Assert.Equal(2, row.Values.Count); Assert.Equal(1, row.MissingOrUnsuccessfulCount); });
        using var retry = await host.Client.PostAsJsonAsync($"/runs/{failed.RunId}/retry", new RevisionRequest(3), Json, Token); Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
    }

    [Theory]
    [InlineData("duplicate")][InlineData("negative")][InlineData("empty")][InlineData("too-many")][InlineData("deadline")][InlineData("same-scenario")][InlineData("stale")]
    public async Task Invalid_requests_are_atomic_and_return_explicit_errors(string kind)
    {
        await using var host = await ManagementHost.CreateAsync(); var scenarios = await Scenarios(host); var request = Request(scenarios);
        request = kind switch { "duplicate" => request with { Seeds = [42, 42] }, "negative" => request with { Seeds = [-1] }, "empty" => request with { Seeds = [] },
            "too-many" => request with { Seeds = Enumerable.Range(0, 26).ToArray() }, "deadline" => request with { TimeLimitSeconds = 0 },
            "same-scenario" => request with { Candidates = [request.Baseline] }, _ => request with { Baseline = request.Baseline with { Revision = 7 } } };
        using var reply = await host.Client.PostAsJsonAsync("/simulation-batches", request, Json, Token);
        Assert.Equal(kind == "stale" ? HttpStatusCode.Conflict : HttpStatusCode.BadRequest, reply.StatusCode);
        Assert.Contains("code", await reply.Content.ReadAsStringAsync(Token));
        Assert.Empty((await host.Client.GetFromJsonAsync<BatchSummary[]>("/simulation-batches", Json, Token))!);
        Assert.False(await Step(host));
    }

    [Fact]
    public async Task Cancellation_before_dispatch_preserves_manifest_and_never_computes()
    {
        await using var host = await ManagementHost.CreateAsync(); var batch = await Create(host, Request(await Scenarios(host)));
        using var cancel = await host.Client.PostAsync($"/simulation-batches/{batch.Batch.Id}/cancel", null, Token); Assert.Equal(HttpStatusCode.Accepted, cancel.StatusCode);
        await Drain(host); batch = (await Read(host, batch.Batch.Id))!;
        Assert.Equal("cancelled", batch.Batch.Status); Assert.Null(host.Engine.LastRequest); Assert.All(batch.Replications, r => Assert.Equal("cancelled", r.Status));
        Assert.All(batch.Replications, r => { Assert.False(r.ResultsValid); Assert.Contains("not started", r.FailureReason!); });
        using var again = await host.Client.PostAsync($"/simulation-batches/{batch.Batch.Id}/cancel", null, Token); Assert.Equal(HttpStatusCode.Accepted, again.StatusCode);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task Active_batch_can_be_cancelled_or_expire_and_remote_cancellation_is_acknowledged(bool expire)
    {
        var transport = new CancellationClient(); await using var host = await ManagementHost.CreateAsync(client: transport);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Engine.Behavior = async (_, token) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException(); };
        var batch = await Create(host, Request(await Scenarios(host), expire ? 1 : 60));
        var processing = Step(host); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        if (!expire) using (var cancelled = await host.Client.PostAsync($"/simulation-batches/{batch.Batch.Id}/cancel", null, Token)) Assert.Equal(HttpStatusCode.Accepted, cancelled.StatusCode);
        await processing.WaitAsync(TimeSpan.FromSeconds(8), Token); await Drain(host); batch = (await Read(host, batch.Batch.Id))!;
        Assert.Equal(expire ? "timed_out" : "cancelled", batch.Batch.Status); Assert.Single(transport.Cancellations);
        Assert.All(batch.Replications, r => { Assert.False(r.ResultsValid); Assert.False(r.RemoteCancellationPending); }); Assert.Empty(batch.Metrics);
    }

    [Fact]
    public async Task Unavailable_remote_cancellation_remains_visible_without_starving_the_next_batch()
    {
        var transport = new CancellationClient { Unavailable = true }; await using var host = await ManagementHost.CreateAsync(client: transport);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Engine.Behavior = async (_, token) => { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException(); };
        var scenarios = await Scenarios(host); var first = await Create(host, Request(scenarios));
        var processing = Step(host); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        using var cancelled = await host.Client.PostAsync($"/simulation-batches/{first.Batch.Id}/cancel", null, Token);
        Assert.Equal(HttpStatusCode.Accepted, cancelled.StatusCode); await processing.WaitAsync(TimeSpan.FromSeconds(8), Token);
        host.Engine.Behavior = null; var next = await Create(host, Request(scenarios));
        await Step(host);
        Assert.Single((await Read(host, next.Batch.Id))!.Replications, r => r.ResultsValid);
        Assert.Contains((await Read(host, first.Batch.Id))!.Replications, r => r.RemoteCancellationPending);
        Assert.Single(transport.Cancellations);
    }

    [Fact]
    public async Task Cancellation_also_stops_a_replication_whose_response_previously_failed_validation()
    {
        var transport = new CancellationClient(); await using var host = await ManagementHost.CreateAsync(client: transport);
        host.Engine.Behavior = (request, _) => Task.FromResult(RecordingEngine.Response(request) with { RunId = Guid.NewGuid() });
        var batch = await Create(host, Request(await Scenarios(host))); await Step(host);
        batch = (await Read(host, batch.Batch.Id))!;
        var failed = Assert.Single(batch.Replications, r => r.Status == "failed");
        Assert.Contains("engine_contract_invalid", failed.FailureReason!);
        using var cancelled = await host.Client.PostAsync($"/simulation-batches/{batch.Batch.Id}/cancel", null, Token);
        Assert.Equal(HttpStatusCode.Accepted, cancelled.StatusCode);
        await Drain(host); batch = (await Read(host, batch.Batch.Id))!;
        Assert.Equal("cancelled", batch.Batch.Status);
        Assert.Equal(failed.RunId, Assert.Single(transport.Cancellations));
        Assert.All(batch.Replications, r => Assert.False(r.RemoteCancellationPending));
        Assert.Equal("failed", batch.Replications.Single(r => r.Id == failed.Id).Status);
    }

    [Fact]
    public async Task Engine_build_changes_are_explicit_failures_and_cannot_contaminate_paired_statistics()
    {
        await using var host = await ManagementHost.CreateAsync(); var batch = await Create(host, Request(await Scenarios(host)));
        await Step(host); host.Engine.Build = new string('d', 64); await Drain(host);
        batch = (await Read(host, batch.Batch.Id))!;
        Assert.Equal(new string('c', 64), batch.Batch.EngineBuild);
        Assert.Equal("completed_with_failures", batch.Batch.Status);
        Assert.Single(batch.Replications, r => r.ResultsValid);
        Assert.All(batch.Replications.Where(r => !r.ResultsValid), r => Assert.Contains("batch_engine_changed", r.FailureReason!));
        Assert.All(batch.Comparisons, row => { Assert.Equal(0, row.Differences.Count); Assert.Null(row.Differences.Mean); Assert.Equal(3, row.ExcludedSeeds.Count); });
    }

    [Fact]
    public async Task Manifest_and_replication_seeds_are_protected_and_retained_by_scenarios()
    {
        await using var host = await ManagementHost.CreateAsync(); var scenarios = await Scenarios(host); var batch = await Create(host, Request(scenarios));
        await using var scope = host.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<TwinDbContext>();
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE twin.simulation_batches SET \"SeedsJson\" = '[1]'::jsonb WHERE \"Id\" = {batch.Batch.Id}", Token));
        var replica = batch.Replications[0];
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE twin.batch_replications SET \"Seed\" = 123 WHERE \"Id\" = {replica.Id}", Token));
        using var manual = await host.Client.PatchAsJsonAsync($"/runs/{replica.RunId}/status", new RunTransitionRequest("running", 1), Json, Token); Assert.Equal(HttpStatusCode.Conflict, manual.StatusCode);
        using var purge = await host.Client.DeleteAsync($"/scenarios/{scenarios.Baseline.Scenario.Id}?retention=purge&revision=1", Token); Assert.Equal(HttpStatusCode.Conflict, purge.StatusCode);
        using var archived = await host.Client.DeleteAsync($"/scenarios/{scenarios.Baseline.Scenario.Id}?retention=archive&revision=1", Token); Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
        var resetError = await Assert.ThrowsAsync<InvalidOperationException>(() => SyntheticScenarioSeeder.ResetAsync(db, Token));
        Assert.Contains("Retained Monte Carlo batches", resetError.Message);
        await Drain(host); Assert.Equal("completed", (await Read(host, batch.Batch.Id))!.Batch.Status);
    }

    [Fact]
    public async Task Concurrent_workers_execute_each_replication_once_and_real_engine_shows_seed_variability()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SIMULATION_TEST_URL")), "Set SIMULATION_TEST_URL for controlled FastAPI execution.");
        await using var host = await ManagementHost.CreateAsync(realEngine: true); var batch = await Create(host, Request(await Scenarios(host)));
        for (var round = 0; round < 8; round++) await Task.WhenAll(Step(host), Step(host));
        batch = (await Read(host, batch.Batch.Id))!;
        Assert.Equal("completed", batch.Batch.Status); Assert.All(batch.Replications, r => Assert.True(r.ResultsValid));
        Assert.Contains(batch.Metrics, row => row.Values.StandardDeviation > 0);
        Assert.All(batch.Comparisons, row => { Assert.Equal(3, row.Differences.Count); Assert.Equal(0, row.Differences.Mean); });
    }

    private sealed class CancellationClient : IIntelligenceClient
    {
        public List<Guid> Cancellations { get; } = [];
        public bool Unavailable { get; init; }
        public Task CancelAsync(Guid runId, string correlationId, CancellationToken token)
        {
            Cancellations.Add(runId);
            return Unavailable ? Task.FromException(new HttpRequestException("Remote engine unavailable.")) : Task.CompletedTask;
        }
        public Task<EngineExecution> StartAsync(SimulationRequest request, string correlationId, CancellationToken token) => throw new NotSupportedException();
        public Task<EngineExecution?> GetAsync(Guid runId, string correlationId, CancellationToken token) => throw new NotSupportedException();
    }
}

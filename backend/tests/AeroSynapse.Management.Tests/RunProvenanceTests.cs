using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Simulation;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using AeroSynapse.Infrastructure.Simulation;
using AeroSynapse.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace AeroSynapse.Management.Tests;

public sealed class RunProvenanceTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static async Task<RunDetails> Run(ManagementHost host, Guid scenarioId)
    {
        var run = await host.StartRunAsync(scenarioId);
        Assert.Matches("^[0-9a-f]{64}$", run.InputHash!);
        return (await host.Client.GetFromJsonAsync<RunDetails>($"/runs/{run.Id}", Json, Token))!;
    }
    private static Task<RunComparison?> Compare(ManagementHost host, Guid a, Guid b) =>
        host.Client.GetFromJsonAsync<RunComparison>($"/runs/{a}/compare/{b}", Json, Token);

    [Fact]
    public async Task Identical_runs_and_changed_inputs_have_persisted_comparable_provenance()
    {
        await using var host = await ManagementHost.CreateAsync();
        var scenario = await host.CreateScenarioAsync();
        var first = await Run(host, scenario.Scenario.Id);
        var second = await Run(host, scenario.Scenario.Id);
        var same = (await Compare(host, first.Id, second.Id))!;
        Assert.True(same.InputsMatch); Assert.True(same.VersionsMatch); Assert.True(same.ResultsMatch);
        Assert.NotEmpty(same.Metrics); Assert.All(same.Metrics, x => Assert.Equal(0, x.Delta));
        Assert.True(first.Provenance!.EngineMilliseconds >= 0); Assert.True(first.Provenance.ExecutionMilliseconds >= 0);
        Assert.Equal(first.Id.ToString("N"), first.Provenance.CorrelationId);
        Assert.NotEqual(first.Id, second.Id);

        using var changed = await host.Client.PutAsJsonAsync($"/scenarios/{scenario.Scenario.Id}",
            new ScenarioWriteRequest(scenario.Scenario.Code, scenario.Scenario.Name, scenario.Scenario.Description,
                scenario.Scenario.SceneId, scenario.Scenario.LayoutVersionId, scenario.Inputs with { Seed = 43 }, 1), Json, Token);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var third = await Run(host, scenario.Scenario.Id);
        var different = (await Compare(host, first.Id, third.Id))!;
        Assert.False(different.InputsMatch); Assert.True(different.VersionsMatch);
        Assert.False(different.Comparability!.Comparable);
        Assert.Contains(different.Comparability.Issues, issue => issue.Code == "seed_mismatch");
        Assert.All(different.Metrics, row => { Assert.Null(row.Delta); Assert.Null(row.PercentChange); });
        Assert.Equal(1, different.Baseline.ScenarioRevision); Assert.Equal(2, different.Candidate.ScenarioRevision);
        Assert.Equal(first.Provenance.InputHash, different.Baseline.Provenance.InputHash);
        host.Engine.Build = new string('d', 64);
        var fourth = await Run(host, scenario.Scenario.Id);
        var rebuilt = (await Compare(host, third.Id, fourth.Id))!;
        Assert.True(rebuilt.InputsMatch); Assert.False(rebuilt.VersionsMatch);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync($"/runs/{first.Id}/compare/{Guid.NewGuid()}", Token)).StatusCode);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TwinDbContext>();
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE twin.runs SET \"InputHash\" = {new string('a', 64)} WHERE \"Id\" = {first.Id}", Token));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    [Fact]
    public async Task Unexpected_managed_failure_is_terminal_diagnostic_and_has_no_valid_results()
    {
        await using var host = await ManagementHost.CreateAsync();
        var scenario = await host.CreateScenarioAsync();
        var completed = await Run(host, scenario.Scenario.Id);
        host.Engine.Behavior = (_, _) => throw new InvalidOperationException("private execution detail");
        var failed = await Run(host, scenario.Scenario.Id);
        Assert.Equal("failed", failed.Status); Assert.False(failed.ResultsValid);
        Assert.Equal("execution_unexpected", failed.Error!.Code);
        Assert.Contains(failed.Provenance!.CorrelationId, failed.Error.Message);
        Assert.DoesNotContain("private execution detail", failed.FailureReason!);
        Assert.Empty(failed.Metrics); Assert.Empty(failed.Snapshots); Assert.Null(failed.Provenance.ResultHash);
        var comparison = (await Compare(host, completed.Id, failed.Id))!;
        Assert.Null(comparison.ResultsMatch); Assert.False(comparison.Candidate.ResultsValid);
        Assert.All(comparison.Metrics, row => { Assert.Null(row.Candidate); Assert.Null(row.Delta); });
        Assert.False(await host.Dispatch(Token));
    }

    [Fact]
    public async Task Unexpected_idempotent_worker_failure_keeps_the_original_correlation_and_failed_status()
    {
        await using var host = await ManagementHost.CreateAsync();
        var scenario = await host.CreateScenarioAsync();
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TwinDbContext>();
        var command = await new SimulationService(db).StartAsync(scenario.Scenario.Id, Guid.NewGuid(), 1, "us025-diagnostic", Token);
        var worker = new SimulationProcessor(db, new BrokenClient(), Options.Create(new SimulationOptions()), NullLogger<SimulationProcessor>.Instance);
        Assert.True(await worker.ProcessNextAsync(Token));
        var failed = (await host.Client.GetFromJsonAsync<RunDetails>($"/runs/{command.RunId}", Json, Token))!;
        Assert.Equal("failed", failed.Status); Assert.Equal("us025-diagnostic", failed.Provenance!.CorrelationId);
        Assert.Contains("us025-diagnostic", failed.FailureReason!); Assert.Null(failed.Provenance.ResultHash);
        Assert.Empty(failed.Metrics); Assert.Empty(failed.Snapshots); Assert.False(await worker.ProcessNextAsync(Token));
    }

    [Fact]
    public async Task Real_FastAPI_receipts_have_cross_language_hashes_and_repeatable_results()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SIMULATION_TEST_URL")), "Set SIMULATION_TEST_URL to include actual FastAPI execution.");
        await using var host = await ManagementHost.CreateAsync(realEngine: true);
        var scenario = await host.CreateScenarioAsync();
        var first = await Run(host, scenario.Scenario.Id);
        var second = await Run(host, scenario.Scenario.Id);
        Assert.Equal("completed", first.Status); Assert.Equal("completed", second.Status);
        var comparison = (await Compare(host, first.Id, second.Id))!;
        Assert.True(comparison.InputsMatch); Assert.True(comparison.VersionsMatch); Assert.True(comparison.ResultsMatch);
        Assert.True(first.Provenance!.EngineMilliseconds > 0); Assert.Matches("^[0-9a-f]{64}$", first.Provenance.EngineBuild!);
        using var edited = await host.Client.PutAsJsonAsync($"/scenarios/{scenario.Scenario.Id}",
            new ScenarioWriteRequest(scenario.Scenario.Code, scenario.Scenario.Name, scenario.Scenario.Description,
                scenario.Scenario.SceneId, scenario.Scenario.LayoutVersionId, scenario.Inputs with { Seed = 43 }, 1), Json, Token);
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var third = await Run(host, scenario.Scenario.Id);
        var different = (await Compare(host, first.Id, third.Id))!;
        Assert.False(different.InputsMatch); Assert.True(different.VersionsMatch); Assert.False(different.ResultsMatch);
    }

    private sealed class BrokenClient : IIntelligenceClient
    {
        public Task<EngineExecution> StartAsync(AeroSynapse.Application.Contracts.SimulationRequest request, string correlationId, CancellationToken token) =>
            throw new InvalidOperationException("private worker detail");
        public Task<EngineExecution?> GetAsync(Guid runId, string correlationId, CancellationToken token) => throw new InvalidOperationException();
    }

    [Fact]
    public async Task Pending_fingerprints_are_immutable_and_legacy_comparisons_report_unknown()
    {
        await using var host = await ManagementHost.CreateAsync();
        var scenario = await host.CreateScenarioAsync();
        var run = await host.StartRunAsync(scenario.Scenario.Id, dispatch: false);
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TwinDbContext>();
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE twin.runs SET \"InputHash\" = {new string('a', 64)} WHERE \"Id\" = {run.Id}", Token));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE twin.runs SET \"EngineVersion\" = 'tampered' WHERE \"Id\" = {run.Id}", Token));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE twin.runs SET \"EngineMilliseconds\" = 1 WHERE \"Id\" = {run.Id}", Token));
        Assert.True(await host.Dispatch(Token));
        var legacy = await db.Runs.Where(x => x.ScenarioId == null).Select(x => x.Id).SingleAsync(Token);
        var comparison = (await Compare(host, legacy, run.Id))!;
        Assert.Null(comparison.InputsMatch); Assert.Null(comparison.VersionsMatch); Assert.Null(comparison.ResultsMatch);
    }

    [Fact]
    public async Task Known_engine_failure_keeps_diagnostics_and_retry_preserves_the_input_fingerprint()
    {
        await using var host = await ManagementHost.CreateAsync();
        var scenario = await host.CreateScenarioAsync();
        host.Engine.Behavior = (request, _) => Task.FromResult(new AeroSynapse.Application.Contracts.SimulationResponse(
            "1.0", request.RunId, "failed", 0, [], [], new("simulation_limit_exceeded", "Synthetic work limit exceeded.")));
        var failed = await Run(host, scenario.Scenario.Id);
        Assert.Equal("failed", failed.Status); Assert.NotNull(failed.Provenance!.EngineBuild);
        Assert.Equal(2.5, failed.Provenance.EngineMilliseconds); Assert.Null(failed.Provenance.ResultHash);
        using var reply = await host.Client.PostAsJsonAsync($"/runs/{failed.Id}/retry", new RevisionRequest(failed.Revision), Json, Token);
        Assert.Equal(HttpStatusCode.Accepted, reply.StatusCode);
        var retry = (await reply.Content.ReadFromJsonAsync<SimulationRun>(Json, Token))!;
        Assert.Equal(failed.Provenance.InputHash, retry.InputHash); Assert.Equal(failed.Id, retry.RetryOfRunId);
        host.Engine.Behavior = null; Assert.True(await host.Dispatch(Token));
        var comparison = (await Compare(host, failed.Id, retry.Id))!;
        Assert.True(comparison.InputsMatch); Assert.True(comparison.VersionsMatch); Assert.Null(comparison.ResultsMatch);
        Assert.True(comparison.Candidate.ResultsValid);
    }

    [Fact]
    public async Task A_forged_engine_fingerprint_fails_without_persisting_valid_results()
    {
        await using var host = await ManagementHost.CreateAsync();
        var scenario = await host.CreateScenarioAsync();
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TwinDbContext>();
        var command = await new SimulationService(db).StartAsync(scenario.Scenario.Id, Guid.NewGuid(), 1, "us025-forged", Token);
        var worker = new SimulationProcessor(db, new CorruptClient(), Options.Create(new SimulationOptions()), NullLogger<SimulationProcessor>.Instance);
        Assert.True(await worker.ProcessNextAsync(Token));
        var failed = (await host.Client.GetFromJsonAsync<RunDetails>($"/runs/{command.RunId}", Json, Token))!;
        Assert.Equal("failed", failed.Status); Assert.Equal("engine_invalid_diagnostics", failed.Error!.Code);
        Assert.Empty(failed.Metrics); Assert.Empty(failed.Snapshots); Assert.Null(failed.Provenance!.ResultHash);
    }

    private sealed class CorruptClient : IIntelligenceClient
    {
        public Task<EngineExecution> StartAsync(AeroSynapse.Application.Contracts.SimulationRequest request, string correlationId, CancellationToken token) =>
            Task.FromResult(new EngineExecution(request.RunId, "completed", RecordingEngine.Response(request), Diagnostics:
                new(new string('0', 64), new string('0', 64), RunFingerprint.Engine(request), new string('c', 64), 1)));
        public Task<EngineExecution?> GetAsync(Guid runId, string correlationId, CancellationToken token) => throw new InvalidOperationException();
    }
}

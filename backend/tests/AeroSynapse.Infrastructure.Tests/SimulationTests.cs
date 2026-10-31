using System.Net;
using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Simulation;
using AeroSynapse.Infrastructure.Operations;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using AeroSynapse.Infrastructure.Simulation;
using AeroSynapse.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AeroSynapse.Infrastructure.Tests;

public sealed class SimulationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly IOptions<SimulationOptions> Options = Microsoft.Extensions.Options.Options.Create(new SimulationOptions());

    [Fact]
    public async Task Concurrent_commands_create_one_pending_run_and_reusing_key_with_different_inputs_conflicts()
    {
        await using var test = await TestDatabase.CreateAsync();
        var scenario = await Prepare(test);
        var key = Guid.NewGuid();
        async Task<SimulationExecution> Start()
        {
            await using var db = test.NewContext();
            return await new SimulationService(db).StartAsync(scenario.Scenario.Id, key, 1, "trace-concurrent", Token);
        }
        var replies = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Start()));
        Assert.Single(replies.Select(x => x.RunId).Distinct());
        Assert.All(replies, x => Assert.Equal("pending", x.Status));
        Assert.Equal(1, await test.Context.SimulationCommands.CountAsync(Token));
        await Assert.ThrowsAsync<ScenarioConflictException>(() => new SimulationService(test.Context).StartAsync(scenario.Scenario.Id, key, 2, "other-trace", Token));
        await Assert.ThrowsAsync<ScenarioConflictException>(() => new SimulationService(test.Context).StartAsync(Guid.NewGuid(), key, 1, "other-trace", Token));
    }

    [Fact]
    public async Task Successful_engine_result_and_terminal_state_are_persisted_and_reopened_without_reexecution()
    {
        await using var test = await TestDatabase.CreateAsync();
        var scenario = await Prepare(test);
        var key = Guid.NewGuid();
        var execution = await new SimulationService(test.Context).StartAsync(scenario.Scenario.Id, key, 1, "trace-success", Token);
        var engine = new FakeEngine();
        await Process(test, engine);
        await using var fresh = test.NewContext();
        var completed = await new SimulationService(fresh).GetAsync(key, Token);
        Assert.Equal("completed", completed!.Status);
        Assert.True(completed.ResultsValid);
        Assert.Equal("trace-success", engine.CorrelationId);
        Assert.Equal(3, await fresh.Metrics.CountAsync(x => x.RunId == execution.RunId, Token));
        Assert.Single(await fresh.Snapshots.Where(x => x.RunId == execution.RunId).ToArrayAsync(Token));
        var replay = await new SimulationService(fresh).StartAsync(scenario.Scenario.Id, key, 1, "new-trace", Token);
        Assert.Equal(execution.RunId, replay.RunId);
        Assert.Equal("trace-success", replay.CorrelationId);
        Assert.False(await Process(test, engine));
        Assert.Equal(1, engine.Starts);
    }

    [Fact]
    public async Task Lost_response_and_worker_restart_only_poll_the_original_run()
    {
        await using var test = await TestDatabase.CreateAsync();
        var scenario = await Prepare(test);
        var key = Guid.NewGuid();
        var execution = await new SimulationService(test.Context).StartAsync(scenario.Scenario.Id, key, 1, "trace-timeout", Token);
        var engine = new FakeEngine { LoseResponse = true };
        await Process(test, engine);
        await using (var fresh = test.NewContext())
        {
            var running = await new SimulationService(fresh).GetAsync(key, Token);
            Assert.Equal("running", running!.Status);
            Assert.Equal("engine_timeout", running.ErrorCode);
            await fresh.SimulationCommands.Where(x => x.Id == key).ExecuteUpdateAsync(x => x.SetProperty(c => c.NextAttemptAtUtc, DateTime.UtcNow.AddSeconds(-1)), Token);
        }
        // A new DbContext/processor represents process recovery from the durable outbox.
        await Process(test, engine);
        await using var reopened = test.NewContext();
        Assert.Equal("completed", (await new SimulationService(reopened).GetAsync(key, Token))!.Status);
        Assert.Equal(1, engine.Starts);
        Assert.Equal(1, engine.Gets);
        Assert.Equal(execution.RunId, engine.Request!.RunId);
    }

    [Fact]
    public async Task Unavailable_network_eventually_fails_and_never_resubmits()
    {
        await using var test = await TestDatabase.CreateAsync();
        var scenario = await Prepare(test);
        var key = Guid.NewGuid();
        await new SimulationService(test.Context).StartAsync(scenario.Scenario.Id, key, 1, "trace-offline", Token);
        var engine = new FakeEngine { Unavailable = true };
        await Process(test, engine);
        await using (var db = test.NewContext())
            await db.SimulationCommands.Where(x => x.Id == key).ExecuteUpdateAsync(x => x
                .SetProperty(c => c.NextAttemptAtUtc, DateTime.UtcNow.AddSeconds(-1))
                .SetProperty(c => c.DispatchedAtUtc, DateTime.UtcNow.AddMinutes(-5)), Token);
        await Process(test, engine);
        await using var fresh = test.NewContext();
        var failed = (await new SimulationService(fresh).GetAsync(key, Token))!;
        Assert.Equal("failed", failed.Status);
        Assert.Equal("engine_network", failed.ErrorCode);
        Assert.False(failed.ResultsValid);
        Assert.NotNull(failed.FailureReason);
        Assert.Empty(await fresh.Metrics.ToArrayAsync(Token));
        Assert.Equal(1, engine.Starts);
        Assert.Equal(1, engine.Gets);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("zone")]
    [InlineData("duplicates")]
    [InlineData("failure-nul")]
    public async Task Inconsistent_engine_results_fail_without_partial_persistence(string invalid)
    {
        await using var test = await TestDatabase.CreateAsync();
        var scenario = await Prepare(test);
        var key = Guid.NewGuid();
        await new SimulationService(test.Context).StartAsync(scenario.Scenario.Id, key, 1, "trace-invalid", Token);
        var engine = new FakeEngine { Invalid = invalid };
        await Process(test, engine);
        await using var fresh = test.NewContext();
        Assert.Equal("failed", (await new SimulationService(fresh).GetAsync(key, Token))!.Status);
        Assert.Empty(await fresh.Metrics.ToArrayAsync(Token));
        Assert.Empty(await fresh.Snapshots.ToArrayAsync(Token));
    }

    [Fact]
    public async Task Manual_status_and_ingestion_cannot_mutate_engine_managed_runs_and_pending_runs_cannot_be_purged()
    {
        await using var test = await TestDatabase.CreateAsync();
        var scenario = await Prepare(test);
        var execution = await new SimulationService(test.Context).StartAsync(scenario.Scenario.Id, Guid.NewGuid(), 1, "trace-owner", Token);
        var storage = new RunStorageService(test.Context);
        await Assert.ThrowsAsync<ScenarioConflictException>(() => storage.TransitionAsync(execution.RunId, new("running", 1), Token));
        await Assert.ThrowsAsync<ScenarioConflictException>(() => storage.DeleteScenarioAsync(scenario.Scenario.Id, "purge", 1, Token));
    }

    [Fact]
    public async Task Pending_runs_survive_migration_rollback_as_planned_and_upgrade_accepts_new_commands()
    {
        await using var test = await TestDatabase.CreateAsync();
        var scenario = await Prepare(test);
        var execution = await new SimulationService(test.Context).StartAsync(scenario.Scenario.Id, Guid.NewGuid(), 1, "trace-migration", Token);
        await test.Context.GetService<IMigrator>().MigrateAsync("20261011133730_PersistRunResults", Token);
        Assert.Equal("planned", await test.Context.Database.SqlQuery<string>($"SELECT \"Status\" AS \"Value\" FROM twin.runs WHERE \"Id\" = {execution.RunId}").SingleAsync(Token));
        await test.Context.Database.MigrateAsync(Token);
        await using var fresh = test.NewContext();
        var pending = await new SimulationService(fresh).StartAsync(scenario.Scenario.Id, Guid.NewGuid(), 1, "trace-upgrade", Token);
        Assert.Equal("pending", pending.Status);
    }

    [Fact]
    public async Task Operations_upgrade_recovers_existing_completed_snapshots_and_rollback_preserves_results()
    {
        await using var test = await TestDatabase.CreateAsync();
        var scenario = await Prepare(test);
        var execution = await new SimulationService(test.Context).StartAsync(scenario.Scenario.Id, Guid.NewGuid(), 1, "trace-upgrade", Token);
        await Process(test, new FakeEngine());
        await test.Context.GetService<IMigrator>().MigrateAsync("20261012210835_QueueManagedRuns", Token);
        Assert.Single(await test.Context.Snapshots.Where(x => x.RunId == execution.RunId).ToArrayAsync(Token));
        Assert.Equal("completed", await test.Context.Runs.Where(x => x.Id == execution.RunId).Select(x => x.Status).SingleAsync(Token));
        await test.Context.Database.MigrateAsync(Token);
        await using var fresh = test.NewContext();
        var recovered = await new OperationsReader(fresh).StateAsync(scenario.Scenario.Id, execution.RunId, null, Token);
        Assert.True(recovered.Run!.ResultsValid);
        Assert.Equal(10, recovered.Run.SimulationMinute);
        Assert.Equal(20, Assert.Single(recovered.Run.Snapshot!.Zones).ProcessedPassengers);
    }

    [Fact]
    public async Task Unsupported_population_is_rejected_atomically_before_accepting_the_command()
    {
        await using var test = await TestDatabase.CreateAsync();
        var scenario = await Prepare(test);
        var service = new ScenarioService(test.Context);
        await service.UpdateAsync(scenario.Scenario.Id, new(scenario.Scenario.Code, scenario.Scenario.Name, scenario.Scenario.Description,
            scenario.Scenario.SceneId, scenario.Scenario.LayoutVersionId,
            scenario.Inputs with { Populations = [scenario.Inputs.Populations[0] with { Count = 100_001 }] }, 1), Token);
        await Assert.ThrowsAsync<ScenarioValidationException>(() => new SimulationService(test.Context).StartAsync(scenario.Scenario.Id, Guid.NewGuid(), 2, "trace-limit", Token));
        await using var fresh = test.NewContext();
        Assert.Empty(await fresh.SimulationCommands.ToArrayAsync(Token));
        Assert.Empty(await fresh.Runs.Where(x => x.ScenarioId == scenario.Scenario.Id).ToArrayAsync(Token));
    }

    [Fact]
    public async Task Long_engine_failure_preserves_valid_unicode_when_truncated()
    {
        await using var test = await TestDatabase.CreateAsync();
        var scenario = await Prepare(test);
        var key = Guid.NewGuid();
        await new SimulationService(test.Context).StartAsync(scenario.Scenario.Id, key, 1, "trace-unicode", Token);
        await Process(test, new FakeEngine { Invalid = "failure-unicode" });
        await using var fresh = test.NewContext();
        var failed = (await new SimulationService(fresh).GetAsync(key, Token))!;
        Assert.Equal("failed", failed.Status);
        Assert.True(failed.FailureReason!.Length <= 2000);
        Assert.False(char.IsHighSurrogate(failed.FailureReason[^1]));
    }

    [Fact]
    public async Task Typed_client_retries_only_get_and_maps_timeouts_and_validation_errors()
    {
        var request = Request();
        var handler = new ScriptedHandler((_, _) => throw new HttpRequestException("offline"));
        using var http = new HttpClient(handler) { BaseAddress = new("http://engine.test/") };
        var client = new IntelligenceClient(http, Options, NullLogger<IntelligenceClient>.Instance);
        Assert.Equal("engine_network", (await Assert.ThrowsAsync<IntelligenceException>(() => client.StartAsync(request, "trace-client", Token))).Code);
        Assert.Equal(1, handler.Calls);
        await Assert.ThrowsAsync<IntelligenceException>(() => client.GetAsync(request.RunId, "trace-client", Token));
        Assert.Equal(4, handler.Calls);
        Assert.All(handler.Headers, value => Assert.Equal("trace-client", value));
        handler.Action = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.UnprocessableEntity));
        Assert.Equal("engine_validation", (await Assert.ThrowsAsync<IntelligenceException>(() => client.StartAsync(request, "trace-client", Token))).Code);
        Assert.Equal(5, handler.Calls);
        handler.Action = async (_, token) => { await Task.Delay(1000, token); return new(HttpStatusCode.OK); };
        using var timedHttp = new HttpClient(handler, disposeHandler: false) { BaseAddress = new("http://engine.test/"), Timeout = TimeSpan.FromMilliseconds(20) };
        var timedClient = new IntelligenceClient(timedHttp, Options, NullLogger<IntelligenceClient>.Instance);
        Assert.Equal("engine_timeout", (await Assert.ThrowsAsync<IntelligenceException>(() => timedClient.StartAsync(request, "trace-client", Token))).Code);
    }

    private static async Task<bool> Process(TestDatabase test, FakeEngine engine)
    {
        await using var db = test.NewContext();
        return await new SimulationProcessor(db, engine, Options, NullLogger<SimulationProcessor>.Instance).ProcessNextAsync(Token);
    }

    private static async Task<ScenarioDetails> Prepare(TestDatabase test)
    {
        var service = new ScenarioService(test.Context);
        var layout = await service.PublishLayoutAsync(SyntheticScenarioSeeder.SceneId, Token);
        return await service.CreateAsync(new("simulation", "Simulation", "Synthetic integration fixture", layout.Version.SceneId, layout.Version.Id,
            new ScenarioInputs
            {
                StartsAtUtc = DateTime.UtcNow, DurationMinutes = 10, Seed = 42,
                Populations = [new(Guid.NewGuid(), "Passengers", 20)],
                StaffAllocations = layout.Layout.Staff.Where(x => x.CheckpointId is not null).Select(x => new StaffAllocation(x.Id, x.CheckpointId!.Value)).ToArray()
            }), Token);
    }

    private static SimulationRequest Request() => new("1.0", Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), DateTimeOffset.UtcNow, "UTC", 10, 42, 20, 2, [new(Guid.NewGuid(), 1, 1)]);

    private sealed class FakeEngine : IIntelligenceClient
    {
        public bool LoseResponse { get; init; }
        public bool Unavailable { get; init; }
        public string? Invalid { get; init; }
        public int Starts { get; private set; }
        public int Gets { get; private set; }
        public string? CorrelationId { get; private set; }
        public SimulationRequest? Request { get; private set; }
        public Task<EngineExecution> StartAsync(SimulationRequest request, string correlationId, CancellationToken token)
        {
            Starts++; Request = request; CorrelationId = correlationId;
            if (Unavailable) throw new IntelligenceException("engine_network", "Network is unavailable.", true);
            if (LoseResponse) throw new IntelligenceException("engine_timeout", "Response was lost.", true);
            return Task.FromResult(Result());
        }
        public Task<EngineExecution?> GetAsync(Guid runId, string correlationId, CancellationToken token)
        {
            Gets++;
            if (Unavailable) throw new IntelligenceException("engine_network", "Network is unavailable.", true);
            return Task.FromResult<EngineExecution?>(Result());
        }
        private EngineExecution Result()
        {
            var request = Request!;
            if (Invalid is "failure-nul" or "failure-unicode")
            {
                var message = Invalid == "failure-nul" ? "Engine\0error" : new string('a', 2000 - "simulation_failed: ".Length - 1) + "😀";
                return new(request.RunId, "failed", new("1.0", request.RunId, "failed", 0, [], [], new("simulation_failed", message)));
            }
            var metric = new SimulationMetric(Invalid == "zone" ? Guid.NewGuid() : request.Checkpoints[0].CheckpointId, request.StartsAtUtc.AddMinutes(10), 0.123456789, 2, 20);
            return new(request.RunId, "completed", new("1.0", Invalid == "run" ? Guid.NewGuid() : request.RunId, "completed", 10,
                Invalid == "duplicates" ? [metric, metric] : [metric],
                [new("1.0", request.Checkpoints[0].CheckpointId, 10, 0, 20)], null));
        }
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Action { get; set; } = action;
        public int Calls { get; private set; }
        public List<string> Headers { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; Headers.Add(request.Headers.GetValues("X-Correlation-ID").Single());
            return Action(request, token);
        }
    }
}

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroSynapse.Application.Operations;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using AeroSynapse.Infrastructure.Storage;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AeroSynapse.Management.Tests;

public sealed class OperationsTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] Types = ["run.started", "run.snapshot", "run.completed", "optimization.completed"];

    [Fact]
    public async Task Hub_sends_ordered_safe_snapshots_only_to_the_active_scenario_and_switches_membership()
    {
        await using var host = await ManagementHost.CreateAsync();
        var a = await Scenario(host, "a"); var b = await Scenario(host, "b");
        var messagesA = new ConcurrentQueue<OperationsMessage>(); var messagesB = new ConcurrentQueue<OperationsMessage>();
        await using var connectionA = await Connect(host, a.Scenario.Id, messagesA);
        await using var connectionB = await Connect(host, b.Scenario.Id, messagesB);
        var runA = await ExternalRun(host, a.Scenario.Id);
        await SampleAndComplete(host, runA);
        await Wait(() => messagesA.Any(x => x.Type == "run.completed"));
        Assert.Empty(messagesB);
        var received = messagesA.ToArray();
        Assert.Equal(new[] { "run.started", "run.snapshot", "run.completed" }, received.Select(x => x.Type));
        Assert.Equal(received.OrderBy(x => x.Sequence).Select(x => x.Sequence), received.Select(x => x.Sequence));
        var snapshot = Assert.Single(received, x => x.Type == "run.snapshot");
        Assert.Equal(runA.Id, snapshot.RunId);
        Assert.Equal(3, snapshot.SimulationMinute);
        Assert.Equal(4, Assert.Single(snapshot.Snapshot!.Zones).QueuedPassengers);
        Assert.DoesNotContain("private@example.com", JsonSerializer.Serialize(received, Json));
        Assert.DoesNotContain("Private Operator", JsonSerializer.Serialize(received, Json));

        await Task.Delay(300, Token);
        await connectionA.InvokeAsync("SubscribeScenario", b.Scenario.Id, Token);
        messagesA.Clear();
        var runB = await ExternalRun(host, b.Scenario.Id);
        await SampleAndComplete(host, runB);
        await Wait(() => messagesA.Any(x => x.Type == "run.completed"));
        Assert.All(messagesA, message => Assert.Equal(b.Scenario.Id, message.ScenarioId));
        Assert.All(messagesB, message => Assert.Equal(b.Scenario.Id, message.ScenarioId));
        Assert.All(messagesA, message => Assert.Equal(runB.Id, message.RunId));
    }

    [Fact]
    public async Task Reconnection_restores_the_committed_cursor_status_and_snapshot_through_GET()
    {
        await using var host = await ManagementHost.CreateAsync();
        var scenario = await Scenario(host, "recover");
        var received = new ConcurrentQueue<OperationsMessage>();
        await using var connection = await Connect(host, scenario.Scenario.Id, received);
        var run = await ExternalRun(host, scenario.Scenario.Id);
        await Wait(() => received.Any(x => x.Type == "run.started"));
        var cursor = received.Last().Sequence;
        await connection.StopAsync(Token);
        await SampleAndComplete(host, run);
        await connection.StartAsync(Token);
        await connection.InvokeAsync("SubscribeScenario", scenario.Scenario.Id, Token);
        var state = await State(host, scenario.Scenario.Id, run.Id, cursor);
        Assert.True(state.Sequence > cursor);
        Assert.Equal("completed", state.Run!.Status);
        Assert.True(state.Run.ResultsValid);
        Assert.Equal(3, state.Run.SimulationMinute);
        Assert.Equal(4, Assert.Single(state.Run.Snapshot!.Zones).QueuedPassengers);
        Assert.DoesNotContain("private@example.com", JsonSerializer.Serialize(state, Json));
        using var crossScenario = await host.Client.GetAsync($"/scenarios/{Guid.NewGuid()}/operations?runId={run.Id}", Token);
        Assert.Equal(HttpStatusCode.NotFound, crossScenario.StatusCode);
    }

    [Fact]
    public async Task Progress_coalesces_volume_preserves_latest_state_and_never_moves_backwards()
    {
        await using var host = await ManagementHost.CreateAsync();
        var scenario = await Scenario(host, "volume");
        var run = await ExternalRun(host, scenario.Scenario.Id);
        await using var scope = host.Services.CreateAsyncScope();
        var recorder = scope.ServiceProvider.GetRequiredService<IRunProgressRecorder>();
        var zone = SyntheticScenarioSeeder.Id(101);
        for (var minute = 1; minute <= 40; minute++)
            await recorder.RecordAsync(run.Id, new(minute, [new(zone, 120 - minute, minute)]), Token);
        await recorder.RecordAsync(run.Id, new(5, [new(zone, 2, 1)]), Token);
        var state = await State(host, scenario.Scenario.Id, run.Id);
        Assert.Equal(40, state.Run!.SimulationMinute);
        Assert.Equal(40, Assert.Single(state.Run.Snapshot!.Zones).ProcessedPassengers);
        var db = scope.ServiceProvider.GetRequiredService<TwinDbContext>();
        Assert.True(await db.OperationsEvents.CountAsync(x => x.ScenarioId == scenario.Scenario.Id && x.Type == "run.snapshot", Token) < 10);
        await Assert.ThrowsAsync<InvalidOperationException>(() => recorder.RecordAsync(run.Id, new(41, [new(Guid.NewGuid(), 1, 2)]), Token));
    }

    [Fact]
    public async Task Journal_retention_is_bounded_and_expired_cursors_request_a_full_state_reset()
    {
        await using var host = await ManagementHost.CreateAsync();
        var scenario = await Scenario(host, "retention");
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TwinDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT twin.operations_emit({scenario.Scenario.Id}, 'run.started', NULL::uuid, 0::double precision,
                'running', false, NULL::jsonb, NULL::jsonb) FROM generate_series(1, 1100)
            """, Token);
        Assert.Equal(1024, await db.OperationsEvents.CountAsync(x => x.ScenarioId == scenario.Scenario.Id, Token));
        var state = await State(host, scenario.Scenario.Id, null, 1);
        Assert.True(state.ResetRequired);
        Assert.Equal(1100, state.Sequence);
    }

    [Fact]
    public async Task Optimization_messages_and_GET_expose_only_ids_from_each_own_scenario()
    {
        await using var host = await ManagementHost.CreateAsync();
        var a = await Scenario(host, "baseline");
        await using var scope = host.Services.CreateAsyncScope();
        var scenarios = scope.ServiceProvider.GetRequiredService<IScenarioService>();
        var b = await scenarios.DuplicateAsync(a.Scenario.Id, new("candidate", "Candidate"), Token);
        var runA = await ExternalRun(host, a.Scenario.Id); var runB = await ExternalRun(host, b.Scenario.Id);
        await SampleAndComplete(host, runA); await SampleAndComplete(host, runB);
        var storage = scope.ServiceProvider.GetRequiredService<IRunStorageService>();
        var result = await storage.RecommendAsync(new(runA.Id, "private@example.com", 1, "Private Operator",
            [new(runB.Id, 1, 2, "{\"name\":\"private@example.com\"}")]), Token);
        var reader = scope.ServiceProvider.GetRequiredService<IOperationsReader>();
        foreach (var scenario in new[] { a.Scenario.Id, b.Scenario.Id })
        {
            var events = await reader.EventsAsync(scenario, 0, 64, Token);
            var message = Assert.Single(events, x => x.Type == "optimization.completed");
            Assert.Equal(result.Recommendation.Id, message.Optimization!.OptimizationId);
            Assert.Equal(scenario == a.Scenario.Id ? runA.Id : (Guid?)null, message.Optimization.BaselineRunId);
            Assert.Equal(scenario == b.Scenario.Id ? runB.Id : (Guid?)null, message.Optimization.RecommendedRunId);
            var state = await State(host, scenario, null);
            Assert.Equal(message.Optimization, state.Optimization);
            Assert.DoesNotContain("private@example.com", JsonSerializer.Serialize(message, Json));
            Assert.DoesNotContain("Private Operator", JsonSerializer.Serialize(state, Json));
        }
    }

    private static Task<ScenarioDetails> Scenario(ManagementHost host, string code) => host.CreateScenarioAsync(request => request with { Code = code });
    private static async Task<SimulationRun> ExternalRun(ManagementHost host, Guid scenarioId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var run = await scope.ServiceProvider.GetRequiredService<IScenarioService>().CreateRunAsync(scenarioId, Token);
        return await scope.ServiceProvider.GetRequiredService<IRunStorageService>().TransitionAsync(run.Id, new("running", 1), Token);
    }
    private static async Task SampleAndComplete(ManagementHost host, SimulationRun run)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var storage = scope.ServiceProvider.GetRequiredService<IRunStorageService>();
        var input = JsonSerializer.Deserialize<RunInputSnapshot>(run.InputSnapshotJson!, Json)!;
        await storage.AddSnapshotAsync(run.Id, new(input.Scenario.Inputs.StartsAtUtc.AddMinutes(3), "checkpoint", SyntheticScenarioSeeder.Id(101),
            "{\"queuedPassengers\":4,\"processedPassengers\":2,\"email\":\"private@example.com\",\"staff\":\"Private Operator\"}"), Token);
        await storage.TransitionAsync(run.Id, new("completed", 2), Token);
    }
    private static async Task<HubConnection> Connect(ManagementHost host, Guid scenarioId, ConcurrentQueue<OperationsMessage> messages)
    {
        var connection = new HubConnectionBuilder().WithUrl("http://localhost/hubs/operations", options =>
        {
            options.Transports = HttpTransportType.LongPolling;
            options.HttpMessageHandlerFactory = _ => host.HubHandler();
        }).Build();
        foreach (var type in Types) connection.On<OperationsMessage>(type, message => messages.Enqueue(message));
        await connection.StartAsync(Token);
        await connection.InvokeAsync("SubscribeScenario", scenarioId, Token);
        return connection;
    }
    private static async Task<ScenarioOperationsState> State(ManagementHost host, Guid scenarioId, Guid? runId, long after = 0) =>
        (await host.Client.GetFromJsonAsync<ScenarioOperationsState>($"/scenarios/{scenarioId}/operations?afterSequence={after}" + (runId.HasValue ? $"&runId={runId}" : ""), Json, Token))!;
    private static async Task Wait(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!predicate() && DateTime.UtcNow < deadline) await Task.Delay(30, Token);
        Assert.True(predicate(), "Expected operations message was not delivered.");
    }
}

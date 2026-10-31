using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using AeroSynapse.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using AeroSynapse.Application.Operations;
using AeroSynapse.Application.Simulation;

namespace AeroSynapse.Management.Tests;

public sealed class QueueForecastApiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class LiveInspector : IIntelligenceClient
    {
        public EngineExecution? Current { get; set; }
        public Task<EngineExecution> StartAsync(SimulationRequest request, string correlationId, CancellationToken token) =>
            throw new InvalidOperationException("Forecasting must never start an execution.");
        public Task<EngineExecution?> GetAsync(Guid id, string correlationId, CancellationToken token) => Task.FromResult(Current);
    }

    [Fact]
    public async Task Live_forecasts_use_new_observations_while_results_are_still_incomplete()
    {
        var inspector = new LiveInspector();
        await using var host = await ManagementHost.CreateAsync(client: inspector);
        var source = await StaffingEvaluationTests.Create(host);
        await using var scope = host.Services.CreateAsyncScope();
        var storage = scope.ServiceProvider.GetRequiredService<IRunStorageService>();
        var run = await scope.ServiceProvider.GetRequiredService<IScenarioService>().CreateRunAsync(source.Scenario.Id, Token);
        run = await storage.TransitionAsync(run.Id, new("running", run.Revision), Token);
        var input = JsonSerializer.Deserialize<RunInputSnapshot>(run.InputSnapshotJson!, Json)!;
        var simulation = CapturedSimulation.Request(run.Id, input);
        var target = simulation.PassengerGeneration!.Flights[0].CheckpointId;
        EngineExecution Frame(int minute, int queue, int remaining) => new(run.Id, "running", null,
            new(minute, simulation.Checkpoints.Select(cp => new ProgressZone(cp.CheckpointId, cp.CheckpointId == target ? queue : 0,
                cp.CheckpointId == target ? simulation.PassengerCount - queue - remaining : 0, cp.CheckpointId == target ? remaining : 0)).ToArray()));
        async Task<QueueForecastResult> Latest()
        {
            using var response = await host.Client.PostAsJsonAsync($"/runs/{run.Id}/queue-forecast/live", new QueueForecastLiveOptions(15, 20), Json, Token);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Token));
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>(Json, Token);
            Assert.Empty(VersionedContract.Validate("QueueForecastResult", payload));
            return payload.Deserialize<QueueForecastResult>(ContractJson.Options)!;
        }
        inspector.Current = Frame(3, 0, simulation.PassengerCount);
        var first = await Latest();
        Assert.Equal(3, first.FromMinute); Assert.Equal("live_simulation", first.ObservationSource);
        Assert.Equal(0, first.Checkpoints.Single(cp => cp.CheckpointId == target).InitialQueuePassengers);
        inspector.Current = Frame(6, simulation.PassengerCount - 10, 0);
        var next = await Latest(); var queue = next.Checkpoints.Single(cp => cp.CheckpointId == target);
        Assert.Equal(6, next.FromMinute); Assert.Equal(simulation.PassengerCount - 10, queue.InitialQueuePassengers);
        Assert.All(queue.Points, point => Assert.Equal(0, point.Arrivals));
        Assert.All(queue.Points, point => Assert.Equal(point.AvailableStaff * queue.ServiceRatePerStaffMinute, point.Capacity));
        Assert.Equal("running", (await storage.GetAsync(run.Id, Token))!.Status);
        Assert.False((await storage.GetAsync(run.Id, Token))!.ResultsValid);
        Assert.Empty((await storage.SnapshotsAsync(run.Id, new(IncludeInvalid: true), Token)).Items);
        var db = scope.ServiceProvider.GetRequiredService<TwinDbContext>();
        Assert.Empty(await db.SimulationCommands.ToArrayAsync(Token));
        inspector.Current = null;
        using var missing = await host.Client.PostAsJsonAsync($"/runs/{run.Id}/queue-forecast/live", new QueueForecastLiveOptions(15, 20), Json, Token);
        Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
        inspector.Current = new(run.Id, "running", null, new(6, simulation.Checkpoints.Select(cp => new ProgressZone(cp.CheckpointId, 0, 0)).ToArray()));
        using var incomplete = await host.Client.PostAsJsonAsync($"/runs/{run.Id}/queue-forecast/live", new QueueForecastLiveOptions(15, 20), Json, Token);
        Assert.Equal(HttpStatusCode.BadGateway, incomplete.StatusCode);
    }

    private static async Task<QueueForecastResult> Forecast(ManagementHost host, Guid id, QueueForecastOptions options)
    {
        using var response = await host.Client.PostAsJsonAsync($"/runs/{id}/queue-forecast", options, Json, Token);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Token));
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(Json, Token);
        Assert.Empty(VersionedContract.Validate("QueueForecastResult", payload));
        return payload.Deserialize<QueueForecastResult>(ContractJson.Options)!;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_forecast_uses_captured_backlog_plans_and_rates_without_mutating_runs_or_scenarios(bool zeroCapacity)
    {
        await using var host = await ManagementHost.CreateAsync(realEngine: true);
        var source = await StaffingEvaluationTests.Create(host);
        if (zeroCapacity)
        {
            using var closed = await host.Client.PutAsJsonAsync($"/scenarios/{source.Scenario.Id}", new ScenarioWriteRequest(
                source.Scenario.Code, source.Scenario.Name, source.Scenario.Description, source.Scenario.SceneId, source.Scenario.LayoutVersionId,
                source.Inputs with { StaffAllocations = [] }, source.Scenario.Revision), Json, Token);
            Assert.True(closed.IsSuccessStatusCode, await closed.Content.ReadAsStringAsync(Token));
        }
        var before = (await host.Client.GetFromJsonAsync<ScenarioDetails>($"/scenarios/{source.Scenario.Id}", Json, Token))!;
        using var started = await host.Client.PostAsync($"/scenarios/{source.Scenario.Id}/runs", null, Token);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var run = (await started.Content.ReadFromJsonAsync<SimulationRun>(Json, Token))!;
        Assert.True(await host.Dispatch(Token));
        var captured = (await host.Client.GetFromJsonAsync<RunDetails>($"/runs/{run.Id}", Json, Token))!;
        Assert.True(captured.ResultsValid, captured.FailureReason);
        var kpis = (await host.Client.GetFromJsonAsync<KpiResult>($"/runs/{run.Id}/kpis?fromMinute=5&toMinute=5&limit=50000", Json, Token))!;
        var result = await Forecast(host, run.Id, new(5, 15, 0));
        Assert.Equal("simulated_estimate", result.Kind); Assert.Equal(5, result.FromMinute); Assert.Equal(20, result.ToMinute);
        foreach (var row in result.Checkpoints)
        {
            Assert.Equal(kpis.Snapshots.Single(snapshot => snapshot.ZoneId == row.CheckpointId).QueuedPassengers, row.InitialQueuePassengers);
            var first = row.Points.FirstOrDefault(point => point.QueuePassengers > 0);
            Assert.Equal(first?.Minute, row.Alert?.Minute);
            Assert.All(row.Points, point => Assert.True(double.IsFinite(point.QueuePassengers) && point.QueuePassengers >= 0));
            if (zeroCapacity) Assert.All(row.Points, point => Assert.Equal(0, point.Capacity));
        }
        Assert.All((await Forecast(host, run.Id, new(5, 30, 100000))).Checkpoints, row => Assert.Null(row.Alert));
        Assert.All((await Forecast(host, run.Id, new(5, 15, null))).Checkpoints, row => Assert.Null(row.Alert));
        var after = (await host.Client.GetFromJsonAsync<ScenarioDetails>($"/scenarios/{source.Scenario.Id}", Json, Token))!;
        Assert.Equal(JsonSerializer.Serialize(before, Json), JsonSerializer.Serialize(after, Json));
        using var changed = await host.Client.PutAsJsonAsync($"/scenarios/{source.Scenario.Id}", new ScenarioWriteRequest(
            after.Scenario.Code, after.Scenario.Name + " updated", after.Scenario.Description, after.Scenario.SceneId, after.Scenario.LayoutVersionId,
            after.Inputs with { StaffAllocations = [] }, after.Scenario.Revision), Json, Token);
        Assert.True(changed.IsSuccessStatusCode, await changed.Content.ReadAsStringAsync(Token));
        Assert.Equal(JsonSerializer.Serialize(result, Json), JsonSerializer.Serialize(await Forecast(host, run.Id, new(5, 15, 0)), Json));
        var unchanged = (await host.Client.GetFromJsonAsync<RunDetails>($"/runs/{run.Id}", Json, Token))!;
        Assert.Equal(JsonSerializer.Serialize(captured, Json), JsonSerializer.Serialize(unchanged, Json));
        await using var scope = host.Services.CreateAsyncScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<TwinDbContext>().Runs.CountAsync(item => item.ScenarioId == source.Scenario.Id, Token));
    }

    [Fact]
    public async Task Pending_missing_invalid_and_uncaptured_periods_fail_before_forecasting()
    {
        await using var host = await ManagementHost.CreateAsync(realEngine: true);
        var source = await StaffingEvaluationTests.Create(host);
        using var cadence = await host.Client.PutAsJsonAsync($"/scenarios/{source.Scenario.Id}/kpis/settings", new KpiWriteRequest(source.Scenario.Revision, 5), Json, Token);
        Assert.Equal(HttpStatusCode.OK, cadence.StatusCode);
        using var missing = await host.Client.PostAsJsonAsync($"/runs/{Guid.NewGuid()}/queue-forecast", new QueueForecastOptions(0, 15, 0), Json, Token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var started = await host.Client.PostAsync($"/scenarios/{source.Scenario.Id}/runs", null, Token);
        var run = (await started.Content.ReadFromJsonAsync<SimulationRun>(Json, Token))!;
        using var pending = await host.Client.PostAsJsonAsync($"/runs/{run.Id}/queue-forecast", new QueueForecastOptions(0, 15, 0), Json, Token);
        Assert.Equal(HttpStatusCode.Conflict, pending.StatusCode);
        Assert.True(await host.Dispatch(Token));
        foreach (var options in new[] { new QueueForecastOptions(1, 15, 0), new QueueForecastOptions(55, 15, 0) })
        {
            using var response = await host.Client.PostAsJsonAsync($"/runs/{run.Id}/queue-forecast", options, Json, Token);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using var invalid = await host.Client.PostAsJsonAsync($"/runs/{run.Id}/queue-forecast", new QueueForecastOptions(0, 14, 0), Json, Token);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
    }
}

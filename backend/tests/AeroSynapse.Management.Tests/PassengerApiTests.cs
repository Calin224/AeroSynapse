using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using AeroSynapse.Infrastructure.Storage;
using Xunit;

namespace AeroSynapse.Management.Tests;

public sealed class PassengerApiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static async Task<ScenarioDetails> Create(ManagementHost host)
    {
        using var published = await host.Client.PostAsync($"/scenes/{SyntheticScenarioSeeder.SceneId}/layout-versions", null, Token);
        var layout = (await published.Content.ReadFromJsonAsync<PublishedLayout>(Json, Token))!;
        var request = JsonSerializer.Deserialize<ScenarioWriteRequest>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scenario-request.json")), Json)!;
        using var created = await host.Client.PostAsJsonAsync("/scenarios", request with { LayoutVersionId = layout.Version.Id }, Json, Token);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<ScenarioDetails>(Json, Token))!;
    }
    private static async Task<ScenarioDetails> Configure(ManagementHost host, ScenarioDetails scenario, int seed = 42)
    {
        var id = scenario.Scenario.Id;
        var settings = (await host.Client.GetFromJsonAsync<PassengerSettings>($"/scenarios/{id}/passengers/settings", Json, Token))!;
        using var saved = await host.Client.PutAsJsonAsync($"/scenarios/{id}/passengers/settings",
            new PassengerConfigWriteRequest(settings.Revision, seed, settings.Defaults), Json, Token);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        return (await saved.Content.ReadFromJsonAsync<ScenarioDetails>(Json, Token))!;
    }

    [Fact]
    public async Task Real_NumPy_generation_runs_and_replays_the_captured_population_after_a_scenario_edit()
    {
        await using var host = await ManagementHost.CreateAsync(realEngine: true);
        var scenario = await Configure(host, await Create(host), 31415);
        var id = scenario.Scenario.Id;
        var path = $"/scenarios/{id}/passengers?revision={scenario.Scenario.Revision}&limit=100000";
        var first = (await host.Client.GetFromJsonAsync<PassengerGenerationResult>(path, Json, Token))!;
        var repeated = (await host.Client.GetFromJsonAsync<PassengerGenerationResult>(path, Json, Token))!;
        Assert.Equal(first.Fingerprint, repeated.Fingerprint);
        Assert.Equal(scenario.Inputs.Flights.Sum(flight => flight.PassengerCapacity), first.ModeledCount);
        Assert.Equal(first.ModeledCount, first.ReturnedCount); Assert.False(first.HasMore); Assert.False(first.HasSubset);
        Assert.All(first.Passengers, passenger => Assert.True(passenger.IsSynthetic));
        using var export = await host.Client.GetAsync($"/scenarios/{id}/passengers/export?revision={scenario.Scenario.Revision}", Token);
        Assert.Equal(HttpStatusCode.NotFound, export.StatusCode);
        using var started = await host.Client.PostAsync($"/scenarios/{id}/runs", null, Token);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var run = (await started.Content.ReadFromJsonAsync<SimulationRun>(Json, Token))!;
        Assert.True(await host.Dispatch(Token));
        var completed = (await host.Client.GetFromJsonAsync<RunDetails>($"/runs/{run.Id}", Json, Token))!;
        Assert.Equal("completed", completed.Status);
        scenario = await Configure(host, scenario, 27182);
        var changed = (await host.Client.GetFromJsonAsync<PassengerGenerationResult>(
            $"/scenarios/{id}/passengers?revision={scenario.Scenario.Revision}&limit=100000", Json, Token))!;
        Assert.NotEqual(first.Fingerprint, changed.Fingerprint);
        var replay = (await host.Client.GetFromJsonAsync<PassengerGenerationResult>($"/runs/{run.Id}/passengers?limit=100000", Json, Token))!;
        Assert.Equal(first.Seed, replay.Seed); Assert.Equal(first.Fingerprint, replay.Fingerprint);
        Assert.Equal(first.Passengers, replay.Passengers);
    }

    [Fact]
    public async Task Real_engine_reports_alternative_flows_and_replays_a_blocked_stage_delay_from_the_run()
    {
        await using var host = await ManagementHost.CreateAsync(realEngine: true);
        var scenario = await Create(host);
        var id = scenario.Scenario.Id;
        var settings = (await host.Client.GetFromJsonAsync<PassengerSettings>($"/scenarios/{id}/passengers/settings", Json, Token))!;
        var minutes = new PassengerDistribution("uniform", .5, 3, 1.75, .4, 1.75);
        var flow = new PassengerFlowConfig([new("standard", .6, false, "security", 1, 1), new("holdBaggage", .4, true, "security", 1, 1)],
            [new("bagDrop", 1, minutes)], [new("bagDrop", 5, 25)]);
        using var saved = await host.Client.PutAsJsonAsync($"/scenarios/{id}/passengers/settings",
            new PassengerConfigWriteRequest(settings.Revision, 42, settings.Defaults with { Flow = flow }), Json, Token);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        scenario = (await saved.Content.ReadFromJsonAsync<ScenarioDetails>(Json, Token))!;
        Assert.Equal(flow.Closures, scenario.Inputs.PassengerGeneration!.Flow!.Closures);
        var report = (await host.Client.GetFromJsonAsync<PassengerFlowReport>(
            $"/scenarios/{id}/passengers/flows?revision={scenario.Scenario.Revision}", Json, Token))!;
        Assert.True(report.IsSynthetic);
        Assert.Equal(["bagDrop", "security"], report.Stages.Select(stage => stage.Stage));
        Assert.Equal(20, report.Stages[0].BlockedMinutes);
        Assert.True(report.Segments.Single(segment => segment.Segment == "holdBaggage").MeanClosureDelayMinutes > 0);
        Assert.Equal(scenario.Inputs.Populations.Sum(population => population.Count), report.Segments.Sum(segment => segment.Passengers));
        using var started = await host.Client.PostAsync($"/scenarios/{id}/runs", null, Token);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var run = (await started.Content.ReadFromJsonAsync<SimulationRun>(Json, Token))!;
        Assert.True(await host.Dispatch(Token));
        Assert.Equal("completed", (await host.Client.GetFromJsonAsync<RunDetails>($"/runs/{run.Id}", Json, Token))!.Status);
        using var kpis = await host.Client.GetAsync($"/runs/{run.Id}/kpis", Token);
        Assert.Equal(HttpStatusCode.NotFound, kpis.StatusCode);
        Assert.Contains("alternative passenger flows", await kpis.Content.ReadAsStringAsync(Token));
        var replay = (await host.Client.GetFromJsonAsync<PassengerFlowReport>($"/runs/{run.Id}/passengers/flows", Json, Token))!;
        Assert.Equal(report.Fingerprint, replay.Fingerprint);
        Assert.Equal(report.Stages, replay.Stages); Assert.Equal(report.Segments, replay.Segments);
    }

    [Fact]
    public async Task Invalid_profiles_are_atomic_and_flight_edits_keep_counts_and_validation_consistent()
    {
        await using var host = await ManagementHost.CreateAsync();
        var scenario = await Configure(host, await Create(host));
        var id = scenario.Scenario.Id;
        var config = scenario.Inputs.PassengerGeneration!;
        using var invalid = await host.Client.PutAsJsonAsync($"/scenarios/{id}/passengers/settings",
            new PassengerConfigWriteRequest(scenario.Scenario.Revision, 43,
                config with { Batches = [config.Batches[0] with { ModeledCount = 999 }] }), Json, Token);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var unchanged = (await host.Client.GetFromJsonAsync<ScenarioDetails>($"/scenarios/{id}", Json, Token))!;
        Assert.Equal(scenario.Scenario.Revision, unchanged.Scenario.Revision); Assert.Equal(42, unchanged.Inputs.Seed);
        using var stale = await host.Client.GetAsync($"/scenarios/{id}/passengers?revision=1", Token);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var flight = scenario.Inputs.Flights[0];
        using var badSchedule = await host.Client.PutAsJsonAsync($"/scenarios/{id}/flights/{flight.Id}",
            new FlightWriteRequest(scenario.Scenario.Revision, flight with { BoardingOpensAtUtc = null }), Json, Token);
        Assert.Equal(HttpStatusCode.BadRequest, badSchedule.StatusCode);
        using var resized = await host.Client.PutAsJsonAsync($"/scenarios/{id}/flights/{flight.Id}",
            new FlightWriteRequest(scenario.Scenario.Revision, flight with { PassengerCapacity = 80 }), Json, Token);
        Assert.Equal(HttpStatusCode.OK, resized.StatusCode);
        scenario = (await resized.Content.ReadFromJsonAsync<ScenarioDetails>(Json, Token))!;
        Assert.Equal(80, scenario.Inputs.Populations.Sum(population => population.Count));
        Assert.Equal(80, scenario.Inputs.PassengerGeneration!.Batches[0].ModeledCount);
        foreach (var item in scenario.Inputs.Events)
        {
            using var removed = await host.Client.DeleteAsync($"/scenarios/{id}/events/{item.Id}?revision={scenario.Scenario.Revision}", Token);
            Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
            scenario = (await removed.Content.ReadFromJsonAsync<ScenarioDetails>(Json, Token))!;
        }
        using var deleted = await host.Client.DeleteAsync($"/scenarios/{id}/flights/{flight.Id}?revision={scenario.Scenario.Revision}", Token);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        var empty = (await deleted.Content.ReadFromJsonAsync<ScenarioDetails>(Json, Token))!;
        Assert.Null(empty.Inputs.PassengerGeneration); Assert.Empty(empty.Inputs.Populations); Assert.Empty(empty.Inputs.Flows);
    }
}

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

public sealed class JourneyApiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Complete_journeys_execute_persist_paginate_and_survive_later_scenario_edits()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SIMULATION_TEST_URL")), "Set SIMULATION_TEST_URL to the real Python engine.");
        await using var host = await ManagementHost.CreateAsync(realEngine: true);
        using var published = await host.Client.PostAsync($"/scenes/{SyntheticScenarioSeeder.SceneId}/layout-versions", null, Token);
        Assert.Equal(HttpStatusCode.Created, published.StatusCode);
        var layout = (await published.Content.ReadFromJsonAsync<PublishedLayout>(Json, Token))!;
        var template = JsonSerializer.Deserialize<ScenarioWriteRequest>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scenario-request.json")), Json)!;
        var start = template.Inputs.StartsAtUtc;
        var arrivals = new[] { 60, 180, 300, 480 };
        var flights = arrivals.Select((arrival, i) => new ScenarioFlight(Guid.NewGuid(), $"DAY-{i + 1}", layout.Layout.Gates[0].Id,
            start.AddMinutes(arrival + 60), 4, start.AddMinutes(arrival + 5), start.AddMinutes(arrival + 55))).ToArray();
        var inputs = template.Inputs with { DurationMinutes = 720, Flights = flights, Populations = [], Flows = [], Events = [], SnapshotIntervalMinutes = 5 };
        var profiles = PassengerScenarios.Defaults(inputs, layout.Layout);
        profiles = profiles with { Batches = profiles.Batches.Select(b => b with { Arrival = new("constant", 0, 0, 0, 0, 0) }).ToArray() };
        inputs = PassengerScenarios.Synchronize(inputs with { PassengerGeneration = profiles }, layout.Layout);
        inputs = inputs with
        {
            Journey = new(1, 1, 1, .25, 4, 10,
                flights.Select((f, i) => new ScenarioJourneyFlight(f.Id, arrivals[i], true, i == 3 ? flights[0].Id : null, i == 3 ? 360 : 0)).ToArray(),
                [new(Guid.NewGuid(), flights[0].Id, flights[1].Id, 2, 5, true)]),
            Events = [new(Guid.NewGuid(), "flight_change", start.AddMinutes(20), null, null, flights[0].Id, "{\"delayMinutes\":240}")]
        };
        var write = template with { Code = "journey-day", LayoutVersionId = layout.Version.Id, Inputs = inputs };
        using var created = await host.Client.PostAsJsonAsync("/scenarios", write, Json, Token);
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Token));
        var scenario = (await created.Content.ReadFromJsonAsync<ScenarioDetails>(Json, Token))!;
        using var started = await host.Client.PostAsync($"/scenarios/{scenario.Scenario.Id}/runs", null, Token);
        Assert.True(started.StatusCode == HttpStatusCode.Accepted, await started.Content.ReadAsStringAsync(Token));
        var run = (await started.Content.ReadFromJsonAsync<SimulationRun>(Json, Token))!;
        Assert.True(await host.Dispatch(Token));
        var completed = (await host.Client.GetFromJsonAsync<RunDetails>($"/runs/{run.Id}", Json, Token))!;
        Assert.True(completed.Status == "completed", completed.FailureReason);
        var report = (await host.Client.GetFromJsonAsync<JourneyReport>($"/runs/{run.Id}/journey?limit=100", Json, Token))!;
        Assert.Equal(16, report.PopulationCount); Assert.Equal(2, report.ConnectionPassengerCount);
        Assert.Equal(2, report.MissedConnectionCount); Assert.Equal(2, report.Plans.Count);
        Assert.True(report.Plans[^1].Flights.Single(f => f.FlightId == flights[3].Id).DepartureMinute > inputs.DurationMinutes);
        var page = (await host.Client.GetFromJsonAsync<JourneyReport>($"/runs/{run.Id}/journey?offset=2&limit=3", Json, Token))!;
        Assert.Equal(3, page.ReturnedCount); Assert.True(page.HasMore); Assert.Equal(report.Passengers.Skip(2).Take(3).Select(p => p.Id), page.Passengers.Select(p => p.Id));
        using var invalidPage = await host.Client.GetAsync($"/runs/{run.Id}/journey?offset=-1", Token);
        Assert.Equal(HttpStatusCode.BadRequest, invalidPage.StatusCode);
        // Editing the scenario and rejecting a stale revision never changes a captured report.
        using var updated = await host.Client.PutAsJsonAsync($"/scenarios/{scenario.Scenario.Id}", write with
        {
            Revision = scenario.Scenario.Revision, Inputs = inputs with { Journey = inputs.Journey! with { PassportStaffCount = 3 } }
        }, Json, Token);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using var stale = await host.Client.PutAsJsonAsync($"/scenarios/{scenario.Scenario.Id}", write with { Revision = scenario.Scenario.Revision }, Json, Token);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var replay = (await host.Client.GetFromJsonAsync<JourneyReport>($"/runs/{run.Id}/journey?limit=100", Json, Token))!;
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(report, Json), JsonSerializer.SerializeToElement(replay, Json)));
        using var anonymous = host.AnonymousClient();
        using var denied = await anonymous.GetAsync($"/runs/{run.Id}/journey", Token);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }
}

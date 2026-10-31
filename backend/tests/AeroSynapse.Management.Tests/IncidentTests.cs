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

public sealed class IncidentTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static async Task<(ScenarioWriteRequest Request, PublishedLayout Layout)> Draft(ManagementHost host)
    {
        using var reply = await host.Client.PostAsync($"/scenes/{SyntheticScenarioSeeder.SceneId}/layout-versions", null, Token);
        var layout = (await reply.Content.ReadFromJsonAsync<PublishedLayout>(Json, Token))!;
        var request = JsonSerializer.Deserialize<ScenarioWriteRequest>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scenario-request.json")), Json)!;
        return (request with { LayoutVersionId = layout.Version.Id }, layout);
    }
    private static ScenarioInputEvent Surge(ScenarioWriteRequest draft, PublishedLayout layout, double share = 1) =>
        new(Guid.NewGuid(), IncidentScenarios.PassengerSurge, draft.Inputs.StartsAtUtc.AddMinutes(5),
            layout.Layout.Routes.Single(r => r.Id == draft.Inputs.Flows[0].RouteId).FromCheckpointId,
            draft.Inputs.Flights[0].GateId, draft.Inputs.Flights[0].Id,
            JsonSerializer.Serialize(new PassengerSurgePayload("1.0", 1, share, true), Json));
    private static ScenarioWriteRequest With(ScenarioWriteRequest draft, params ScenarioInputEvent[] incidents) =>
        draft with { Inputs = draft.Inputs with { Events = [.. draft.Inputs.Events, .. incidents] } };
    private static async Task<ScenarioDetails> Save(ManagementHost host, ScenarioWriteRequest request)
    {
        using var response = await host.Client.PostAsJsonAsync("/scenarios", request, Json, Token);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Token));
        return (await response.Content.ReadFromJsonAsync<ScenarioDetails>(Json, Token))!;
    }
    private static async Task Error(HttpResponseMessage response, string field)
    {
        using (response)
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json, Token);
            Assert.Equal("validation_failed", body.GetProperty("code").GetString());
            Assert.Contains(body.GetProperty("errors").EnumerateObject(), property => property.Name.Contains(field, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Real_engine_applies_the_surge_and_replays_immutable_captured_inputs_after_editing()
    {
        await using var host = await ManagementHost.CreateAsync(realEngine: true);
        var (draft, layout) = await Draft(host); var incident = Surge(draft, layout);
        var request = With(draft, incident); var scenario = await Save(host, request); var id = scenario.Scenario.Id;
        Assert.Null(scenario.Inputs.PassengerGeneration); // Compilation never writes inferred profiles back.
        Assert.Equal(draft.Inputs.Populations, scenario.Inputs.Populations); Assert.Equal(draft.Inputs.Flows, scenario.Inputs.Flows);
        Assert.Equal(incident, scenario.Inputs.Events.Last());
        var settings = (await host.Client.GetFromJsonAsync<PassengerSettings>($"/scenarios/{id}/passengers/settings", Json, Token))!;
        Assert.Null(settings.Config); Assert.Equal(120, settings.Defaults.Batches.Single().ModeledCount);
        Assert.Empty(settings.Defaults.Batches.Single().Surges);
        var preview = (await host.Client.GetFromJsonAsync<PassengerGenerationResult>($"/scenarios/{id}/passengers?revision=1&limit=100000", Json, Token))!;
        Assert.Equal(120, preview.ModeledCount); Assert.True(preview.HasSubset);
        Assert.Equal(120, preview.Batches.Single().SurgePassengerCount);
        Assert.All(preview.Passengers, p => { Assert.True(p.IsSynthetic); Assert.True(p.InSurge); Assert.True(p.ArrivalMinute >= 5 && p.ArrivalMinute < 6); });
        using var start = await host.Client.PostAsync($"/scenarios/{id}/runs", null, Token);
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);
        var run = (await start.Content.ReadFromJsonAsync<SimulationRun>(Json, Token))!;
        Assert.True(await host.Dispatch(Token));
        var completed = (await host.Client.GetFromJsonAsync<RunDetails>($"/runs/{run.Id}", Json, Token))!;
        Assert.Equal("completed", completed.Status); Assert.True(completed.ResultsValid);
        var captured = JsonSerializer.Deserialize<RunInputSnapshot>(completed.InputSnapshotJson!, Json)!;
        Assert.Equal(incident, captured.Scenario.Inputs.Events.Last());
        var engineRequest = CapturedSimulation.Request(run.Id, captured);
        Assert.Equal(new PassengerDemandSurge(5, 6, 1), engineRequest.PassengerGeneration!.Batches.Single().Surges.Single());
        Assert.Equal(run.InputHash, RunFingerprint.Input(engineRequest));
        using var edit = await host.Client.PutAsJsonAsync($"/scenarios/{id}/events/{incident.Id}",
            new EventWriteRequest(1, incident with { OccurredAtUtc = draft.Inputs.StartsAtUtc.AddMinutes(15) }), Json, Token);
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var changed = (await host.Client.GetFromJsonAsync<PassengerGenerationResult>($"/scenarios/{id}/passengers?revision=2&limit=100000", Json, Token))!;
        Assert.NotEqual(preview.Fingerprint, changed.Fingerprint);
        Assert.All(changed.Passengers, p => Assert.True(p.ArrivalMinute >= 15 && p.ArrivalMinute < 16));
        var replay = (await host.Client.GetFromJsonAsync<PassengerGenerationResult>($"/runs/{run.Id}/passengers?limit=100000", Json, Token))!;
        Assert.Equal(preview.Fingerprint, replay.Fingerprint); Assert.Equal(preview.Passengers, replay.Passengers);
        using var removed = await host.Client.DeleteAsync($"/scenarios/{id}/events/{incident.Id}?revision=2", Token);
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        var without = (await removed.Content.ReadFromJsonAsync<ScenarioDetails>(Json, Token))!;
        Assert.False(IncidentScenarios.HasIncidents(without.Inputs)); Assert.Null(without.Inputs.PassengerGeneration);
        Assert.Equal(draft.Inputs.Populations, without.Inputs.Populations);
        Assert.Equal(preview.Fingerprint, (await host.Client.GetFromJsonAsync<PassengerGenerationResult>($"/runs/{run.Id}/passengers?limit=100000", Json, Token))!.Fingerprint);
    }

    [Theory]
    [InlineData("missing", "payloadJson")]
    [InlineData("wrong-type", "payloadJson")]
    [InlineData("extra", "payloadJson")]
    [InlineData("duplicate-key", "payloadJson")]
    [InlineData("version", "contractVersion")]
    [InlineData("real", "isSynthetic")]
    [InlineData("zero-share", "share")]
    [InlineData("excess-share", "share")]
    [InlineData("zero-duration", "durationMinutes")]
    [InlineData("horizon", "durationMinutes")]
    [InlineData("boarding", "durationMinutes")]
    [InlineData("flight", "flightId")]
    [InlineData("checkpoint", "checkpointId")]
    [InlineData("gate", "gateId")]
    [InlineData("unknown", "type")]
    [InlineData("duplicate-id", "id")]
    [InlineData("empty-id", "id")]
    public async Task Invalid_incidents_are_rejected_on_validate_create_and_event_write_without_engine_calls(string kind, string field)
    {
        await using var host = await ManagementHost.CreateAsync(); var (draft, layout) = await Draft(host);
        var valid = Surge(draft, layout);
        string Payload(string version = "1.0", double duration = 1, double share = 1, bool demo = true) =>
            JsonSerializer.Serialize(new PassengerSurgePayload(version, duration, share, demo), Json);
        var invalid = kind switch {
            "missing" => valid with { PayloadJson = "{}" },
            "wrong-type" => valid with { PayloadJson = "{\"contractVersion\":\"1.0\",\"durationMinutes\":\"1\",\"share\":1,\"isSynthetic\":true}" },
            "extra" => valid with { PayloadJson = valid.PayloadJson.Replace("}", ",\"command\":\"real\"}") },
            "duplicate-key" => valid with { PayloadJson = valid.PayloadJson.Replace("}", ",\"share\":0.5}") },
            "version" => valid with { PayloadJson = Payload(version: "2.0") },
            "real" => valid with { PayloadJson = Payload(demo: false) },
            "zero-share" => valid with { PayloadJson = Payload(share: 0) },
            "excess-share" => valid with { PayloadJson = Payload(share: 1.1) },
            "zero-duration" => valid with { PayloadJson = Payload(duration: 0) },
            "horizon" => valid with { PayloadJson = Payload(duration: 60) },
            "boarding" => valid with { OccurredAtUtc = draft.Inputs.StartsAtUtc.AddMinutes(49), PayloadJson = Payload(duration: 2) },
            "flight" => valid with { FlightId = null },
            "checkpoint" => valid with { CheckpointId = null },
            "gate" => valid with { GateId = null },
            "unknown" => valid with { Type = "incident.close-real-gate" },
            "duplicate-id" => valid with { Id = draft.Inputs.Events[0].Id },
            _ => valid with { Id = Guid.Empty }
        };
        await Error(await host.Client.PostAsJsonAsync("/scenarios/validate", With(draft, invalid), Json, Token), field);
        await Error(await host.Client.PostAsJsonAsync("/scenarios", With(draft, invalid), Json, Token), field);
        var scenario = await Save(host, draft); var id = scenario.Scenario.Id;
        if (kind == "duplicate-id")
        {
            using var conflict = await host.Client.PostAsJsonAsync($"/scenarios/{id}/events", new EventWriteRequest(1, invalid), Json, Token);
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        }
        else await Error(await host.Client.PostAsJsonAsync($"/scenarios/{id}/events", new EventWriteRequest(1, invalid), Json, Token), field);
        var unchanged = (await host.Client.GetFromJsonAsync<ScenarioDetails>($"/scenarios/{id}", Json, Token))!;
        Assert.Equal(1, unchanged.Scenario.Revision); Assert.Equal(draft.Inputs.Events, unchanged.Inputs.Events);
        Assert.Empty((await host.Client.GetFromJsonAsync<SimulationRun[]>($"/scenarios/{id}/runs", Json, Token))!);
        Assert.Null(host.Engine.LastRequest); Assert.False(await host.Dispatch(Token));
    }

    [Fact]
    public async Task Native_profiles_and_incidents_compose_once_and_cannot_exceed_shared_limits()
    {
        await using var host = await ManagementHost.CreateAsync(); var (draft, layout) = await Draft(host);
        var config = PassengerScenarios.Defaults(draft.Inputs, layout.Layout);
        config = config with { Batches = [config.Batches[0] with { ModeledCount = 120, CountMode = "subset", Surges = [new(1, 2, .2)] }] };
        draft = draft with { Inputs = draft.Inputs with { PassengerGeneration = config } };
        var request = With(draft, Surge(draft, layout, .8));
        var saved = await Save(host, request);
        var first = PassengerScenarios.Request(saved.Inputs, layout.Layout);
        var second = PassengerScenarios.Request(saved.Inputs, layout.Layout);
        Assert.Equal(2, first.Batches.Single().Surges.Count); Assert.Equal(JsonSerializer.Serialize(first, Json), JsonSerializer.Serialize(second, Json));
        Assert.Single(saved.Inputs.PassengerGeneration!.Batches.Single().Surges);
        var tooMuch = request with { Code = "too-much", Inputs = request.Inputs with { Events = [.. request.Inputs.Events, Surge(draft, layout, .01)] } };
        await Error(await host.Client.PostAsJsonAsync("/scenarios", tooMuch, Json, Token), "share");
        var tooMany = draft with { Code = "too-many", Inputs = draft.Inputs with { Events = [.. draft.Inputs.Events, .. Enumerable.Range(0, 16).Select(_ => Surge(draft, layout, .01))] } };
        await Error(await host.Client.PostAsJsonAsync("/scenarios", tooMany, Json, Token), "events");
        var narrow = request with { Code = "narrow", Inputs = request.Inputs with { PassengerGeneration = config with {
            Batches = [config.Batches[0] with { Arrival = new("uniform", 0, 4, 2, 1, 2) }] } } };
        await Error(await host.Client.PostAsJsonAsync("/scenarios", narrow, Json, Token), "durationMinutes");
        // A duplicate batch is a readable validation error, never a SingleOrDefault exception.
        var duplicate = request with { Code = "duplicate", Inputs = request.Inputs with { PassengerGeneration = config with { Batches = [config.Batches[0], config.Batches[0]] } } };
        await Error(await host.Client.PostAsJsonAsync("/scenarios", duplicate, Json, Token), "passengerGeneration");
    }

    [Fact]
    public async Task Captured_execution_revalidates_incidents_and_annotations_keep_the_aggregate_engine()
    {
        await using var host = await ManagementHost.CreateAsync(); var (draft, layout) = await Draft(host);
        var scenario = await Save(host, draft);
        var captured = new RunInputSnapshot(1, DateTime.UtcNow, scenario, layout, KpiConventions.Engine, 1);
        Assert.Null(CapturedSimulation.Request(Guid.NewGuid(), captured).PassengerGeneration);
        var invalidInputs = draft.Inputs with { Events = [Surge(draft, layout) with { PayloadJson = "{}" }] };
        var invalid = captured with { Scenario = scenario with { Inputs = invalidInputs } };
        Assert.Throws<ScenarioValidationException>(() => CapturedSimulation.Request(Guid.NewGuid(), invalid));
        Assert.Null(host.Engine.LastRequest);
    }
}

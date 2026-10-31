using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using AeroSynapse.Infrastructure.Storage;
using Xunit;

namespace AeroSynapse.Management.Tests;

public sealed class ScenarioStudioTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static async Task<ScenarioWriteRequest> Request(ManagementHost host)
    {
        using var reply = await host.Client.PostAsync($"/scenes/{SyntheticScenarioSeeder.SceneId}/layout-versions", null, Token);
        var layout = (await reply.Content.ReadFromJsonAsync<PublishedLayout>(Json, Token))!;
        return JsonSerializer.Deserialize<ScenarioWriteRequest>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scenario-request.json")), Json)!
            with { LayoutVersionId = layout.Version.Id };
    }
    [Fact]
    public async Task Validation_checks_the_published_layout_without_creating_or_modifying_scenarios()
    {
        await using var host = await ManagementHost.CreateAsync(); var request = await Request(host);
        using var valid = await host.Client.PostAsJsonAsync("/scenarios/validate", request, Json, Token);
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode); Assert.True((await valid.Content.ReadFromJsonAsync<ScenarioValidationResult>(Json, Token))!.IsValid);
        Assert.Empty((await host.Client.GetFromJsonAsync<ScenarioSummary[]>("/scenarios", Json, Token))!);
        using var missing = await host.Client.PostAsJsonAsync("/scenarios/validate", request with { LayoutVersionId = Guid.NewGuid() }, Json, Token);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.True((await missing.Content.ReadFromJsonAsync<JsonElement>(Json, Token)).GetProperty("errors").TryGetProperty("layoutVersionId", out _));
    }
    [Theory]
    [InlineData("capacity", "inputs.flows")]
    [InlineData("flight", "inputs.flights[0].gateId")]
    [InlineData("time", "inputs.flights[0].boardingClosesAtUtc")]
    [InlineData("staff", "inputs.staffAllocations[1].staffMemberId")]
    public async Task Invalid_capacity_flight_time_and_staff_return_field_errors_without_changing_the_original(string kind, string field)
    {
        await using var host = await ManagementHost.CreateAsync(); var request = await Request(host);
        using var created = await host.Client.PostAsJsonAsync("/scenarios", request, Json, Token);
        var original = (await created.Content.ReadFromJsonAsync<ScenarioDetails>(Json, Token))!;
        var first = request.Inputs.Flights[0];
        var inputs = kind switch {
            "capacity" => request.Inputs with { Flights = [first with { PassengerCapacity = 1 }] },
            "flight" => request.Inputs with { Flights = [first with { GateId = Guid.NewGuid() }] },
            "time" => request.Inputs with { Flights = [first with { BoardingClosesAtUtc = first.DepartureAtUtc }] },
            _ => request.Inputs with { StaffAllocations = [request.Inputs.StaffAllocations[0], request.Inputs.StaffAllocations[0]] }
        };
        using var invalid = await host.Client.PostAsJsonAsync("/scenarios/validate", request with { Inputs = inputs }, Json, Token);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var body = await invalid.Content.ReadFromJsonAsync<JsonElement>(Json, Token);
        Assert.Equal("validation_failed", body.GetProperty("code").GetString()); Assert.True(body.GetProperty("errors").TryGetProperty(field, out _));
        var reloaded = (await host.Client.GetFromJsonAsync<ScenarioDetails>($"/scenarios/{original.Scenario.Id}", Json, Token))!;
        Assert.Equal(1, reloaded.Scenario.Revision); Assert.Equal(JsonSerializer.Serialize(original.Inputs, Json), JsonSerializer.Serialize(reloaded.Inputs, Json));
    }
    [Fact]
    public async Task Editing_and_duplicating_round_trip_advanced_configuration_event_targets_and_synthetic_versions()
    {
        await using var host = await ManagementHost.CreateAsync(); var request = await Request(host);
        using var created = await host.Client.PostAsJsonAsync("/scenarios", request, Json, Token);
        var source = (await created.Content.ReadFromJsonAsync<ScenarioDetails>(Json, Token))!; var id = source.Scenario.Id;
        var passengerSettings = await host.Client.GetFromJsonAsync<JsonElement>($"/scenarios/{id}/passengers/settings", Json, Token);
        var navigationSettings = await host.Client.GetFromJsonAsync<JsonElement>($"/scenarios/{id}/navigation/settings", Json, Token);
        var events = request.Inputs.Events.Select(e => e with { PayloadJson = "{\"lanes\":2,\"metadata\":{\"note\":\"păstrează\",\"active\":true},\"counts\":[1,2]}" }).ToArray();
        var generation = passengerSettings.GetProperty("defaults").Deserialize<PassengerGenerationConfig>(Json)!;
        // The fixture models 120 of the flight's 130 seats, so retain its explicit subset.
        generation = generation with { Batches = generation.Batches.Select(batch => batch with { CountMode = "subset", ModeledCount = 120 }).ToArray() };
        var inputs = request.Inputs with { TimeZone = "Europe/Bucharest", Seed = 987, SnapshotIntervalMinutes = 3, Events = events,
            PassengerGeneration = generation,
            Navigation = navigationSettings.GetProperty("defaults").Deserialize<NavigationConfig>(Json) };
        using var edited = await host.Client.PutAsJsonAsync($"/scenarios/{id}", request with { Revision = 1, Name = "Studio edit", Inputs = inputs }, Json, Token);
        Assert.True(edited.StatusCode == HttpStatusCode.OK, await edited.Content.ReadAsStringAsync(Token));
        var loaded = (await host.Client.GetFromJsonAsync<ScenarioDetails>($"/scenarios/{id}", Json, Token))!;
        Assert.Equal(2, loaded.Scenario.Revision); Assert.True(loaded.Scenario.IsSynthetic); Assert.Equal(request.LayoutVersionId, loaded.Scenario.LayoutVersionId);
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(inputs, Json), JsonSerializer.SerializeToNode(loaded.Inputs, Json)));
        using var duplicate = await host.Client.PostAsJsonAsync($"/scenarios/{id}/duplicate", new DuplicateScenarioRequest("studio-copy", "Copie Studio"), Json, Token);
        Assert.Equal(HttpStatusCode.Created, duplicate.StatusCode); var copy = (await duplicate.Content.ReadFromJsonAsync<ScenarioDetails>(Json, Token))!;
        Assert.NotEqual(id, copy.Scenario.Id); Assert.Equal(1, copy.Scenario.Revision); Assert.True(copy.Scenario.IsSynthetic);
        Assert.True(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(inputs, Json), JsonSerializer.SerializeToNode(copy.Inputs, Json)));
        using var copyEdit = await host.Client.PutAsJsonAsync($"/scenarios/{copy.Scenario.Id}", request with {
            Code = copy.Scenario.Code, Name = copy.Scenario.Name, Revision = 1, Inputs = copy.Inputs with { Seed = 111 } }, Json, Token);
        Assert.Equal(HttpStatusCode.OK, copyEdit.StatusCode);
        var unchanged = (await host.Client.GetFromJsonAsync<ScenarioDetails>($"/scenarios/{id}", Json, Token))!;
        Assert.Equal(987, unchanged.Inputs.Seed); Assert.Equal(2, unchanged.Scenario.Revision);
    }
}

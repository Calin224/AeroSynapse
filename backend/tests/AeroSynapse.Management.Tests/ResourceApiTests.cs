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

public sealed class ResourceApiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static IntegratedResourceConfig Config(ResourceSettings settings)
    {
        var gate = settings.Gates.Single(item => item.Id == settings.Flights[0].GateId);
        var worker = settings.Staff.First(item => !item.ReservedForSecurity && item.TerminalId == gate.TerminalId);
        return new("1.0", [new(gate.Id, [new(0,settings.DurationMinutes)])],
            [new(worker.Id, ["boarding"], [new(0,settings.DurationMinutes)])],
            [new(Guid.NewGuid(), "Handling demo", [worker.Id], [gate.Id])],
            settings.Flights.Select(flight => new ResourceRequirement(flight.Id, [gate.Id], ["boarding"], 1)).ToArray());
    }

    [Fact]
    public async Task Save_solve_read_duplicate_and_disable_preserve_security_allocations_and_never_apply_a_proposal()
    {
        await using var host = await ManagementHost.CreateAsync(realEngine: true);
        var original = await host.CreateScenarioAsync(); var id = original.Scenario.Id;
        var path = $"/scenarios/{id}/resources";
        var settings = (await host.Client.GetFromJsonAsync<ResourceSettings>(path + "/settings", Json, Token))!;
        Assert.Null(settings.Resources); Assert.True(settings.Compatible);
        var config = Config(settings);
        using var saved = await host.Client.PutAsJsonAsync(path + "/settings", new ResourceConfigWriteRequest(settings.Revision, config), Json, Token);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var configured = (await saved.Content.ReadFromJsonAsync<ScenarioDetails>(Json, Token))!;
        Assert.Equal(original.Scenario.Revision + 1, configured.Scenario.Revision);
        // Compare persisted reads: PostgreSQL stores timestamps at microsecond precision.
        var before = (await host.Client.GetFromJsonAsync<ScenarioDetails>($"/scenarios/{id}", Json, Token))!;
        using var proposal = await host.Client.PostAsJsonAsync(path + "/proposals", new ResourceProposalRequest(configured.Scenario.Revision, 5), Json, Token);
        Assert.True(proposal.IsSuccessStatusCode, await proposal.Content.ReadAsStringAsync(Token));
        var result = (await proposal.Content.ReadFromJsonAsync<ResourceSolveResult>(ContractJson.Options, Token))!;
        Assert.Equal("optimal", result.Status); Assert.True(result.AdvisoryOnly); Assert.True(ResourceValidation.Valid(result.Constraints, result));
        var after = (await host.Client.GetFromJsonAsync<ScenarioDetails>($"/scenarios/{id}", Json, Token))!;
        Assert.Equal(JsonSerializer.Serialize(before, Json), JsonSerializer.Serialize(after, Json));
        Assert.Equal(JsonSerializer.Serialize(original.Inputs.StaffAllocations, Json), JsonSerializer.Serialize(after.Inputs.StaffAllocations, Json));
        Assert.Equal(JsonSerializer.Serialize(original.Inputs.Flights, Json), JsonSerializer.Serialize(after.Inputs.Flights, Json));
        Assert.Empty((await host.Client.GetFromJsonAsync<SimulationRun[]>($"/scenarios/{id}/runs", Json, Token))!);
        using var duplicate = await host.Client.PostAsJsonAsync($"/scenarios/{id}/duplicate", new DuplicateScenarioRequest("RESOURCE-COPY", "Copy"), Json, Token);
        Assert.True(duplicate.IsSuccessStatusCode, await duplicate.Content.ReadAsStringAsync(Token));
        var copy = (await duplicate.Content.ReadFromJsonAsync<ScenarioDetails>(Json, Token))!;
        Assert.Equal(JsonSerializer.Serialize(config, Json), JsonSerializer.Serialize(copy.Inputs.IntegratedResources, Json));
        using var disabled = await host.Client.PutAsJsonAsync(path + "/settings", new ResourceConfigWriteRequest(after.Scenario.Revision, null), Json, Token);
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        Assert.Null((await disabled.Content.ReadFromJsonAsync<ScenarioDetails>(Json, Token))!.Inputs.IntegratedResources);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Impossible_rules_and_timeout_are_returned_without_assignments(bool timeout)
    {
        await using var host = await ManagementHost.CreateAsync(realEngine: true);
        var original = await host.CreateScenarioAsync(); var path = $"/scenarios/{original.Scenario.Id}/resources";
        var settings = (await host.Client.GetFromJsonAsync<ResourceSettings>(path + "/settings", Json, Token))!;
        var config = Config(settings);
        if (!timeout) config = config with { Gates = config.Gates.Select(item => item with { Availability = [] }).ToArray() };
        using var saved = await host.Client.PutAsJsonAsync(path + "/settings", new ResourceConfigWriteRequest(settings.Revision, config), Json, Token);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var scenario = (await saved.Content.ReadFromJsonAsync<ScenarioDetails>(Json, Token))!;
        using var response = await host.Client.PostAsJsonAsync(path + "/proposals", new ResourceProposalRequest(scenario.Scenario.Revision, timeout ? 0 : 5), Json, Token);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Token));
        var result = (await response.Content.ReadFromJsonAsync<ResourceSolveResult>(ContractJson.Options, Token))!;
        Assert.Equal(timeout ? "timeout" : "infeasible", result.Status); Assert.Empty(result.Assignments); Assert.Null(result.Score);
        if (!timeout) Assert.Contains(result.Rules, item => item.Code == "gate_availability" && item.ResourceId == config.Gates[0].GateId);
    }

    [Fact]
    public async Task Opt_in_unknown_resources_security_reuse_and_stale_revisions_fail_before_solving()
    {
        await using var host = await ManagementHost.CreateAsync();
        var created = await host.CreateScenarioAsync();
        var scenario = (await host.Client.GetFromJsonAsync<ScenarioDetails>($"/scenarios/{created.Scenario.Id}", Json, Token))!;
        var path = $"/scenarios/{scenario.Scenario.Id}/resources";
        using var noConfig = await host.Client.PostAsJsonAsync(path + "/proposals", new ResourceProposalRequest(scenario.Scenario.Revision, 5), Json, Token);
        Assert.Equal(HttpStatusCode.BadRequest, noConfig.StatusCode);
        var settings = (await host.Client.GetFromJsonAsync<ResourceSettings>(path + "/settings", Json, Token))!;
        var config = Config(settings);
        using var stale = await host.Client.PutAsJsonAsync(path + "/settings", new ResourceConfigWriteRequest(settings.Revision + 1, config), Json, Token);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var reserved = settings.Staff.First(item => item.ReservedForSecurity);
        var invalid = config with { Workers = [new(reserved.Id, ["boarding"], [new(0,60)])],
            Teams = config.Teams.Select(item => item with { MemberIds = [reserved.Id] }).ToArray() };
        using var security = await host.Client.PutAsJsonAsync(path + "/settings", new ResourceConfigWriteRequest(settings.Revision, invalid), Json, Token);
        Assert.Equal(HttpStatusCode.BadRequest, security.StatusCode);
        var after = (await host.Client.GetFromJsonAsync<ScenarioDetails>($"/scenarios/{scenario.Scenario.Id}", Json, Token))!;
        Assert.Equal(JsonSerializer.Serialize(scenario, Json), JsonSerializer.Serialize(after, Json));
    }
}

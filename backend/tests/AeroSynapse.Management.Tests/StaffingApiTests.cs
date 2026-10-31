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

namespace AeroSynapse.Management.Tests;

public sealed class StaffingApiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static async Task<ScenarioDetails> Create(ManagementHost host, bool profiles = true)
    {
        using var published = await host.Client.PostAsync($"/scenes/{SyntheticScenarioSeeder.SceneId}/layout-versions", null, Token);
        var layout = (await published.Content.ReadFromJsonAsync<PublishedLayout>(Json, Token))!;
        var request = JsonSerializer.Deserialize<ScenarioWriteRequest>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scenario-request.json")), Json)!;
        using var created = await host.Client.PostAsJsonAsync("/scenarios", request with { LayoutVersionId = layout.Version.Id }, Json, Token);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var scenario = (await created.Content.ReadFromJsonAsync<ScenarioDetails>(Json, Token))!;
        if (!profiles) return scenario;
        var settings = (await host.Client.GetFromJsonAsync<PassengerSettings>($"/scenarios/{scenario.Scenario.Id}/passengers/settings", Json, Token))!;
        using var configured = await host.Client.PutAsJsonAsync($"/scenarios/{scenario.Scenario.Id}/passengers/settings",
            new PassengerConfigWriteRequest(settings.Revision, 42, settings.Defaults), Json, Token);
        Assert.Equal(HttpStatusCode.OK, configured.StatusCode);
        return (await configured.Content.ReadFromJsonAsync<ScenarioDetails>(Json, Token))!;
    }
    private static StaffingProposalRequest Proposal(StaffingSettings settings) => new(settings.Revision, settings.AvailableStaff, 5,
        settings.Checkpoints.Select(item => new StaffingConstraint(item.Id, 0, 6, 2, false, false, 10)).ToArray());

    [Fact]
    public async Task Real_profile_forecast_and_CP_SAT_return_feasible_limits_score_and_leave_the_scenario_unchanged()
    {
        await using var host = await ManagementHost.CreateAsync(realEngine: true);
        var scenario = await Create(host); var id = scenario.Scenario.Id;
        var settings = (await host.Client.GetFromJsonAsync<StaffingSettings>($"/scenarios/{id}/staffing/settings", Json, Token))!;
        Assert.True(settings.PassengerProfilesActive); Assert.Equal(SyntheticScenarioSeeder.StaffSlots, settings.AvailableStaff);
        var before = (await host.Client.GetFromJsonAsync<ScenarioDetails>($"/scenarios/{id}", Json, Token))!;
        var proposal = Proposal(settings);
        proposal = proposal with { Checkpoints = proposal.Checkpoints.Select((item, index) => index == 1 ? item with { Closed = true }
            : index == 0 ? item with { RequiredOpen = true, MinimumStaff = 1 } : item).ToArray() };
        using var response = await host.Client.PostAsJsonAsync($"/scenarios/{id}/staffing/proposals", proposal, Json, Token);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Token));
        var result = (await response.Content.ReadFromJsonAsync<StaffingSolveResult>(ContractJson.Options, Token))!;
        Assert.Equal("optimal", result.Status); Assert.True(StaffingValidation.Valid(result.Constraints, result));
        Assert.Equal(id, result.Constraints.ScenarioId); Assert.Equal(scenario.Scenario.Revision, result.Constraints.ScenarioRevision);
        Assert.Contains(result.Constraints.Checkpoints, item => item.DemandMilli > 0 && item.CapacityMilli > 0);
        Assert.Equal(0, result.Assignments.Single(item => item.CheckpointId == settings.Checkpoints[1].Id).StaffCount);
        Assert.All(result.Assignments, item => Assert.InRange(item.StaffCount, 0, 2));
        Assert.InRange(result.UsedStaff!.Value, 1, settings.AvailableStaff);
        var original = (await host.Client.GetFromJsonAsync<ScenarioDetails>($"/scenarios/{id}", Json, Token))!;
        Assert.Equal(JsonSerializer.Serialize(before, Json), JsonSerializer.Serialize(original, Json));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Infeasible_and_expired_budgets_are_not_reported_as_successful_configurations(bool timeout)
    {
        await using var host = await ManagementHost.CreateAsync(realEngine: true);
        var scenario = await Create(host); var id = scenario.Scenario.Id;
        var settings = (await host.Client.GetFromJsonAsync<StaffingSettings>($"/scenarios/{id}/staffing/settings", Json, Token))!;
        var proposal = Proposal(settings);
        proposal = timeout ? proposal with { TimeLimitSeconds = 0 } : proposal with { AvailableStaff = 0,
            Checkpoints = proposal.Checkpoints.Select(item => item with { RequiredOpen = true, MinimumStaff = 1 }).ToArray() };
        using var response = await host.Client.PostAsJsonAsync($"/scenarios/{id}/staffing/proposals", proposal, Json, Token);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Token));
        var result = (await response.Content.ReadFromJsonAsync<StaffingSolveResult>(ContractJson.Options, Token))!;
        Assert.Equal(timeout ? "timeout" : "infeasible", result.Status);
        Assert.Empty(result.Assignments); Assert.Null(result.Score); Assert.Null(result.UsedStaff);
    }

    [Fact]
    public async Task Stale_unknown_duplicate_and_excess_staff_inputs_are_rejected_before_solving()
    {
        await using var host = await ManagementHost.CreateAsync();
        var scenario = await Create(host); var id = scenario.Scenario.Id;
        var settings = (await host.Client.GetFromJsonAsync<StaffingSettings>($"/scenarios/{id}/staffing/settings", Json, Token))!;
        var proposal = Proposal(settings);
        foreach (var (invalid, status) in new[]
        {
            (proposal with { Revision = settings.Revision + 1 }, HttpStatusCode.Conflict),
            (proposal with { AvailableStaff = settings.AvailableStaff + 1 }, HttpStatusCode.BadRequest),
            (proposal with { Checkpoints = proposal.Checkpoints.Skip(1).ToArray() }, HttpStatusCode.BadRequest),
            (proposal with { Checkpoints = [.. proposal.Checkpoints, proposal.Checkpoints[0]] }, HttpStatusCode.BadRequest),
            (proposal with { Checkpoints = proposal.Checkpoints.Select(item => item with { CheckpointId = Guid.NewGuid() }).ToArray() }, HttpStatusCode.BadRequest),
        })
        {
            using var response = await host.Client.PostAsJsonAsync($"/scenarios/{id}/staffing/proposals", invalid, Json, Token);
            Assert.Equal(status, response.StatusCode);
        }
        Assert.Null(host.Engine.LastRequest);
    }

    [Fact]
    public async Task Fastest_valid_passenger_profiles_can_be_planned_without_truncating_capacity()
    {
        await using var host = await ManagementHost.CreateAsync(realEngine: true);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TwinDbContext>();
            await db.Checkpoints.Where(item => item.SceneId == SyntheticScenarioSeeder.SceneId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.CapacityPerMinute, 1000000), Token);
        }
        var scenario = await Create(host, profiles: false); var id = scenario.Scenario.Id;
        var passengerSettings = (await host.Client.GetFromJsonAsync<PassengerSettings>($"/scenarios/{id}/passengers/settings", Json, Token))!;
        var config = passengerSettings.Defaults with
        {
            Batches = passengerSettings.Defaults.Batches.Select(batch => batch with
            {
                Service = new("constant", .01, .01, .01, 0, .01),
                Groups = [new("solo", 1, 1, 1, .1, 1)]
            }).ToArray(),
            Flow = new([new("standard", 1, false, "security", .1, 1)], [], [])
        };
        using var saved = await host.Client.PutAsJsonAsync($"/scenarios/{id}/passengers/settings",
            new PassengerConfigWriteRequest(passengerSettings.Revision, 42, config), Json, Token);
        Assert.True(saved.StatusCode == HttpStatusCode.OK, await saved.Content.ReadAsStringAsync(Token));
        var settings = (await host.Client.GetFromJsonAsync<StaffingSettings>($"/scenarios/{id}/staffing/settings", Json, Token))!;
        using var response = await host.Client.PostAsJsonAsync($"/scenarios/{id}/staffing/proposals", Proposal(settings), Json, Token);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Token));
        var result = (await response.Content.ReadFromJsonAsync<StaffingSolveResult>(ContractJson.Options, Token))!;
        Assert.Equal("optimal", result.Status);
        Assert.All(result.Constraints.Checkpoints.Where(item => item.DemandMilli > 0),
            item => Assert.Equal(10000000000000L, item.CapacityMilli));
        Assert.True(StaffingValidation.Valid(result.Constraints, result));
    }
}

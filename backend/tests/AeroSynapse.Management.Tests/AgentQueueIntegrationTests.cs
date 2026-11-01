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
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace AeroSynapse.Management.Tests;

public sealed class AgentQueueIntegrationTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task The_Copilot_proposes_opening_a_control_for_a_long_queue_without_changing_the_scenario()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SIMULATION_TEST_URL")),
            "Set SIMULATION_TEST_URL to the rebuilt Python engine.");
        await using var host = await ManagementHost.CreateAsync(realEngine: true);
        var source = await StaffingEvaluationTests.Create(host, multipleCheckpoints: true);
        var busy = SyntheticScenarioSeeder.Id(101);
        var profiles = source.Inputs.PassengerGeneration!;
        var config = profiles with
        {
            Batches = profiles.Batches.Select(batch => batch with
            {
                CheckpointId = busy,
                CheckpointMode = "fixed",
                Arrival = new("constant", 0, 0, 0, 0, 0),
                Service = new("constant", 20, 20, 20, 0, 20),
                Groups = [new("solo", 1, 1, 1, 1, 1)],
                Surges = []
            }).ToArray()
        };
        using (var response = await host.Client.PutAsJsonAsync($"/scenarios/{source.Scenario.Id}/passengers/settings",
                   new PassengerConfigWriteRequest(source.Scenario.Revision, source.Inputs.Seed, config), Json, Token))
        {
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Token));
            source = (await response.Content.ReadFromJsonAsync<ScenarioDetails>(Json, Token))!;
        }
        source = await Save(host, source, source.Inputs with
        {
            StaffAllocations = [source.Inputs.StaffAllocations.First(item => item.CheckpointId == busy)],
            Events = [],
            SnapshotIntervalMinutes = 1,
            Agent = null
        });
        var navigation = (await host.Client.GetFromJsonAsync<NavigationSettings>($"/scenarios/{source.Scenario.Id}/navigation/settings", Json, Token))!;
        source = await Save(host, source, source.Inputs with { Navigation = navigation.Defaults });
        var baseline = await Run(host, source.Scenario.Id);
        using (var response = await host.Client.PostAsJsonAsync($"/scenarios/{source.Scenario.Id}/runs/{baseline.Id}/copilot",
                   new CopilotQuestion(busy, "Ce puncte de control recomanzi să deschid și cum distribui coada?", 5), Json, Token))
        {
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Token));
            var answer = (await response.Content.ReadFromJsonAsync<CopilotAnswer>(Json, Token))!;
            Assert.Equal("answered", answer.Status);
            Assert.Equal("queue_advice", answer.ReasonCode);
            Assert.Contains(answer.QueueAdvice!.Options, option => option.Kind == "checkpoint_open");
            Assert.All(answer.QueueAdvice.Facts, fact => Assert.Equal(baseline.Id, fact.RunId));
            var unchanged = (await host.Client.GetFromJsonAsync<ScenarioDetails>($"/scenarios/{source.Scenario.Id}", Json, Token))!;
            Assert.Equal(source.Scenario.Revision, unchanged.Scenario.Revision);
        }
    }

    private static async Task<ScenarioDetails> Save(ManagementHost host, ScenarioDetails source, ScenarioInputs inputs)
    {
        using var response = await host.Client.PutAsJsonAsync($"/scenarios/{source.Scenario.Id}",
            new ScenarioWriteRequest(source.Scenario.Code, source.Scenario.Name, source.Scenario.Description,
                source.Scenario.SceneId, source.Scenario.LayoutVersionId, inputs, source.Scenario.Revision), Json, Token);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Token));
        return (await response.Content.ReadFromJsonAsync<ScenarioDetails>(Json, Token))!;
    }

    private static async Task<RunDetails> Run(ManagementHost host, Guid scenario)
    {
        var started = await host.StartRunAsync(scenario);
        var run = (await host.Client.GetFromJsonAsync<RunDetails>($"/runs/{started.Id}", Json, Token))!;
        Assert.True(run.Status == "completed", $"{run.Status}: {run.FailureReason}; {JsonSerializer.Serialize(run.Error, Json)}");
        return run;
    }
}

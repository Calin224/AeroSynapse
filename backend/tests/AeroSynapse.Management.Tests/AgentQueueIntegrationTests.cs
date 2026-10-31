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

    private sealed class QueueAgentModel : IAgentModel
    {
        public AgentStatus Status => new(true, "queue-test");

        public Task<AgentChoice> ChooseAsync(AgentPrompt prompt, CancellationToken token)
        {
            var option = prompt.Options.FirstOrDefault(item => item.Kind == "checkpoint_open")
                ?? prompt.Options.FirstOrDefault(item => item.Kind == "queue_redistribution")
                ?? prompt.Options.First(item => item.Kind == "none");
            return Task.FromResult(new AgentChoice("chosen", option.Id, "Distribuie coada către controlul disponibil.", "queue-test"));
        }
    }

    [Fact]
    public async Task A_real_run_opens_a_control_redistributes_waiting_passengers_and_preserves_the_KPI_prefix()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SIMULATION_TEST_URL")),
            "Set SIMULATION_TEST_URL to the rebuilt Python engine.");
        await using var host = await ManagementHost.CreateAsync(realEngine: true, services: services =>
        {
            services.RemoveAll<IAgentModel>();
            services.AddSingleton<IAgentModel>(new QueueAgentModel());
        });
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
        source = await Save(host, source, source.Inputs with
        {
            Agent = new(StaffMoves: true, CheckInDesks: false, GateChoices: false,
                DecisionIntervalMinutes: 5, TransferMinutes: 2, ReserveOperators: 1)
        });
        var run = await Run(host, source.Scenario.Id);
        var decisions = (await host.Client.GetFromJsonAsync<AgentDecisionView[]>($"/runs/{run.Id}/agent-decisions", Json, Token))!;
        var opened = Assert.Single(decisions, item => item.Kind == "checkpoint_open");
        Assert.Equal("chosen", opened.ModelStatus);
        var opening = opened.Options.Single(item => item.Id == opened.OptionId);
        var target = opening.ToCheckpointId!.Value;
        Assert.NotEqual(busy, target);
        Assert.Equal(1, opening.Count);

        var report = (await host.Client.GetFromJsonAsync<ProcessResult>($"/runs/{run.Id}/processes?limit=100000", Json, Token))!;
        Assert.Contains(report.Events!, item => item.Type == "queue_redistribution");
        var routes = (await host.Client.GetFromJsonAsync<NavigationResult>($"/runs/{run.Id}/navigation?limit=100000", Json, Token))!;
        Assert.All(report.Passengers.Where(p => p.RedistributedAtMinute is not null), p =>
            Assert.Equal(p.CheckpointId, routes.Journeys.Single(j => j.Id == p.Id).CheckpointId));
        var before = (await host.Client.GetFromJsonAsync<ProcessResult>($"/runs/{baseline.Id}/processes?limit=100000", Json, Token))!;
        Assert.Equal(before.PopulationCount, report.PopulationCount);
        Assert.Equal(before.Passengers.Select(person => (person.Id, person.ArrivalMinute, person.ServiceMinutes)),
            report.Passengers.Select(person => (person.Id, person.ArrivalMinute, person.ServiceMinutes)));
        var transferred = report.Passengers.Where(person => person.RedistributedAtMinute is not null).ToArray();
        Assert.NotEmpty(transferred);
        Assert.Contains(transferred, person => person.CheckpointId == target && person.SecurityClearedMinute is not null);
        Assert.All(transferred, person =>
        {
            Assert.Equal(busy, person.AssignedCheckpointId);
            Assert.True(person.RedistributedAtMinute > opened.Minute);
            Assert.True(person.TransferMinutes >= 1);
            if (person.ProcessingStartedMinute is { } started)
                Assert.True(started >= person.RedistributedAtMinute + person.TransferMinutes);
        });
        Assert.All(report.Checkpoints, checkpoint => Assert.True(checkpoint.PeakBusy <= checkpoint.Capacity));
        var baselineInService = before.Passengers.Where(person => person.ProcessingStartedMinute <= opened.Minute).ToArray();
        Assert.NotEmpty(baselineInService);
        Assert.All(baselineInService, person =>
        {
            var actual = report.Passengers.Single(item => item.Id == person.Id);
            Assert.Equal(busy, actual.CheckpointId);
            Assert.Equal(person.ProcessingStartedMinute, actual.ProcessingStartedMinute);
            Assert.Equal(person.SecurityClearedMinute, actual.SecurityClearedMinute);
        });

        var baselineRows = await Rows(host, baseline.Id);
        var actualRows = await Rows(host, run.Id);
        var prefix = baselineRows.Where(row => row.Minute <= opened.Minute).ToArray();
        Assert.NotEmpty(prefix);
        foreach (var expected in prefix)
        {
            var actual = actualRows.Single(row => row.ZoneId == expected.ZoneId && row.Minute == expected.Minute);
            Assert.Equal((expected.PopulationCount, expected.ArrivedPassengers, expected.QueuedPassengers, expected.ProcessingPassengers,
                    expected.ProcessedPassengers, expected.EffectiveCapacity ?? expected.Capacity, expected.MeanWaitMinutes),
                (actual.PopulationCount, actual.ArrivedPassengers, actual.QueuedPassengers, actual.ProcessingPassengers,
                    actual.ProcessedPassengers, actual.EffectiveCapacity, actual.MeanWaitMinutes));
        }
        var firstTransfer = transferred.Min(person => person.RedistributedAtMinute!.Value);
        Assert.All(actualRows.Where(row => row.ZoneId == target && row.Minute <= opened.Minute), row =>
            Assert.Equal(0, row.EffectiveCapacity));
        Assert.Contains(actualRows, row => row.ZoneId == target && row.Minute >= firstTransfer && row.EffectiveCapacity > 0);
        Assert.Contains(actualRows, row => row.ZoneId == target && row.QueuedPassengers + row.ProcessedPassengers > 0);
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

    private static async Task<KpiSnapshot[]> Rows(ManagementHost host, Guid run)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TwinDbContext>();
        var payloads = await db.Snapshots.AsNoTracking().Where(item => item.RunId == run && item.ZoneType == "checkpoint")
            .Select(item => item.PayloadJson).ToArrayAsync(Token);
        return payloads.Select(payload => JsonSerializer.Deserialize<KpiSnapshot>(payload, ContractJson.Options)!).ToArray();
    }
}

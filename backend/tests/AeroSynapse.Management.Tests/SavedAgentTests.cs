using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Storage;
using AeroSynapse.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AeroSynapse.Management.Tests;

// The live agent was removed: a scenario saved with it still loads and saves, and runs exactly as captured.
public sealed class SavedAgentTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task A_saved_agent_no_longer_changes_the_run()
    {
        await using var host = await ManagementHost.CreateAsync();
        using var engine = new HttpClient { BaseAddress = new Uri(Environment.GetEnvironmentVariable("SIMULATION_TEST_URL") ?? "http://intelligence:8000") };
        // Passenger-profile captures need real observed results, while the recorder keeps the exact engine input.
        host.Engine.Behavior = async (request, token) =>
        {
            using var response = await engine.PostAsJsonAsync("/simulation/kpi-run", request, ContractJson.Options, token);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<SimulationResponse>(ContractJson.Options, token))!;
        };
        var source = await StaffingEvaluationTests.Create(host, multipleCheckpoints: true);
        using var changed = await host.Client.PutAsJsonAsync($"/scenarios/{source.Scenario.Id}", new ScenarioWriteRequest(source.Scenario.Code,
            source.Scenario.Name, source.Scenario.Description, source.Scenario.SceneId, source.Scenario.LayoutVersionId,
            source.Inputs with { Agent = new(StaffMoves: true, CheckInDesks: false, GateChoices: false, DecisionIntervalMinutes: 10, TransferMinutes: 2, ReserveOperators: 4) },
            source.Scenario.Revision), Json, Token);
        Assert.True(changed.StatusCode == HttpStatusCode.OK, await changed.Content.ReadAsStringAsync(Token));
        var scenario = (await changed.Content.ReadFromJsonAsync<ScenarioDetails>(Json, Token))!;
        Assert.NotNull(scenario.Inputs.Agent);

        var run = await host.StartRunAsync(scenario.Scenario.Id);
        Assert.Equal("completed", (await host.Client.GetFromJsonAsync<RunDetails>($"/runs/{run.Id}", Json, Token))!.Status);
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TwinDbContext>();
        Assert.False(await db.AgentDecisions.AnyAsync(d => d.RunId == run.Id, Token));
        var stored = await db.Runs.AsNoTracking().SingleAsync(r => r.Id == run.Id, Token);
        var captured = JsonSerializer.Deserialize<RunInputSnapshot>(stored.InputSnapshotJson!, Json)!;
        // The engine received the captured day unchanged: no staffing steps were added mid-run.
        Assert.Equal(RunFingerprint.Input(CapturedSimulation.Request(run.Id, captured)), RunFingerprint.Input(host.Engine.LastRequest!));
    }
}

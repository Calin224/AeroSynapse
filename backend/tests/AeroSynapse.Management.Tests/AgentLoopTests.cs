using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using AeroSynapse.Infrastructure.Execution;
using AeroSynapse.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace AeroSynapse.Management.Tests;

/// <summary>Picks the first offered move, or answers with a fixed status.</summary>
internal sealed class ScriptedAgentModel(string status = "chosen") : IAgentModel
{
    public int Calls;
    public AgentStatus Status => new(status == "chosen", "scripted");
    public Task<AgentChoice> ChooseAsync(AgentPrompt prompt, CancellationToken token)
    {
        Interlocked.Increment(ref Calls);
        var move = prompt.Options.FirstOrDefault(option => option.Kind != "none") ?? prompt.Options[0];
        return Task.FromResult(status == "chosen" ? new AgentChoice("chosen", move.Id, "Coada crește la control.", "scripted", 10, 5)
            : new AgentChoice(status, null, null, "scripted"));
    }
}

public sealed class AgentLoopTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<(ManagementHost Host, ScenarioDetails Scenario)> Start(ScriptedAgentModel model, ScenarioAgent? agent = null)
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SIMULATION_TEST_URL")), "Set SIMULATION_TEST_URL to the real Python engine.");
        var host = await ManagementHost.CreateAsync(realEngine: true, services: services =>
        { services.RemoveAll<IAgentModel>(); services.AddSingleton<IAgentModel>(model); });
        var source = await StaffingEvaluationTests.Create(host, multipleCheckpoints: true);
        using var changed = await host.Client.PutAsJsonAsync($"/scenarios/{source.Scenario.Id}", new ScenarioWriteRequest(source.Scenario.Code,
            source.Scenario.Name, source.Scenario.Description, source.Scenario.SceneId, source.Scenario.LayoutVersionId,
            source.Inputs with { Agent = agent ?? new(StaffMoves: true, CheckInDesks: false, GateChoices: false, DecisionIntervalMinutes: 10, TransferMinutes: 2) },
            source.Scenario.Revision), Json, Token);
        Assert.True(changed.StatusCode == HttpStatusCode.OK, await changed.Content.ReadAsStringAsync(Token));
        return (host, (await changed.Content.ReadFromJsonAsync<ScenarioDetails>(Json, Token))!);
    }

    private static async Task<RunDetails> Run(ManagementHost host, ScenarioDetails scenario)
    {
        using var started = await host.Client.PostAsync($"/scenarios/{scenario.Scenario.Id}/runs", null, Token);
        Assert.True(started.StatusCode == HttpStatusCode.Accepted, await started.Content.ReadAsStringAsync(Token));
        var run = (await started.Content.ReadFromJsonAsync<SimulationRun>(Json, Token))!;
        Assert.True(await host.Dispatch(Token));
        return (await host.Client.GetFromJsonAsync<RunDetails>($"/runs/{run.Id}", Json, Token))!;
    }

    [Fact]
    public async Task The_agent_moves_operators_during_the_run_and_the_run_carries_its_decisions()
    {
        var model = new ScriptedAgentModel();
        var (host, scenario) = await Start(model);
        await using var _ = host;
        var run = await Run(host, scenario);
        Assert.True(run.Status == "completed", run.FailureReason);
        var decisions = (await host.Client.GetFromJsonAsync<AgentDecisionView[]>($"/runs/{run.Id}/agent-decisions", Json, Token))!;
        Assert.Equal([10, 20, 30, 40, 50], decisions.Select(d => d.Minute));
        Assert.Contains(decisions, d => d.Kind == "staff_move" && d.ModelStatus == "chosen" && d.Reason.Length > 0);
        Assert.Equal(decisions.Length, model.Calls + decisions.Count(d => d.ModelStatus == "single_option"));
        // The engine ran the decided staffing: the moved control's capacity steps up after the transfer.
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TwinDbContext>();
        var first = decisions.First(d => d.Kind == "staff_move");
        var target = first.Options.Single(o => o.Id == first.OptionId).ToCheckpointId!.Value;
        var rows = await db.Snapshots.AsNoTracking().Where(s => s.RunId == run.Id && s.ZoneType == "checkpoint" && s.ZoneId == target).ToArrayAsync(Token);
        var capacity = rows.Select(row => JsonSerializer.Deserialize<KpiSnapshot>(row.PayloadJson, ContractJson.Options)!)
            .ToDictionary(row => row.Minute, row => row.EffectiveCapacity);
        Assert.True(capacity[first.Minute + 3] > capacity[first.Minute]);
        // Preparing the run again reuses every recorded decision without asking the model.
        var calls = model.Calls;
        var loop = scope.ServiceProvider.GetRequiredService<AgentLoop>();
        var stored = await db.Runs.AsNoTracking().SingleAsync(r => r.Id == run.Id, Token);
        var captured = JsonSerializer.Deserialize<RunInputSnapshot>(stored.InputSnapshotJson!, Json)!;
        var request = CapturedSimulation.Request(run.Id, captured);
        var again = await loop.PrepareAsync(stored, captured, request, Token);
        Assert.Equal(calls, model.Calls);
        Assert.NotNull(again.StaffSchedules);
        Assert.Equal(RunFingerprint.Input(again), RunFingerprint.Input(await loop.PrepareAsync(stored, captured, request, Token)));
    }

    [Fact]
    public async Task The_agent_calls_in_reserve_operators_until_the_reserve_runs_out()
    {
        var model = new ScriptedAgentModel();
        var (host, scenario) = await Start(model, new(StaffMoves: false, CheckInDesks: false, GateChoices: false,
            DecisionIntervalMinutes: 10, TransferMinutes: 2, ReserveOperators: 4));
        await using var _ = host;
        var run = await Run(host, scenario);
        Assert.True(run.Status == "completed", run.FailureReason);
        var decisions = (await host.Client.GetFromJsonAsync<AgentDecisionView[]>($"/runs/{run.Id}/agent-decisions", Json, Token))!;
        var calls = decisions.Where(d => d.Kind == "staff_call").Select(d => d.Options.Single(o => o.Id == d.OptionId)).ToArray();
        Assert.Equal(4, calls.Sum(call => call.Count));
        // Once the reserve is spent only "no change" is left, so the model is not asked again.
        Assert.All(decisions.SkipWhile(d => d.Kind == "staff_call"), d => Assert.Equal("single_option", d.ModelStatus));
        // The same day without the agent's calls, simulated by the real engine, and served again without recomputing.
        var impact = (await host.Client.GetFromJsonAsync<AgentImpact>($"/runs/{run.Id}/agent-impact", Json, Token))!;
        Assert.Equal(calls.GroupBy(call => call.ToCheckpointId).Count(), impact.Changes.Count);
        Assert.All(impact.Changes, change => Assert.Contains("din rezervă la CP-", change));
        Assert.NotNull(impact.WithAgent.MeanWaitMinutes);
        Assert.True(impact.WithAgent.MeanWaitMinutes <= impact.WithoutAgent.MeanWaitMinutes);
        var again = (await host.Client.GetFromJsonAsync<AgentImpact>($"/runs/{run.Id}/agent-impact", Json, Token))!;
        Assert.Equal((impact.WithoutAgent, impact.WithAgent), (again.WithoutAgent, again.WithAgent));
        Assert.Equal(impact.Changes, again.Changes);
    }

    [Theory]
    [InlineData("unconfigured")]
    [InlineData("timeout")]
    public async Task Without_a_model_answer_the_fallback_rule_decides(string status)
    {
        var model = new ScriptedAgentModel(status);
        var (host, scenario) = await Start(model);
        await using var _ = host;
        var run = await Run(host, scenario);
        Assert.True(run.Status == "completed", run.FailureReason);
        var decisions = (await host.Client.GetFromJsonAsync<AgentDecisionView[]>($"/runs/{run.Id}/agent-decisions", Json, Token))!;
        Assert.NotEmpty(decisions);
        Assert.All(decisions, d =>
        {
            Assert.Contains(d.ModelStatus, new[] { status, "single_option" });
            // Every action the rule takes says why; quiet steps stay "none".
            if (d.Kind != "none") Assert.StartsWith("Regulă de rezervă", d.Reason);
        });
    }
}

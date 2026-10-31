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

public sealed class ParetoEvaluationTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static ParetoOptions Options(ObjectiveWeights weights) => new(weights, new(10, 1000, .1), 1000000, 1, 10080, 10080, 1);
    private static async Task<StaffingEvaluationDetails> Read(ManagementHost host, Guid id) =>
        (await host.Client.GetFromJsonAsync<StaffingEvaluationDetails>($"/staffing-evaluations/{id}", Json, Token))!;

    [Fact]
    public void Solver_contract_reconciles_real_python_results_and_rejects_a_forged_objective()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "staffing");
        var request = ContractJson.Read<ParetoSearchRequest>(JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "pareto-request.json"))).RootElement, "ParetoSearchRequest");
        var result = ContractJson.Read<ParetoSearchResult>(JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "pareto-response.json"))).RootElement, "ParetoSearchResult");
        Assert.True(ParetoModel.ValidResult(request, result));
        var solves = result.Solves.ToArray(); solves[0] = solves[0] with { ScaledObjective = solves[0].ScaledObjective + 1 };
        var forged = result with { Solves = solves };
        Assert.False(ParetoModel.ValidResult(request, forged));
        Assert.Throws<ContractValidationException>(() => ContractJson.Read<ParetoSearchResult>(JsonSerializer.SerializeToElement(forged, Json), "ParetoSearchResult"));
    }

    [Fact]
    public void Dominance_is_independent_of_weights_and_cannot_promote_an_infeasible_or_equal_point()
    {
        var a = new ParetoPoint(Guid.NewGuid(), 0, true, [], true, [], 5, 200, .1, .5, .2, 1, .48);
        var b = a with { RunId = Guid.NewGuid(), TimeMinutes = 10, CostPoints = 120, RiskFraction = .2 };
        Assert.False(ParetoAnalysisPolicy.Dominates(a, b)); Assert.False(ParetoAnalysisPolicy.Dominates(b, a));
        Assert.False(ParetoAnalysisPolicy.Dominates(a, a));
        Assert.True(ParetoAnalysisPolicy.Dominates(a with { CostPoints = 150 }, a));
        Assert.False(ParetoAnalysisPolicy.Dominates(a with { Feasible = false, TimeMinutes = 0, CostPoints = 0, RiskFraction = 0 }, b));
    }

    [Fact]
    public async Task Real_weighted_resolves_produce_distinct_priorities_and_persist_simulated_tradeoffs()
    {
        await using var host = await ManagementHost.CreateAsync(realEngine: true);
        var source = await StaffingEvaluationTests.Create(host);
        var request = (await StaffingEvaluationTests.Request(host, source)) with { CandidateCount = 4, MultiObjective = Options(new(100, 0, 0)) };
        var initial = await StaffingEvaluationTests.Start(host, source.Scenario.Id, request);
        Assert.Empty(initial.Pareto!.FrontierRunIds); Assert.Equal("unavailable", initial.Pareto.Status);
        await StaffingEvaluationTests.Drain(host);
        var fast = await Read(host, request.CommandId); var a = fast.Pareto!;
        Assert.Equal("tradeoffs_available", a.Status); Assert.True(a.FrontierRunIds.Count >= 2); Assert.NotEmpty(a.Tradeoffs);
        Assert.True(ParetoModel.ValidResult(a.Planning.Request, a.Planning));
        foreach (var point in a.Points.Where(point => point.Feasible))
        {
            var metrics = fast.Variants.Single(variant => variant.RunId == point.RunId).Metrics!;
            Assert.Equal(metrics.MeanWaitMinutes, point.TimeMinutes); Assert.Equal(metrics.BoardingRisk, point.RiskFraction);
            var n = a.Options.Normalization; var w = a.Options.Weights;
            Assert.Equal((w.Time * (point.TimeMinutes / n.TimeMinutes) + w.Cost * (point.CostPoints / n.CostPoints)
                + w.Risk * (point.RiskFraction / n.RiskFraction)) / (w.Time + w.Cost + w.Risk), point.WeightedScore);
            Assert.DoesNotContain(a.Points, other => point.OnFrontier && ParetoAnalysisPolicy.Dominates(other, point));
        }
        Assert.All(a.Tradeoffs, tradeoff => {
            var deltas = new[] { tradeoff.TimeDeltaMinutes, tradeoff.CostDeltaPoints, tradeoff.RiskDeltaFraction };
            Assert.Contains(deltas, value => value < 0); Assert.Contains(deltas, value => value > 0);
        });
        var changed = request with { CommandId = Guid.NewGuid(), Name = "Prioritate cost", MultiObjective = Options(new(0, 100, 0)) };
        await StaffingEvaluationTests.Start(host, source.Scenario.Id, changed); await StaffingEvaluationTests.Drain(host);
        var cheap = await Read(host, changed.CommandId); var b = cheap.Pareto!;
        Assert.NotEqual(a.Planning.Solves[0].Assignments.Select(item => item.StaffCount), b.Planning.Solves[0].Assignments.Select(item => item.StaffCount));
        Assert.True(a.Planning.Solves[0].ProxyTimeMinutes < b.Planning.Solves[0].ProxyTimeMinutes);
        Assert.True(a.Planning.Solves[0].OperationalCost > b.Planning.Solves[0].OperationalCost);
        Assert.NotEqual(a.PreferredRunId, b.PreferredRunId);
        using var comparisonResponse = await host.Client.GetAsync($"/staffing-evaluations/{request.CommandId}/pareto/compare/{changed.CommandId}", Token);
        Assert.Equal(HttpStatusCode.OK, comparisonResponse.StatusCode);
        var comparison = (await comparisonResponse.Content.ReadFromJsonAsync<ParetoFrontierComparison>(Json, Token))!;
        Assert.True(comparison.Comparable); Assert.NotEmpty(comparison.UnionFrontierRunIds); Assert.NotEmpty(comparison.Comparisons);
        var differentCost = cheap with { Pareto = b with { Options = b.Options with { StaffMinuteCost = 2 } } };
        Assert.False(ParetoAnalysisPolicy.Compare(fast, differentCost).Comparable);
        using var exported = await host.Client.GetAsync($"/staffing-evaluations/{request.CommandId}/pareto/export", Token);
        Assert.Equal(HttpStatusCode.NotFound, exported.StatusCode);
        var document = (await host.Client.GetFromJsonAsync<StaffingEvaluationDetails>($"/staffing-evaluations/{request.CommandId}", Json, Token))!;
        Assert.Equal(request.MultiObjective, document.Pareto!.Options); Assert.Equal(ParetoAnalysisPolicy.Formula, document.Pareto.Formula);
        Assert.Equal(JsonSerializer.Serialize(fast, Json), JsonSerializer.Serialize(document, Json));
        var retry = await StaffingEvaluationTests.Start(host, source.Scenario.Id, request);
        Assert.Equal(fast.Variants.Select(item => item.RunId), retry.Variants.Select(item => item.RunId));
        using var conflict = await host.Client.PostAsJsonAsync($"/scenarios/{source.Scenario.Id}/staffing/evaluations", request with { MultiObjective = changed.MultiObjective }, Json, Token);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var current = (await host.Client.GetFromJsonAsync<ScenarioDetails>($"/scenarios/{source.Scenario.Id}", Json, Token))!;
        Assert.Equal(JsonSerializer.Serialize(source, Json), JsonSerializer.Serialize(current, Json));
        await using var scope = host.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<TwinDbContext>();
        Assert.Empty(await db.SimulationCommands.ToArrayAsync(Token));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Infeasible_operating_budgets_and_simulated_temporal_limits_are_excluded_from_frontiers(bool budget)
    {
        await using var host = await ManagementHost.CreateAsync(realEngine: true); var source = await StaffingEvaluationTests.Create(host);
        var options = Options(new(1, 1, 1)); options = budget ? options with { OperationalBudget = 0 } : options with { MaxMeanWaitMinutes = 0, MaxP95WaitMinutes = 0 };
        var request = (await StaffingEvaluationTests.Request(host, source)) with { MultiObjective = options };
        await StaffingEvaluationTests.Start(host, source.Scenario.Id, request); await StaffingEvaluationTests.Drain(host);
        var result = (await Read(host, request.CommandId)).Pareto!;
        Assert.Empty(result.FrontierRunIds); Assert.Null(result.PreferredRunId); Assert.Empty(result.Tradeoffs);
        Assert.Equal("no_feasible_solution", result.Status);
        Assert.All(result.Points, point => { Assert.False(point.Feasible); Assert.Null(point.WeightedScore); Assert.NotEmpty(point.Exclusions); });
        if (budget) { Assert.Empty(result.Planning.Candidates); Assert.Contains(result.Points[0].Exclusions, value => value == "operational_budget"); }
        else Assert.Contains(result.Points.SelectMany(point => point.Exclusions), value => value == "mean_wait_limit");
    }

    [Fact]
    public async Task Invalid_weights_and_references_are_rejected_before_capturing_scenarios_or_runs()
    {
        await using var host = await ManagementHost.CreateAsync(); var source = await StaffingEvaluationTests.Create(host);
        var request = await StaffingEvaluationTests.Request(host, source); var options = Options(new(0, 0, 0));
        await using var scope = host.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<TwinDbContext>();
        var runCount = await db.Runs.CountAsync(Token);
        foreach (var invalid in new[] { options, options with { Weights = new(1, 1, 1), Normalization = new(0, 1000, .1) } })
        {
            using var response = await host.Client.PostAsJsonAsync($"/scenarios/{source.Scenario.Id}/staffing/evaluations", request with { MultiObjective = invalid }, Json, Token);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        Assert.Empty(await db.Batches.ToArrayAsync(Token)); Assert.Equal(runCount, await db.Runs.CountAsync(Token)); Assert.Single(await db.Scenarios.ToArrayAsync(Token));
    }

    [Fact]
    public async Task Cancelled_evaluations_cannot_expose_partial_frontiers()
    {
        await using var host = await ManagementHost.CreateAsync(); var source = await StaffingEvaluationTests.Create(host);
        var request = (await StaffingEvaluationTests.Request(host, source)) with { MultiObjective = Options(new(1, 1, 1)) };
        await StaffingEvaluationTests.Start(host, source.Scenario.Id, request);
        using var cancelled = await host.Client.PostAsync($"/simulation-batches/{request.CommandId}/cancel", null, Token);
        Assert.Equal(HttpStatusCode.Accepted, cancelled.StatusCode); await StaffingEvaluationTests.Drain(host);
        var result = await Read(host, request.CommandId);
        Assert.Equal("cancelled", result.Batch.Batch.Status); Assert.Equal("unavailable", result.Pareto!.Status);
        Assert.Empty(result.Pareto.FrontierRunIds); Assert.Null(result.Pareto.PreferredRunId);
    }
}

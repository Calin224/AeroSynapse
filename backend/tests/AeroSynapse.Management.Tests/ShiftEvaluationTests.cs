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

// Shift mode: today, the best fixed plan and CP-SAT shift plans, all simulated by the real engine on the same population.
public sealed class ShiftEvaluationTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly ShiftRules Rules = new(4, 12, 8, 5, 2, 1, 200, 2000, 75);

    private static async Task<ScenarioDetails> LongDay(ManagementHost host)
    {
        var source = await StaffingEvaluationTests.Create(host, multipleCheckpoints: true);
        var request = JsonSerializer.Deserialize<ScenarioWriteRequest>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scenario-request.json")), Json)!;
        using var updated = await host.Client.PutAsJsonAsync($"/scenarios/{source.Scenario.Id}", request with {
            LayoutVersionId = source.Scenario.LayoutVersionId, Revision = source.Scenario.Revision,
            Inputs = source.Inputs with { DurationMinutes = 180 } }, Json, Token);
        Assert.True(updated.StatusCode == HttpStatusCode.OK, await updated.Content.ReadAsStringAsync(Token));
        return (await updated.Content.ReadFromJsonAsync<ScenarioDetails>(Json, Token))!;
    }

    [Fact]
    public async Task Shift_plans_are_simulated_beside_today_and_the_best_fixed_plan_and_approval_writes_the_staffing()
    {
        await using var host = await ManagementHost.CreateAsync(realEngine: true);
        var source = await LongDay(host);
        var request = await StaffingEvaluationTests.Request(host, source) with { Shifts = Rules };
        var started = await StaffingEvaluationTests.Start(host, source.Scenario.Id, request);
        var captured = new List<ScenarioDetails>();
        foreach (var variant in started.Variants)
            captured.Add((await host.Client.GetFromJsonAsync<ScenarioDetails>($"/scenarios/{variant.ScenarioId}", Json, Token))!);
        // Baseline, one fixed candidate without shifts, and at least one shift plan; staffing is all that differs.
        Assert.InRange(started.Variants.Count, 3, 4);
        Assert.Null(captured[0].Inputs.StaffShifts);
        Assert.Null(captured[1].Inputs.StaffShifts);
        Assert.All(captured.Skip(2), item => Assert.NotNull(item.Inputs.StaffShifts));
        Assert.All(captured, item => Assert.Equal(JsonSerializer.Serialize(source.Inputs with { StaffAllocations = [], StaffShifts = null }, Json),
            JsonSerializer.Serialize(item.Inputs with { StaffAllocations = [], StaffShifts = null }, Json)));
        Assert.All(started.Variants, item => Assert.Equal(12, item.Schedule!.Checkpoints[0].Staff.Count));
        Assert.All(started.Variants, item => Assert.All(item.Schedule!.Checkpoints, row => Assert.Equal(12, row.Arrivals!.Count)));
        var shifts = captured[2].Inputs.StaffShifts!;
        Assert.All(shifts.Shifts, shift => Assert.InRange(shift.Segments[^1].EndMinute - shift.Segments[0].StartMinute, Rules.MinShiftSlots * 15, Rules.MaxShiftSlots * 15));

        await StaffingEvaluationTests.Drain(host);
        var result = (await host.Client.GetFromJsonAsync<StaffingEvaluationDetails>($"/staffing-evaluations/{request.CommandId}", Json, Token))!;
        // Every run, including the scheduled ones, passed the .NET correlation of its KPI report.
        Assert.Equal("completed", result.Batch.Batch.Status);
        Assert.All(result.Variants, item => Assert.NotNull(item.Metrics));
        var population = (await host.Client.GetFromJsonAsync<PassengerGenerationResult>($"/runs/{result.Variants[0].RunId}/passengers?limit=100000", Json, Token))!;
        foreach (var variant in result.Variants.Skip(1))
            Assert.Equal(population.Fingerprint, (await host.Client.GetFromJsonAsync<PassengerGenerationResult>($"/runs/{variant.RunId}/passengers?limit=100000", Json, Token))!.Fingerprint);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TwinDbContext>();
            var run = result.Variants[2].RunId;
            var minutes = StaffShiftScenarios.Minutes(shifts, 180, source.Inputs.StaffAllocations.Select(item => item.CheckpointId).Distinct());
            foreach (var row in await db.Snapshots.AsNoTracking().Where(item => item.RunId == run && item.ZoneType == "checkpoint").ToArrayAsync(Token))
            {
                var kpi = JsonSerializer.Deserialize<KpiSnapshot>(row.PayloadJson, ContractJson.Options)!;
                if (minutes.TryGetValue(kpi.ZoneId, out var staffed) && kpi.Minute < 180) Assert.Equal(staffed[kpi.Minute], kpi.EffectiveCapacity);
            }
        }

        Assert.NotNull(result.RecommendedRunId);
        using var prepared = await host.Client.PostAsync($"/staffing-evaluations/{request.CommandId}/recommendation", null, Token);
        var review = (await prepared.Content.ReadFromJsonAsync<RecommendationOutcome>(Json, Token))!.Recommendation!;
        var recommended = captured[result.Variants.Single(item => item.RunId == result.RecommendedRunId).Position];
        // The explanation carries a schedule only when the recommended variant is a shift plan.
        Assert.Equal(recommended.Inputs.StaffShifts is not null, review.Explanation.Schedule is not null);
        if (review.Explanation.Schedule is { } schedule) Assert.Equal(12, schedule[0].Before.Count);
        using var approved = await host.Client.PostAsJsonAsync($"/recommendation-reviews/{review.Id}/approve",
            new RecommendationDecisionRequest(Guid.NewGuid(), "test-admin", "Plan pe ture"), Json, Token);
        Assert.True(approved.IsSuccessStatusCode, await approved.Content.ReadAsStringAsync(Token));
        var current = (await host.Client.GetFromJsonAsync<ScenarioDetails>($"/scenarios/{source.Scenario.Id}", Json, Token))!;
        var winner = captured[result.Variants.Single(item => item.RunId == result.RecommendedRunId).Position];
        Assert.Equal(source.Scenario.Revision + 1, current.Scenario.Revision);
        Assert.Equal(JsonSerializer.Serialize(winner.Inputs.StaffShifts, Json), JsonSerializer.Serialize(current.Inputs.StaffShifts, Json));
        Assert.Equal(winner.Inputs.StaffAllocations, current.Inputs.StaffAllocations);
    }

    [Fact]
    public async Task Short_scenarios_and_multi_objective_searches_cannot_plan_shifts()
    {
        await using var host = await ManagementHost.CreateAsync(realEngine: true);
        var source = await StaffingEvaluationTests.Create(host, multipleCheckpoints: true);
        var request = await StaffingEvaluationTests.Request(host, source) with { Shifts = Rules };
        using var shortDay = await host.Client.PostAsJsonAsync($"/scenarios/{source.Scenario.Id}/staffing/evaluations", request, Json, Token);
        Assert.Equal(HttpStatusCode.BadRequest, shortDay.StatusCode);
        Assert.Contains("durationMinutes", await shortDay.Content.ReadAsStringAsync(Token));
        using var weighted = await host.Client.PostAsJsonAsync($"/scenarios/{source.Scenario.Id}/staffing/evaluations",
            request with { CommandId = Guid.NewGuid(), MultiObjective = new(new(1, 1, 1), new(1, 1, 1), 1000000, 1, 60, 60, 1) }, Json, Token);
        Assert.Equal(HttpStatusCode.BadRequest, weighted.StatusCode);
    }
}

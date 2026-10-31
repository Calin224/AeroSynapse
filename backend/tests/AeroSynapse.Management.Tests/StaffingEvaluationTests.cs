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
using Xunit;

namespace AeroSynapse.Management.Tests;

public sealed class StaffingEvaluationTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static async Task<ScenarioDetails> Create(ManagementHost host, bool scheduledSurge = false, bool multipleCheckpoints = false)
    {
        var (request, layout) = await host.DraftAsync();
        var scenario = await host.PostScenarioAsync(request);
        if (multipleCheckpoints)
        {
            using var updated = await host.Client.PutAsJsonAsync($"/scenarios/{scenario.Scenario.Id}", request with {
                Revision = scenario.Scenario.Revision,
                Inputs = scenario.Inputs with {
                    Flights = [.. scenario.Inputs.Flights, scenario.Inputs.Flights[0] with {
                        Id = Guid.NewGuid(), Code = "AS-202", GateId = SyntheticScenarioSeeder.Id(202), PassengerCapacity = 4 }],
                    StaffAllocations = [.. scenario.Inputs.StaffAllocations, new(SyntheticScenarioSeeder.Id(303), SyntheticScenarioSeeder.Id(102))]
                } }, Json, Token);
            Assert.True(updated.StatusCode == HttpStatusCode.OK, await updated.Content.ReadAsStringAsync(Token));
            scenario = (await updated.Content.ReadFromJsonAsync<ScenarioDetails>(Json, Token))!;
        }
        var settings = (await host.Client.GetFromJsonAsync<PassengerSettings>($"/scenarios/{scenario.Scenario.Id}/passengers/settings", Json, Token))!;
        // Fixed lanes: each flight loads the checkpoint chosen here, so CP-SAT faces one congested control.
        var config = settings.Defaults with { Batches = settings.Defaults.Batches.Select((batch, index) => batch with {
            CheckpointMode = "fixed",
            CheckpointId = multipleCheckpoints && index == 1 ? SyntheticScenarioSeeder.Id(102) : batch.CheckpointId,
            Service = multipleCheckpoints && index == 1 ? new("constant", 20, 20, 20, 0, 20) : new("constant", 1, 1, 1, 0, 1),
            Groups = multipleCheckpoints && index == 1 ? batch.Groups.Select(group => group with { ServiceMultiplier = 6 }).ToArray() : batch.Groups,
            Surges = [new(5, 5.2, 1)] }).ToArray() };
        using var saved = await host.Client.PutAsJsonAsync($"/scenarios/{scenario.Scenario.Id}/passengers/settings",
            new PassengerConfigWriteRequest(settings.Revision, 42, config), Json, Token);
        Assert.True(saved.StatusCode == HttpStatusCode.OK, await saved.Content.ReadAsStringAsync(Token));
        scenario = (await host.Client.GetFromJsonAsync<ScenarioDetails>($"/scenarios/{scenario.Scenario.Id}", Json, Token))!;
        if (scheduledSurge)
        {
            using var added = await host.Client.PostAsJsonAsync($"/scenarios/{scenario.Scenario.Id}/events", new EventWriteRequest(scenario.Scenario.Revision,
                new(Guid.NewGuid(), "passenger_surge", scenario.Inputs.StartsAtUtc.AddMinutes(10), config.Batches[0].CheckpointId,
                    null, scenario.Inputs.Flights[0].Id, "{\"passengerCount\":5,\"windowMinutes\":1}")), Json, Token);
            Assert.True(added.StatusCode == HttpStatusCode.Created, await added.Content.ReadAsStringAsync(Token));
        }
        return (await host.Client.GetFromJsonAsync<ScenarioDetails>($"/scenarios/{scenario.Scenario.Id}", Json, Token))!;
    }
    internal static async Task<CreateStaffingEvaluationRequest> Request(ManagementHost host, ScenarioDetails source)
    {
        var settings = (await host.Client.GetFromJsonAsync<StaffingSettings>($"/scenarios/{source.Scenario.Id}/staffing/settings", Json, Token))!;
        var target = source.Inputs.StaffAllocations[0].CheckpointId;
        return new(Guid.NewGuid(), "Validare personal DEMO", new(settings.Revision, settings.AvailableStaff, 5,
            settings.Checkpoints.Select(item => new StaffingConstraint(item.Id, item.Id == target ? 1 : item.CurrentStaff,
                item.Id == target ? 4 : item.CurrentStaff, item.Id == target ? 4 : item.CurrentStaff,
                item.CurrentStaff > 0, item.CurrentStaff == 0, 10)).ToArray()), 3, new());
    }
    internal static async Task<StaffingEvaluationDetails> Start(ManagementHost host, Guid scenario, CreateStaffingEvaluationRequest request)
    {
        using var response = await host.Client.PostAsJsonAsync($"/scenarios/{scenario}/staffing/evaluations", request, Json, Token);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Token));
        Assert.Equal($"/staffing-evaluations/{request.CommandId}", response.Headers.Location!.OriginalString);
        return (await response.Content.ReadFromJsonAsync<StaffingEvaluationDetails>(Json, Token))!;
    }
    internal static async Task Drain(ManagementHost host)
    {
        for (var i = 0; i < 8; i++)
        {
            await using var scope = host.Services.CreateAsyncScope();
            if (!await scope.ServiceProvider.GetRequiredService<BatchProcessor>().ProcessNextAsync(Token)) return;
        }
        Assert.Fail("Staffing evaluation did not finish.");
    }

    [Fact]
    public async Task Real_CP_SAT_candidates_have_distinct_persisted_inputs_and_real_paired_SimPy_metrics()
    {
        await using var host = await ManagementHost.CreateAsync(realEngine: true);
        var source = await Create(host, scheduledSurge: true, multipleCheckpoints: true); var request = await Request(host, source);
        var started = await Start(host, source.Scenario.Id, request);
        Assert.Equal("complete", started.Planning.Status); Assert.Equal(4, started.Variants.Count);
        Assert.Equal(4, started.Variants.Select(item => item.RunId).Distinct().Count());
        Assert.Equal(4, started.Variants.Select(item => item.VariantId).Distinct().Count());
        Assert.Equal(4, started.Variants.Select(item => item.ScenarioId).Distinct().Count());
        Assert.All(started.Variants, item => { Assert.Null(item.Metrics); Assert.Equal("pending", item.Assessment.Status); });
        Assert.Null(started.RecommendedRunId); Assert.Equal([source.Inputs.Seed], started.Batch.Seeds);
        foreach (var variant in started.Variants)
        {
            var captured = (await host.Client.GetFromJsonAsync<ScenarioDetails>($"/scenarios/{variant.ScenarioId}", Json, Token))!;
            Assert.Equal(JsonSerializer.Serialize(source.Inputs with { StaffAllocations = [] }, Json),
                JsonSerializer.Serialize(captured.Inputs with { StaffAllocations = [] }, Json));
        }
        await Drain(host);
        var result = (await host.Client.GetFromJsonAsync<StaffingEvaluationDetails>($"/staffing-evaluations/{request.CommandId}", Json, Token))!;
        Assert.Equal("completed", result.Batch.Batch.Status);
        var baseline = result.Variants.Single(item => item.Position == 0);
        var target = source.Inputs.StaffAllocations[0].CheckpointId;
        Assert.All(result.Variants, item => { Assert.NotNull(item.Metrics); Assert.Equal(139, item.Metrics.PopulationCount); });
        Assert.NotEqual(baseline.Metrics!.Checkpoints.Max(item => item.P95WaitMinutes), baseline.Metrics.P95WaitMinutes);
        Assert.Equal("rejected", result.Variants.Single(item => item.Configuration.Single(cp => cp.CheckpointId == target).StaffCount == 1).Assessment.Status);
        var winner = result.Variants.Single(item => item.RunId == result.RecommendedRunId);
        Assert.Equal(4, winner.Configuration.Single(item => item.CheckpointId == target).StaffCount);
        Assert.Equal("improved", winner.Assessment.Status); Assert.Equal(2, winner.ReallocatedPeople); Assert.Equal(20, winner.ReallocationCost);
        var original = (await host.Client.GetFromJsonAsync<ScenarioDetails>($"/scenarios/{source.Scenario.Id}", Json, Token))!;
        Assert.Equal(JsonSerializer.Serialize(source, Json), JsonSerializer.Serialize(original, Json));
        var population = (await host.Client.GetFromJsonAsync<PassengerGenerationResult>($"/runs/{baseline.RunId}/passengers?limit=100000", Json, Token))!;
        var baselineProcess = (await host.Client.GetFromJsonAsync<ProcessResult>($"/runs/{baseline.RunId}/processes?limit=100000", Json, Token))!;
        Assert.Equal(139, baselineProcess.PopulationCount); Assert.Equal(139, baselineProcess.Passengers.Count);
        Assert.Contains(baselineProcess.Passengers, item => item.QueueWaitMinutes.HasValue && !item.SecurityClearedMinute.HasValue);
        await using var scope = host.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<TwinDbContext>();
        foreach (var variant in result.Variants)
        {
            var passengers = (await host.Client.GetFromJsonAsync<PassengerGenerationResult>($"/runs/{variant.RunId}/passengers?limit=100000", Json, Token))!;
            Assert.Equal(population.Fingerprint, passengers.Fingerprint); Assert.Equal(population.Passengers, passengers.Passengers);
            var process = (await host.Client.GetFromJsonAsync<ProcessResult>($"/runs/{variant.RunId}/processes?limit=100000", Json, Token))!;
            Assert.Equal(baselineProcess.Passengers.Select(item => (item.Id, item.ArrivalMinute, item.ServiceMinutes)),
                process.Passengers.Select(item => (item.Id, item.ArrivalMinute, item.ServiceMinutes)));
            var snapshots = await db.Snapshots.AsNoTracking().Where(item => item.RunId == variant.RunId && item.ZoneType == "checkpoint")
                .OrderByDescending(item => item.RecordedAtUtc).Take(3).ToArrayAsync(Token);
            var actual = snapshots.Select(item => JsonSerializer.Deserialize<KpiSnapshot>(item.PayloadJson, ContractJson.Options)!).ToArray();
            Assert.All(actual, item => Assert.Equal(source.Inputs.DurationMinutes, item.Minute));
            Assert.Equal(actual.Sum(item => item.ProcessedPassengers), variant.Metrics!.ProcessedCount);
            var waits = process.Passengers.Where(item => item.SecurityClearedMinute.HasValue).Select(item => item.QueueWaitMinutes!.Value).Order().ToArray();
            Assert.Equal(actual.Sum(item => item.WaitSampleCount), waits.Length);
            Assert.Equal(waits[(int)Math.Ceiling(.95 * waits.Length) - 1], variant.Metrics.P95WaitMinutes);
            Assert.Equal(actual.Sum(item => (item.MeanWaitMinutes ?? 0) * item.WaitSampleCount) / actual.Sum(item => item.WaitSampleCount), variant.Metrics.MeanWaitMinutes);
            Assert.Equal(actual.Sum(item => item.BoardingAtRiskCount) / (double)actual.Sum(item => item.BoardingRiskPopulationCount), variant.Metrics.BoardingRisk);
            if (variant.Position == 0) continue;
            var comparison = (await host.Client.GetFromJsonAsync<RunComparison>($"/runs/{baseline.RunId}/compare/{variant.RunId}", Json, Token))!;
            Assert.True(comparison.Comparability!.Comparable); Assert.True(comparison.Comparability.PopulationMatches);
        }
        var history = (await host.Client.GetFromJsonAsync<BatchSummary[]>($"/scenarios/{source.Scenario.Id}/staffing/evaluations", Json, Token))!;
        Assert.Single(history); Assert.Equal(request.CommandId, history[0].Id);
        var reread = (await host.Client.GetFromJsonAsync<StaffingEvaluationDetails>($"/staffing-evaluations/{request.CommandId}", Json, Token))!;
        Assert.Equal(JsonSerializer.Serialize(result, Json), JsonSerializer.Serialize(reread, Json));

        // Final checkpoint percentiles cannot substitute for missing individual observations.
        var runIds = result.Variants.Select(item => item.RunId).ToArray();
        await db.Snapshots.Where(item => runIds.Contains(item.RunId) && item.ZoneType == "scene").ExecuteDeleteAsync(Token);
        var withoutTraces = (await host.Client.GetFromJsonAsync<StaffingEvaluationDetails>($"/staffing-evaluations/{request.CommandId}", Json, Token))!;
        Assert.Null(withoutTraces.RecommendedRunId);
        Assert.All(withoutTraces.Variants, item => { Assert.Null(item.Metrics); Assert.Equal("unassessable", item.Assessment.Status); });
    }

    [Fact]
    public async Task Journey_runs_are_assessed_on_the_security_simulation_they_built_on()
    {
        await using var host = await ManagementHost.CreateAsync(realEngine: true);
        var source = await Create(host, multipleCheckpoints: true);
        // The journey panel's default: passengers arrive when boarding opens, with no connections.
        var journey = new ScenarioJourney(2, 1, 2, .25, 4, 10, source.Inputs.Flights.Select(flight => new ScenarioJourneyFlight(flight.Id,
            Math.Max(0, ((flight.BoardingOpensAtUtc ?? flight.DepartureAtUtc.AddMinutes(-30)) - source.Inputs.StartsAtUtc).TotalMinutes),
            false, null, 30)).ToArray(), []);
        using var changed = await host.Client.PutAsJsonAsync($"/scenarios/{source.Scenario.Id}", new ScenarioWriteRequest(source.Scenario.Code,
            source.Scenario.Name, source.Scenario.Description, source.Scenario.SceneId, source.Scenario.LayoutVersionId,
            source.Inputs with { Journey = journey }, source.Scenario.Revision), Json, Token);
        Assert.True(changed.StatusCode == HttpStatusCode.OK, await changed.Content.ReadAsStringAsync(Token));
        source = (await changed.Content.ReadFromJsonAsync<ScenarioDetails>(Json, Token))!;
        var request = await Request(host, source);
        await Start(host, source.Scenario.Id, request);
        await Drain(host);
        var result = (await host.Client.GetFromJsonAsync<StaffingEvaluationDetails>($"/staffing-evaluations/{request.CommandId}", Json, Token))!;
        Assert.Equal("completed", result.Batch.Batch.Status);
        await using var scope = host.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<TwinDbContext>();
        var runIds = result.Variants.Select(item => item.RunId).ToArray();
        var scenes = await db.Snapshots.AsNoTracking().Where(item => runIds.Contains(item.RunId) && item.ZoneType == "scene").ToArrayAsync(Token);
        Assert.All(scenes, item => Assert.Contains("\"journeys/v1\"", item.PayloadJson));
        Assert.All(result.Variants, item => Assert.True(item.Metrics is not null, string.Join(",", item.Assessment.Reasons)));
        var baseline = result.Variants.Single(item => item.Position == 0);
        Assert.Equal("baseline", baseline.Assessment.Status); Assert.True(baseline.Metrics!.PopulationCount > 0);
        Assert.All(result.Variants, item => Assert.Equal(baseline.Metrics.PopulationCount, item.Metrics!.PopulationCount));
        Assert.NotNull(result.RecommendedRunId);
    }

    [Fact]
    public async Task Concurrent_retries_reuse_captures_even_after_source_edits_and_changed_commands_conflict()
    {
        await using var host = await ManagementHost.CreateAsync(); var source = await Create(host); var request = await Request(host, source);
        var starts = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Start(host, source.Scenario.Id, request)));
        Assert.All(starts, item => Assert.Equal(starts[0].Variants.Select(value => value.RunId), item.Variants.Select(value => value.RunId)));
        using var changed = await host.Client.PutAsJsonAsync($"/scenarios/{source.Scenario.Id}", new ScenarioWriteRequest(source.Scenario.Code,
            source.Scenario.Name, source.Scenario.Description, source.Scenario.SceneId, source.Scenario.LayoutVersionId,
            source.Inputs with { Seed = 99 }, source.Scenario.Revision), Json, Token);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var repeated = await Start(host, source.Scenario.Id, request);
        Assert.Equal(starts[0].Variants.Select(value => value.RunId), repeated.Variants.Select(value => value.RunId));
        Assert.Equal(42, repeated.Batch.Seeds[0]);
        using var conflict = await host.Client.PostAsJsonAsync($"/scenarios/{source.Scenario.Id}/staffing/evaluations",
            request with { Policy = request.Policy with { MaxP95IncreaseMinutes = 1 } }, Json, Token);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task No_candidates_are_fabricated_for_exhausted_or_expired_searches(bool timeout)
    {
        await using var host = await ManagementHost.CreateAsync(); var source = await Create(host); var request = await Request(host, source);
        request = request with { Proposal = timeout ? request.Proposal with { TimeLimitSeconds = 0 } : request.Proposal with { AvailableStaff = 0 } };
        var result = await Start(host, source.Scenario.Id, request);
        Assert.Equal(timeout ? "timeout" : "exhausted", result.Planning.Status);
        Assert.Empty(result.Planning.Candidates); Assert.Single(result.Variants); Assert.Null(result.RecommendedRunId);
        Assert.All(result.Variants, item => Assert.Null(item.Metrics));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_and_failed_simulations_cannot_be_recommended_or_display_zero_metrics(bool cancellation)
    {
        await using var host = await ManagementHost.CreateAsync(); var source = await Create(host); var request = await Request(host, source);
        await Start(host, source.Scenario.Id, request);
        if (cancellation)
        {
            using var response = await host.Client.PostAsync($"/simulation-batches/{request.CommandId}/cancel", null, Token);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }
        else host.Engine.Behavior = (input, _) => Task.FromResult(new SimulationResponse("1.0", input.RunId, "failed", 0, [], [], new("demo_failure", "Deliberate simulator failure.")));
        await Drain(host);
        var result = (await host.Client.GetFromJsonAsync<StaffingEvaluationDetails>($"/staffing-evaluations/{request.CommandId}", Json, Token))!;
        Assert.Equal(cancellation ? "cancelled" : "completed_with_failures", result.Batch.Batch.Status);
        Assert.Null(result.RecommendedRunId); Assert.All(result.Variants, item => { Assert.Null(item.Metrics); Assert.Equal("failed", item.Assessment.Status); });
    }

    [Theory]
    [InlineData("revision", 409)]
    [InlineData("constraints", 400)]
    [InlineData("policy", 400)]
    [InlineData("candidates", 400)]
    public async Task Invalid_evaluations_leave_no_candidate_scenarios_or_batch(string kind, int status)
    {
        await using var host = await ManagementHost.CreateAsync(); var source = await Create(host); var request = await Request(host, source);
        request = kind switch { "revision" => request with { Proposal = request.Proposal with { Revision = source.Scenario.Revision + 1 } },
            "constraints" => request with { Proposal = request.Proposal with { Checkpoints = request.Proposal.Checkpoints.Skip(1).ToArray() } },
            "policy" => request with { Policy = request.Policy with { MaxBoardingRiskIncrease = -1 } },
            _ => request with { CandidateCount = 5 } };
        using var response = await host.Client.PostAsJsonAsync($"/scenarios/{source.Scenario.Id}/staffing/evaluations", request, Json, Token);
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.Single((await host.Client.GetFromJsonAsync<ScenarioSummary[]>("/scenarios", Json, Token))!);
        Assert.Empty((await host.Client.GetFromJsonAsync<BatchSummary[]>("/simulation-batches", Json, Token))!);
    }
}

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

public sealed class WhatIfTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static async Task<ScenarioDetails> Create(ManagementHost host, bool profiles = false, int passengers = 120)
    {
        using var published = await host.Client.PostAsync($"/scenes/{SyntheticScenarioSeeder.SceneId}/layout-versions", null, Token);
        var layout = (await published.Content.ReadFromJsonAsync<PublishedLayout>(Json, Token))!;
        var request = JsonSerializer.Deserialize<ScenarioWriteRequest>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scenario-request.json")), Json)!;
        request = request with { LayoutVersionId = layout.Version.Id, Inputs = request.Inputs with {
            Flights = [request.Inputs.Flights[0] with { PassengerCapacity = passengers + 10 }],
            Populations = [request.Inputs.Populations[0] with { Count = passengers }], Flows = [request.Inputs.Flows[0] with { PassengerCount = passengers }] } };
        if (profiles)
        {
            var generation = PassengerScenarios.Defaults(request.Inputs, layout.Layout);
            request = request with { Inputs = request.Inputs with { PassengerGeneration = generation with { Batches = [generation.Batches[0] with { CountMode = "subset", ModeledCount = passengers }] },
                Events = [.. request.Inputs.Events, new(Guid.NewGuid(), IncidentScenarios.PassengerSurge, request.Inputs.StartsAtUtc.AddMinutes(5),
                    generation.Batches[0].CheckpointId, request.Inputs.Flights[0].GateId, request.Inputs.Flights[0].Id,
                    "{\"contractVersion\":\"1.0\",\"durationMinutes\":5,\"share\":0.75,\"isSynthetic\":true}")] } };
        }
        using var response = await host.Client.PostAsJsonAsync("/scenarios", request, Json, Token);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Token));
        return (await response.Content.ReadFromJsonAsync<ScenarioDetails>(Json, Token))!;
    }
    private static CreateWhatIfRequest Request(ScenarioDetails scenario) => new(Guid.NewGuid(), scenario.Scenario.Revision, "Personal alternativ DEMO", scenario.Inputs.StaffAllocations[0].CheckpointId, 1);
    private static async Task<BatchDetails> Pair(ManagementHost host, Guid scenarioId, CreateWhatIfRequest request)
    {
        using var response = await host.Client.PostAsJsonAsync($"/scenarios/{scenarioId}/what-if", request, Json, Token);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Token));
        Assert.Equal($"/simulation-batches/{request.CommandId}", response.Headers.Location!.OriginalString);
        return (await response.Content.ReadFromJsonAsync<BatchDetails>(Json, Token))!;
    }
    private static async Task Drain(ManagementHost host)
    {
        for (var i = 0; i < 5; i++)
        {
            await using var scope = host.Services.CreateAsyncScope();
            if (!await scope.ServiceProvider.GetRequiredService<BatchProcessor>().ProcessNextAsync(Token)) return;
        }
        Assert.Fail("Pair did not finish.");
    }

    [Fact]
    public async Task Real_engine_pair_preserves_the_exact_population_incidents_and_source_and_is_recoverable_after_edits()
    {
        await using var host = await ManagementHost.CreateAsync(realEngine: true); var source = await Create(host, profiles: true);
        var request = Request(source); var pair = await Pair(host, source.Scenario.Id, request);
        Assert.Equal([source.Inputs.Seed], pair.Seeds); Assert.Equal(2, pair.Replications.Count);
        var baseline = pair.Replications.Single(r => r.VariantId == pair.Variants.Single(v => v.Position == 0).Id);
        var candidate = pair.Replications.Single(r => r.VariantId == pair.Variants.Single(v => v.Position == 1).Id);
        Assert.NotEqual(baseline.RunId, candidate.RunId);
        var copyId = pair.Variants.Single(v => v.Position == 1).ScenarioId; Assert.NotEqual(source.Scenario.Id, copyId);
        var copy = (await host.Client.GetFromJsonAsync<ScenarioDetails>($"/scenarios/{copyId}", Json, Token))!;
        Assert.Equal(1, copy.Scenario.Revision); Assert.Single(copy.Inputs.StaffAllocations, a => a.CheckpointId == request.CheckpointId);
        Assert.Equal(JsonSerializer.Serialize(source.Inputs with { StaffAllocations = [] }, Json), JsonSerializer.Serialize(copy.Inputs with { StaffAllocations = [] }, Json));
        var untouched = (await host.Client.GetFromJsonAsync<ScenarioDetails>($"/scenarios/{source.Scenario.Id}", Json, Token))!;
        Assert.Equal(source.Scenario.Revision, untouched.Scenario.Revision); Assert.Equal(source.Scenario.Name, untouched.Scenario.Name);
        Assert.Equal(JsonSerializer.Serialize(source.Inputs, Json), JsonSerializer.Serialize(untouched.Inputs, Json));
        // Pending runs already own their inputs; editing the original cannot change their population.
        using var edit = await host.Client.PutAsJsonAsync($"/scenarios/{source.Scenario.Id}", new ScenarioWriteRequest(source.Scenario.Code,
            "Schimbat ulterior", source.Scenario.Description, source.Scenario.SceneId, source.Scenario.LayoutVersionId, source.Inputs with { Seed = 123 }, 1), Json, Token);
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var retries = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Pair(host, source.Scenario.Id, request)));
        Assert.All(retries, retry => Assert.Equal(pair.Replications.Select(r => r.RunId), retry.Replications.Select(r => r.RunId)));
        using var scope = host.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<TwinDbContext>();
        Assert.Equal(2, await db.Runs.CountAsync(r => r.ScenarioId == source.Scenario.Id || r.ScenarioId == copyId, Token));
        await Drain(host);
        var a = (await host.Client.GetFromJsonAsync<PassengerGenerationResult>($"/runs/{baseline.RunId}/passengers?limit=100000", Json, Token))!;
        var b = (await host.Client.GetFromJsonAsync<PassengerGenerationResult>($"/runs/{candidate.RunId}/passengers?limit=100000", Json, Token))!;
        Assert.Equal(120, a.ModeledCount); Assert.Equal(a.Fingerprint, b.Fingerprint); Assert.Equal(a.Passengers, b.Passengers);
        var comparison = (await host.Client.GetFromJsonAsync<RunComparison>($"/runs/{baseline.RunId}/compare/{candidate.RunId}", Json, Token))!;
        Assert.True(comparison.Comparability!.Comparable); Assert.True(comparison.Comparability.PopulationMatches);
        Assert.False(comparison.InputsMatch); Assert.True(comparison.VersionsMatch);
        Assert.Equal(baseline.RunId, comparison.Baseline.Id); Assert.Equal(candidate.RunId, comparison.Candidate.Id);
        Assert.NotEmpty(comparison.Metrics);
        foreach (var metric in comparison.Metrics.Where(m => m.Baseline.HasValue && m.Candidate.HasValue))
        {
            Assert.Equal(metric.Candidate - metric.Baseline, metric.Delta);
            if (metric.Baseline > 0) Assert.Equal(metric.Delta / metric.Baseline * 100, metric.PercentChange);
            else { Assert.Null(metric.PercentChange); Assert.Equal("baseline_not_positive", metric.UnavailableReason); }
        }
        using var conflict = await host.Client.PostAsJsonAsync($"/scenarios/{source.Scenario.Id}/what-if", request with { StaffCount = 0 }, Json, Token);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task A_check_in_pair_reopens_the_desks_with_a_matching_team_and_keeps_security()
    {
        await using var host = await ManagementHost.CreateAsync(); var created = await Create(host);
        var team = new ScenarioOperator[] { new(Guid.NewGuid(), "Ana", "checkIn"), new(Guid.NewGuid(), "Bogdan", "checkIn") };
        using var edit = await host.Client.PutAsJsonAsync($"/scenarios/{created.Scenario.Id}", new ScenarioWriteRequest(created.Scenario.Code,
            created.Scenario.Name, created.Scenario.Description, created.Scenario.SceneId, created.Scenario.LayoutVersionId,
            created.Inputs with { CheckIn = new(SyntheticScenarioSeeder.CheckInAreaId, 2, .4m), Operators = team }, 1), Json, Token);
        var source = (await edit.Content.ReadFromJsonAsync<ScenarioDetails>(Json, Token))!;
        var request = Request(source) with { CheckpointId = SyntheticScenarioSeeder.CheckInAreaId, StaffCount = 4 };
        var copyId = (await Pair(host, source.Scenario.Id, request)).Variants.Single(v => v.Position == 1).ScenarioId;
        var copy = (await host.Client.GetFromJsonAsync<ScenarioDetails>($"/scenarios/{copyId}", Json, Token))!;
        Assert.Equal(4, copy.Inputs.CheckIn!.StaffCount);
        Assert.Equal(["Ana", "Bogdan", "Operator check-in 3", "Operator check-in 4"], copy.Inputs.Operators!.Select(person => person.Name));
        Assert.Equal(source.Inputs.StaffAllocations, copy.Inputs.StaffAllocations);
        using var tooMany = await host.Client.PostAsJsonAsync($"/scenarios/{source.Scenario.Id}/what-if",
            request with { CommandId = Guid.NewGuid(), StaffCount = 13 }, Json, Token);
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
    }

    [Fact]
    public async Task A_control_pair_adds_only_unassigned_operators_of_the_scenario_team()
    {
        await using var host = await ManagementHost.CreateAsync(); var created = await Create(host);
        var allocated = created.Inputs.StaffAllocations.Select(a => a.StaffMemberId).ToHashSet();
        var spare = Enumerable.Range(301, SyntheticScenarioSeeder.StaffSlots).Select(SyntheticScenarioSeeder.Id).First(id => !allocated.Contains(id));
        var team = allocated.Append(spare).Select(id => new ScenarioOperator(id, $"Op {id}", "security")).ToArray();
        using var edit = await host.Client.PutAsJsonAsync($"/scenarios/{created.Scenario.Id}", new ScenarioWriteRequest(created.Scenario.Code,
            created.Scenario.Name, created.Scenario.Description, created.Scenario.SceneId, created.Scenario.LayoutVersionId,
            created.Inputs with { Operators = team }, 1), Json, Token);
        Assert.True(edit.IsSuccessStatusCode, await edit.Content.ReadAsStringAsync(Token));
        var source = (await edit.Content.ReadFromJsonAsync<ScenarioDetails>(Json, Token))!;
        var checkpoint = source.Inputs.StaffAllocations[0].CheckpointId;
        var current = source.Inputs.StaffAllocations.Count(a => a.CheckpointId == checkpoint);
        var request = Request(source) with { StaffCount = current + 1 };
        var copyId = (await Pair(host, source.Scenario.Id, request)).Variants.Single(v => v.Position == 1).ScenarioId;
        var copy = (await host.Client.GetFromJsonAsync<ScenarioDetails>($"/scenarios/{copyId}", Json, Token))!;
        Assert.Contains(new StaffAllocation(spare, checkpoint), copy.Inputs.StaffAllocations);
        using var tooMany = await host.Client.PostAsJsonAsync($"/scenarios/{source.Scenario.Id}/what-if",
            request with { CommandId = Guid.NewGuid(), StaffCount = current + 2 }, Json, Token);
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
    }

    [Theory]
    [InlineData("negative", 400)][InlineData("unavailable", 400)][InlineData("checkpoint", 400)][InlineData("revision", 409)]
    [InlineData("command", 400)][InlineData("name", 400)][InlineData("timeout", 400)][InlineData("engine-limit", 400)]
    public async Task Invalid_pairs_never_leave_a_copy_or_runs_and_do_not_consume_staff_from_other_checkpoints(string kind, int status)
    {
        await using var host = await ManagementHost.CreateAsync(); var source = await Create(host, passengers: kind == "engine-limit" ? 100001 : 120);
        var request = Request(source);
        request = kind switch { "negative" => request with { StaffCount = -1 }, "unavailable" => request with { StaffCount = 999 },
            "checkpoint" => request with { CheckpointId = Guid.NewGuid() }, "revision" => request with { Revision = 2 },
            "command" => request with { CommandId = Guid.Empty }, "name" => request with { Name = "" }, "timeout" => request with { TimeLimitSeconds = 0 }, _ => request };
        using var reply = await host.Client.PostAsJsonAsync($"/scenarios/{source.Scenario.Id}/what-if", request, Json, Token);
        Assert.Equal((HttpStatusCode)status, reply.StatusCode); Assert.Contains("code", await reply.Content.ReadAsStringAsync(Token));
        Assert.Single((await host.Client.GetFromJsonAsync<ScenarioSummary[]>("/scenarios", Json, Token))!);
        Assert.Empty((await host.Client.GetFromJsonAsync<BatchSummary[]>("/simulation-batches", Json, Token))!);
        Assert.Empty((await host.Client.GetFromJsonAsync<SimulationRun[]>($"/scenarios/{source.Scenario.Id}/runs", Json, Token))!);
        Assert.Null(host.Engine.LastRequest);
    }

    [Fact]
    public async Task Zero_staff_is_an_explicit_variant_and_a_failed_candidate_is_not_compared_as_zero()
    {
        await using var host = await ManagementHost.CreateAsync(); var source = await Create(host);
        var pair = await Pair(host, source.Scenario.Id, Request(source) with { StaffCount = 0 });
        var copyId = pair.Variants.Single(v => v.Position == 1).ScenarioId;
        host.Engine.Behavior = (request, _) => Task.FromResult(request.ScenarioId == copyId
            ? new SimulationResponse("1.0", request.RunId, "failed", 0, [], [], new("demo_failure", "Candidate failed deliberately.")) : RecordingEngine.Response(request));
        await Drain(host);
        var baselineId = pair.Replications.Single(r => r.VariantId == pair.Variants.Single(v => v.Position == 0).Id).RunId;
        var candidateId = pair.Replications.Single(r => r.VariantId == pair.Variants.Single(v => v.Position == 1).Id).RunId;
        var comparison = (await host.Client.GetFromJsonAsync<RunComparison>($"/runs/{baselineId}/compare/{candidateId}", Json, Token))!;
        Assert.False(comparison.Comparability!.Comparable); Assert.False(comparison.Candidate.ResultsValid);
        Assert.Contains("demo_failure", comparison.Candidate.FailureReason!);
        Assert.Contains(comparison.Comparability.Issues, issue => issue.Code == "results_unavailable");
        Assert.All(comparison.Metrics, row => { Assert.Null(row.Candidate); Assert.Null(row.Delta); Assert.Null(row.PercentChange); });
    }

    [Fact]
    public async Task Compatibility_checks_captured_population_layout_horizon_seed_build_and_independent_identity()
    {
        await using var host = await ManagementHost.CreateAsync(); var source = await Create(host);
        var pair = await Pair(host, source.Scenario.Id, Request(source));
        await using var scope = host.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<TwinDbContext>();
        var ids = pair.Replications.Select(r => r.RunId).ToArray();
        var runs = await db.Runs.AsNoTracking().Where(r => ids.Contains(r.Id)).ToArrayAsync(Token);
        var a = runs[0]; var b = runs[1];
        a.Status = b.Status = "completed"; a.EngineBuild = b.EngineBuild = new string('c', 64);
        Assert.True(RunComparisonPolicy.Assess(a, b).Comparable);
        var original = b.InputSnapshotJson!; var snapshot = JsonSerializer.Deserialize<RunInputSnapshot>(original, Json)!;
        var edits = new (string Code, RunInputSnapshot Value)[] {
            ("inputs_unavailable", snapshot with { SchemaVersion = 2 }),
            ("inputs_unavailable", snapshot with { Layout = snapshot.Layout with { Layout = snapshot.Layout.Layout with { Scene = null! } } }),
            ("inputs_unavailable", snapshot with { Scenario = snapshot.Scenario with { Inputs = snapshot.Scenario.Inputs with { Events = null! } } }),
            ("seed_mismatch", snapshot with { Scenario = snapshot.Scenario with { Inputs = snapshot.Scenario.Inputs with { Seed = 43 } } }),
            ("observation_mismatch", snapshot with { SnapshotIntervalMinutes = 2 }),
            ("observation_mismatch", snapshot with { Scenario = snapshot.Scenario with { Inputs = snapshot.Scenario.Inputs with { DurationMinutes = 61 } } }),
            ("layout_mismatch", snapshot with { Layout = snapshot.Layout with { Version = snapshot.Layout.Version with { Id = Guid.NewGuid() } } }),
            ("population_mismatch", snapshot with { Scenario = snapshot.Scenario with { Inputs = snapshot.Scenario.Inputs with {
                Populations = [snapshot.Scenario.Inputs.Populations[0] with { Count = 119 }],
                Flows = [snapshot.Scenario.Inputs.Flows[0] with { PassengerCount = 119 }] } } })
        };
        foreach (var edit in edits)
        {
            b.InputSnapshotJson = JsonSerializer.Serialize(edit.Value, Json);
            var blocked = RunComparisonPolicy.Assess(a, b); Assert.False(blocked.Comparable);
            Assert.Contains(blocked.Issues, issue => issue.Code == edit.Code);
        }
        b.InputSnapshotJson = original; b.EngineBuild = new string('d', 64);
        Assert.Contains(RunComparisonPolicy.Assess(a, b).Issues, issue => issue.Code == "engine_mismatch");
        b.EngineBuild = null; Assert.Contains(RunComparisonPolicy.Assess(a, b).Issues, issue => issue.Code == "engine_unknown");
        b.EngineBuild = a.EngineBuild; b.InputSnapshotJson = null;
        Assert.Null(RunComparisonPolicy.Assess(a, b).PopulationMatches);
        Assert.Contains(RunComparisonPolicy.Assess(a, b).Issues, issue => issue.Code == "inputs_unavailable");
        b.InputSnapshotJson = original; b.Id = a.Id;
        Assert.Contains(RunComparisonPolicy.Assess(a, b).Issues, issue => issue.Code == "same_run");
    }

    [Fact]
    public async Task A_candidate_cannot_steal_people_already_allocated_to_another_checkpoint()
    {
        await using var host = await ManagementHost.CreateAsync(); var source = await Create(host);
        var layout = (await host.Client.GetFromJsonAsync<PublishedLayout>($"/scenes/{source.Scenario.SceneId}/layout-versions/{source.Scenario.LayoutVersionId}", Json, Token))!;
        var cp = source.Inputs.StaffAllocations[0].CheckpointId;
        var other = layout.Layout.Checkpoints.First(c => c.Id != cp);
        var allocations = layout.Layout.Staff.Select((member, index) => new StaffAllocation(member.Id, index < 2 ? cp : other.Id)).ToArray();
        using var edit = await host.Client.PutAsJsonAsync($"/scenarios/{source.Scenario.Id}", new ScenarioWriteRequest(source.Scenario.Code,
            source.Scenario.Name, source.Scenario.Description, source.Scenario.SceneId, source.Scenario.LayoutVersionId,
            source.Inputs with { StaffAllocations = allocations }, 1), Json, Token);
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        using var blocked = await host.Client.PostAsJsonAsync($"/scenarios/{source.Scenario.Id}/what-if", Request(source) with { Revision = 2, StaffCount = 3 }, Json, Token);
        Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
        Assert.Contains("staffCount", await blocked.Content.ReadAsStringAsync(Token));
        Assert.Single((await host.Client.GetFromJsonAsync<ScenarioSummary[]>("/scenarios", Json, Token))!);
    }
}

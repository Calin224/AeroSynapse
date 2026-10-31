using System.Text.Json;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using AeroSynapse.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace AeroSynapse.Infrastructure.Tests;

public sealed class RunStorageTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly DateTime Start = new(2026, 10, 11, 10, 0, 0, DateTimeKind.Utc);
    private static Guid Cp1 => SyntheticScenarioSeeder.Id(101);
    private static Guid Cp2 => SyntheticScenarioSeeder.Id(102);

    [Fact]
    public async Task Metrics_and_output_snapshots_persist_with_run_time_and_zone_filters()
    {
        await using var test = await TestDatabase.CreateAsync();
        var (_, run) = await Prepare(test);
        var service = new RunStorageService(test.Context);
        await service.TransitionAsync(run.Id, new("running", 1), Token);
        for (var minute = 1; minute <= 3; minute++)
        {
            var zone = minute == 2 ? Cp2 : Cp1;
            await service.AddMetricAsync(run.Id, new(Start.AddMinutes(minute), "checkpoint", zone, "queue_length", minute * 10, "people"), Token);
            await service.AddSnapshotAsync(run.Id, new(Start.AddMinutes(minute), "checkpoint", zone, $"{{\"queueLength\":{minute * 10}}}"), Token);
        }
        Assert.False((await service.MetricsAsync(run.Id, new(), Token)).IsValid);
        Assert.Empty((await service.MetricsAsync(run.Id, new(), Token)).Items);
        var completed = await service.TransitionAsync(run.Id, new("completed", 2), Token);
        Assert.True(completed.ResultsValid);
        Assert.NotNull(completed.LaunchedAtUtc);
        Assert.NotNull(completed.FinishedAtUtc);
        await using var fresh = test.NewContext();
        var reopened = new RunStorageService(fresh);
        var query = new RunResultQuery(Start.AddMinutes(2), Start.AddMinutes(3), "checkpoint", Cp1);
        var metrics = await reopened.MetricsAsync(run.Id, query, Token);
        Assert.True(metrics.IsValid);
        Assert.Equal(30, Assert.Single(metrics.Items).Value);
        Assert.Equal(run.Id, metrics.Items[0].RunId);
        var snapshots = await reopened.SnapshotsAsync(run.Id, query, Token);
        Assert.Equal(30, JsonDocument.Parse(Assert.Single(snapshots.Items).PayloadJson).RootElement.GetProperty("queueLength").GetInt32());
        var firstTwo = await reopened.MetricsAsync(run.Id, new(Limit: 2), Token);
        Assert.Equal(2, firstTwo.Items.Count);
        Assert.True(firstTwo.Items[0].RecordedAtUtc < firstTwo.Items[1].RecordedAtUtc);
        Assert.Empty((await reopened.SnapshotsAsync(run.Id, new(ZoneType: "checkpoint", ZoneId: Guid.NewGuid()), Token)).Items);
        await Assert.ThrowsAsync<ScenarioNotFoundException>(() => reopened.MetricsAsync(Guid.NewGuid(), new(), Token));
        await Assert.ThrowsAsync<ScenarioConflictException>(() => reopened.AddMetricAsync(run.Id, Metric(), Token));
    }

    [Fact]
    public async Task Failed_runs_preserve_reason_and_partial_results_are_never_valid()
    {
        await using var test = await TestDatabase.CreateAsync();
        var (_, run) = await Prepare(test);
        var service = new RunStorageService(test.Context);
        await Assert.ThrowsAsync<ScenarioConflictException>(() => service.AddMetricAsync(run.Id, Metric(), Token));
        await service.TransitionAsync(run.Id, new("running", 1), Token);
        await service.AddMetricAsync(run.Id, Metric(), Token);
        await service.AddSnapshotAsync(run.Id, new(Start.AddMinutes(1), "checkpoint", Cp1, "{\"queueLength\":10}"), Token);
        var invalid = await Assert.ThrowsAsync<ScenarioValidationException>(() => service.TransitionAsync(run.Id, new("failed", 2, " "), Token));
        Assert.Contains("failureReason", invalid.Errors.Keys);
        var nulReason = await Assert.ThrowsAsync<ScenarioValidationException>(() => service.TransitionAsync(run.Id, new("failed", 2, "Engine\0timeout"), Token));
        Assert.Contains("failureReason", nulReason.Errors.Keys);
        var unchanged = (await service.GetAsync(run.Id, Token))!;
        Assert.Equal("running", unchanged.Status);
        Assert.Equal(2, unchanged.Revision);
        Assert.Null(unchanged.FailureReason);
        await service.TransitionAsync(run.Id, new("failed", 2, "Engine timeout"), Token);
        await using var fresh = test.NewContext();
        var reopened = new RunStorageService(fresh);
        var failed = Assert.IsType<SimulationRun>(await reopened.GetAsync(run.Id, Token));
        Assert.False(failed.ResultsValid);
        Assert.Equal("Engine timeout", failed.FailureReason);
        Assert.NotNull(failed.FinishedAtUtc);
        var normal = await reopened.MetricsAsync(run.Id, new(), Token);
        Assert.False(normal.IsValid);
        Assert.Equal("Engine timeout", normal.FailureReason);
        Assert.Empty(normal.Items);
        var diagnostic = await reopened.MetricsAsync(run.Id, new(IncludeInvalid: true), Token);
        Assert.False(diagnostic.IsValid);
        Assert.Single(diagnostic.Items);
        Assert.Empty((await reopened.SnapshotsAsync(run.Id, new(), Token)).Items);
        Assert.Single((await reopened.SnapshotsAsync(run.Id, new(IncludeInvalid: true), Token)).Items);
        await Assert.ThrowsAsync<ScenarioConflictException>(() => reopened.TransitionAsync(run.Id, new("completed", failed.Revision), Token));
        await Assert.ThrowsAsync<ScenarioConflictException>(() => reopened.AddSnapshotAsync(run.Id, new(Start, "checkpoint", Cp1, "{}"), Token));
    }

    [Theory]
    [InlineData("zone", "zoneId")]
    [InlineData("zone-type", "zoneId")]
    [InlineData("time", "recordedAtUtc")]
    [InlineData("utc", "recordedAtUtc")]
    [InlineData("name", "name")]
    [InlineData("precision", "value")]
    [InlineData("range", "value")]
    [InlineData("snapshot-json", "payloadJson")]
    [InlineData("nul-name", "name")]
    [InlineData("nul-unit", "unit")]
    [InlineData("snapshot-nul-value", "payloadJson")]
    [InlineData("snapshot-nul-key", "payloadJson")]
    [InlineData("snapshot-nul-nested", "payloadJson")]
    [InlineData("snapshot-surrogate-value", "payloadJson")]
    [InlineData("snapshot-surrogate-key", "payloadJson")]
    [InlineData("snapshot-number-range", "payloadJson")]
    public async Task Invalid_samples_are_rejected_without_storing_results(string invalid, string field)
    {
        await using var test = await TestDatabase.CreateAsync();
        var (_, run) = await Prepare(test);
        var service = new RunStorageService(test.Context);
        await service.TransitionAsync(run.Id, new("running", 1), Token);
        var metric = invalid switch
        {
            "zone" => Metric() with { ZoneId = Guid.NewGuid() },
            "zone-type" => Metric() with { ZoneType = "unknown" },
            "time" => Metric() with { RecordedAtUtc = Start.AddMinutes(61) },
            "utc" => Metric() with { RecordedAtUtc = DateTime.SpecifyKind(Start, DateTimeKind.Unspecified) },
            "name" => Metric() with { Name = " " },
            "nul-name" => Metric() with { Name = "queue\0length" },
            "nul-unit" => Metric() with { Unit = "peo\0ple" },
            "precision" => Metric() with { Value = 0.0000001m },
            "range" => Metric() with { Value = 1000000000000m },
            _ => Metric()
        };
        var payload = invalid switch
        {
            "snapshot-nul-value" => """{"text":"\u0000"}""",
            "snapshot-nul-key" => """{"\u0000":1}""",
            "snapshot-nul-nested" => """{"nested":[{"text":"\u0000"}]}""",
            "snapshot-surrogate-value" => """{"text":"\ud800"}""",
            "snapshot-surrogate-key" => """{"\udc00":1}""",
            "snapshot-number-range" => """{"value":1e1000000000}""",
            _ => "[]"
        };
        var error = invalid.StartsWith("snapshot-", StringComparison.Ordinal)
            ? await Assert.ThrowsAsync<ScenarioValidationException>(() => service.AddSnapshotAsync(run.Id, new(Start, "checkpoint", Cp1, payload), Token))
            : await Assert.ThrowsAsync<ScenarioValidationException>(() => service.AddMetricAsync(run.Id, metric, Token));
        Assert.Contains(field, error.Errors.Keys);
        Assert.Equal(0, await test.Context.Metrics.CountAsync(Token));
        Assert.Equal(0, await test.Context.Snapshots.CountAsync(Token));
    }

    [Fact]
    public async Task Replay_carries_the_check_in_desks_beside_the_controls()
    {
        await using var test = await TestDatabase.CreateAsync();
        var (_, run) = await Prepare(test);
        var service = new RunStorageService(test.Context);
        await service.TransitionAsync(run.Id, new("running", 1), Token);
        var desks = SyntheticScenarioSeeder.CheckInAreaId;
        var desk = new AeroSynapse.Application.Contracts.CheckInSnapshot(desks, "checkIn", 0, 5, 3, 2, 0, 0, 3, null, null, null, 2, 0, null);
        await service.AddSnapshotAsync(run.Id, new(Start, "checkIn", desks,
            JsonSerializer.Serialize(desk, AeroSynapse.Application.Contracts.ContractJson.Options)), Token);
        await service.AddSnapshotAsync(run.Id, new(Start, "checkpoint", Cp1,
            $$"""{"contractVersion":"1.0","zoneId":"{{Cp1}}","elapsedMinutes":0,"queuedPassengers":4,"processedPassengers":0}"""), Token);
        await service.TransitionAsync(run.Id, new("completed", 2), Token);
        var replay = await new AeroSynapse.Infrastructure.Execution.ReplayService(test.NewContext()).GetAsync(run.Id, Token);
        using var document = JsonDocument.Parse(replay.PayloadJson);
        var samples = document.RootElement.GetProperty("samples").EnumerateArray().ToArray();
        var atDesks = samples.Single(sample => sample.GetProperty("zoneId").GetGuid() == desks);
        Assert.Equal(3, atDesks.GetProperty("queuedPassengers").GetInt32());
        Assert.Equal("checkIn", atDesks.GetProperty("checkIn").GetProperty("zoneType").GetString());
        // Control samples are unchanged, so replays of runs without desks keep their hash.
        Assert.False(samples.Single(sample => sample.GetProperty("zoneId").GetGuid() == Cp1).TryGetProperty("checkIn", out _));
    }

    [Fact]
    public async Task Check_in_results_belong_to_a_check_in_area_of_the_captured_layout()
    {
        await using var test = await TestDatabase.CreateAsync();
        var (scenario, run) = await Prepare(test);
        var layout = await new ScenarioService(test.Context).GetLayoutAsync(scenario.Scenario.SceneId, scenario.Scenario.LayoutVersionId, Token);
        Assert.Equal(SyntheticScenarioSeeder.CheckInAreaId, Assert.Single(layout!.Layout.CheckInAreas!).Id);
        var service = new RunStorageService(test.Context);
        await service.TransitionAsync(run.Id, new("running", 1), Token);
        await service.AddSnapshotAsync(run.Id, new(Start, "checkIn", SyntheticScenarioSeeder.CheckInAreaId, """{"queuedPassengers":3}"""), Token);
        // A control is not a check-in area, in the API validator and in the database trigger alike.
        var error = await Assert.ThrowsAsync<ScenarioValidationException>(() => service.AddSnapshotAsync(run.Id, new(Start.AddMinutes(1), "checkIn", Cp1, "{}"), Token));
        Assert.Contains("zoneId", error.Errors.Keys);
        test.Context.Snapshots.Add(new RunSnapshot { Id = Guid.NewGuid(), SceneId = run.SceneId, RunId = run.Id, IsSynthetic = true,
            RecordedAtUtc = Start.AddMinutes(2), ZoneType = "checkIn", ZoneId = Cp1, PayloadJson = "{}" });
        await Assert.ThrowsAsync<DbUpdateException>(() => test.Context.SaveChangesAsync(Token));
        test.Context.ChangeTracker.Clear();
        Assert.Equal("checkIn", (await test.Context.Snapshots.SingleAsync(x => x.RunId == run.Id, Token)).ZoneType);
        // Check-in rows stay out of the controls' live queue stream.
        Assert.Equal(0, await test.Context.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM twin.run_progress WHERE \"RunId\" = {run.Id}").SingleAsync(Token));
    }

    [Fact]
    public async Task Snapshot_preserves_valid_unicode_literal_escape_text_and_large_supported_json_numbers()
    {
        await using var test = await TestDatabase.CreateAsync();
        var (_, run) = await Prepare(test);
        var service = new RunStorageService(test.Context);
        await service.TransitionAsync(run.Id, new("running", 1), Token);
        const string payload = """{"nested":[{"text":"Aurora \ud83d\ude80","literal":"\\u0000","value":1e1000}]}""";
        var sample = await service.AddSnapshotAsync(run.Id, new(Start, "checkpoint", Cp1, payload), Token);
        await using var fresh = test.NewContext();
        var stored = await fresh.Snapshots.SingleAsync(x => x.Id == sample.Id, Token);
        using var document = JsonDocument.Parse(stored.PayloadJson);
        var nested = document.RootElement.GetProperty("nested")[0];
        Assert.Equal("Aurora \ud83d\ude80", nested.GetProperty("text").GetString());
        Assert.Equal("\\u0000", nested.GetProperty("literal").GetString());
        Assert.Equal(JsonValueKind.Number, nested.GetProperty("value").ValueKind);
    }

    [Theory]
    [InlineData("limit", "limit")]
    [InlineData("utc", "fromUtc")]
    [InlineData("range", "toUtc")]
    [InlineData("zone-pair", "zoneId")]
    [InlineData("zone-type", "zoneType")]
    public async Task Invalid_result_queries_have_explicit_field_errors(string invalid, string field)
    {
        await using var test = await TestDatabase.CreateAsync();
        var (_, run) = await Prepare(test);
        var query = invalid switch
        {
            "limit" => new RunResultQuery(Limit: 101),
            "utc" => new RunResultQuery(FromUtc: DateTime.SpecifyKind(Start, DateTimeKind.Unspecified)),
            "range" => new RunResultQuery(Start.AddMinutes(2), Start),
            "zone-pair" => new RunResultQuery(ZoneType: "checkpoint"),
            _ => new RunResultQuery(ZoneType: "unknown", ZoneId: Cp1)
        };
        var error = await Assert.ThrowsAsync<ScenarioValidationException>(() => new RunStorageService(test.Context).MetricsAsync(run.Id, query, Token));
        Assert.Contains(field, error.Errors.Keys);
    }

    [Theory]
    [InlineData("parameters")]
    [InlineData("launch-time")]
    [InlineData("start-time")]
    [InlineData("regression")]
    [InlineData("terminal")]
    public async Task PostgreSQL_rejects_mutable_inputs_and_invalid_lifecycle_updates(string invalid)
    {
        await using var test = await TestDatabase.CreateAsync();
        var (_, run) = await Prepare(test);
        var service = new RunStorageService(test.Context);
        await service.TransitionAsync(run.Id, new("running", 1), Token);
        if (invalid == "terminal") await service.TransitionAsync(run.Id, new("completed", 2), Token);
        test.Context.ChangeTracker.Clear();
        var tracked = await test.Context.Runs.SingleAsync(x => x.Id == run.Id, Token);
        if (invalid == "parameters") tracked.ParametersJson = "{\"seed\":99}";
        if (invalid == "launch-time") tracked.LaunchedAtUtc = tracked.LaunchedAtUtc!.Value.AddMinutes(1);
        if (invalid == "start-time") tracked.StartedAtUtc = tracked.StartedAtUtc.AddMinutes(-1);
        if (invalid == "regression") { tracked.Status = "planned"; tracked.LaunchedAtUtc = null; }
        if (invalid == "terminal") tracked.Revision++;
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => test.Context.SaveChangesAsync(Token));
        Assert.Equal(PostgresErrorCodes.CheckViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }

    [Fact]
    public async Task Recommendations_keep_baseline_and_every_ranked_candidate_and_restrict_purge()
    {
        await using var test = await TestDatabase.CreateAsync();
        var service = new RunStorageService(test.Context);
        var (baselineScenario, baseline) = await Prepare(test, "baseline");
        var (firstScenario, first) = await Prepare(test, "candidate-1");
        var (_, second) = await Prepare(test, "candidate-2");
        foreach (var run in new[] { baseline, first, second }) await Complete(service, run.Id);
        var recommendation = await service.RecommendAsync(new(baseline.Id, "Minimize waiting time", 12,
            "Candidate 2 has the lowest wait", [new(first.Id, 2, 8), new(second.Id, 1, 5, "{\"improvement\":7}")]), Token);
        await using var fresh = test.NewContext();
        var reopened = new RunStorageService(fresh);
        var persisted = Assert.IsType<RecommendationDetails>(await reopened.RecommendationAsync(recommendation.Recommendation.Id, Token));
        Assert.Equal(baseline.Id, persisted.Recommendation.BaselineRunId);
        Assert.Equal(12, persisted.Recommendation.BaselineScore);
        Assert.Equal(2, persisted.Candidates.Count);
        Assert.Equal(second.Id, persisted.RecommendedRunId);
        Assert.Equal(first.Id, persisted.Candidates[1].RunId);
        Assert.Single(await reopened.RecommendationsAsync(baseline.Id, Token));
        Assert.Single(await reopened.RecommendationsAsync(first.Id, Token));
        Assert.Single(await reopened.RecommendationsAsync(second.Id, Token));
        await Assert.ThrowsAsync<ScenarioConflictException>(() => reopened.DeleteScenarioAsync(baselineScenario.Scenario.Id, "purge", 1, Token));
        await Assert.ThrowsAsync<ScenarioConflictException>(() => reopened.DeleteScenarioAsync(firstScenario.Scenario.Id, "purge", 1, Token));
        Assert.NotNull(await reopened.GetAsync(first.Id, Token));
        var archived = await reopened.DeleteScenarioAsync(baselineScenario.Scenario.Id, "archive", 1, Token);
        Assert.True(archived.Archived);
        Assert.Equal(2, archived.Revision);
        Assert.NotNull(await reopened.RecommendationAsync(recommendation.Recommendation.Id, Token));
        var scenarios = new ScenarioService(fresh);
        Assert.NotNull((await scenarios.GetAsync(baselineScenario.Scenario.Id, Token))!.Scenario.DeletedAtUtc);
        Assert.DoesNotContain(await scenarios.ListAsync(null, Token), x => x.Id == baselineScenario.Scenario.Id);
        await Assert.ThrowsAsync<ScenarioNotFoundException>(() => scenarios.CreateRunAsync(baselineScenario.Scenario.Id, Token));
        await Assert.ThrowsAsync<ScenarioNotFoundException>(() => scenarios.DuplicateAsync(baselineScenario.Scenario.Id, new("copy", "Copy"), Token));
    }

    [Theory]
    [InlineData("baseline-running", "baselineRunId")]
    [InlineData("candidate-running", "candidates[0].runId")]
    [InlineData("baseline-candidate", "candidates[0].runId")]
    [InlineData("duplicate-candidate", "candidates[1].runId")]
    [InlineData("duplicate-rank", "candidates[1].rank")]
    [InlineData("empty", "candidates")]
    [InlineData("null", "candidates")]
    [InlineData("null-entry", "candidates[0]")]
    [InlineData("nul-objective", "objective")]
    [InlineData("nul-rationale", "rationale")]
    [InlineData("nul-assessment-value", "candidates[0].assessmentJson")]
    [InlineData("nul-assessment-key", "candidates[0].assessmentJson")]
    [InlineData("surrogate-assessment", "candidates[0].assessmentJson")]
    [InlineData("range-assessment", "candidates")]
    public async Task Invalid_comparisons_cannot_be_presented_as_recommendations(string invalid, string field)
    {
        await using var test = await TestDatabase.CreateAsync();
        var (_, baseline) = await Prepare(test, "baseline");
        var (_, candidate) = await Prepare(test, "candidate");
        var service = new RunStorageService(test.Context);
        if (invalid == "baseline-running") await service.TransitionAsync(baseline.Id, new("running", 1), Token);
        else await Complete(service, baseline.Id);
        if (invalid == "candidate-running") await service.TransitionAsync(candidate.Id, new("running", 1), Token);
        else await Complete(service, candidate.Id);
        IReadOnlyList<CandidateComparison> comparisons = invalid switch
        {
            "baseline-candidate" => [new(baseline.Id, 1, 5)],
            "duplicate-candidate" => [new(candidate.Id, 1, 5), new(candidate.Id, 2, 4)],
            "duplicate-rank" => [new(candidate.Id, 1, 5), new(Guid.NewGuid(), 1, 4)],
            "empty" => [], "null" => null!, "null-entry" => [null!],
            "nul-assessment-value" => [new(candidate.Id, 1, 5, """{"text":"\u0000"}""")],
            "nul-assessment-key" => [new(candidate.Id, 1, 5, """{"\u0000":1}""")],
            "surrogate-assessment" => [new(candidate.Id, 1, 5, """{"text":"\ud800"}""")],
            "range-assessment" => [new(candidate.Id, 1, 5, """{"value":1e1000000000}""")],
            _ => [new(candidate.Id, 1, 5)]
        };
        var request = new RecommendationRequest(baseline.Id, "Wait", 10, "Lower wait", comparisons);
        if (invalid == "nul-objective") request = request with { Objective = "Wait\0time" };
        if (invalid == "nul-rationale") request = request with { Rationale = "Lower\0wait" };
        var error = await Assert.ThrowsAsync<ScenarioValidationException>(() => service.RecommendAsync(request, Token));
        Assert.Contains(field, error.Errors.Keys);
        Assert.Equal(0, await test.Context.Recommendations.CountAsync(Token));
        Assert.Equal(0, await test.Context.RecommendationCandidates.CountAsync(Token));
    }

    [Fact]
    public async Task Comparisons_reject_failed_runs_and_different_layout_versions()
    {
        await using var test = await TestDatabase.CreateAsync();
        var (_, baseline) = await Prepare(test, "baseline");
        var (_, failed) = await Prepare(test, "failed");
        var service = new RunStorageService(test.Context);
        await Complete(service, baseline.Id);
        await service.TransitionAsync(failed.Id, new("running", 1), Token);
        await service.TransitionAsync(failed.Id, new("failed", 2, "Engine error"), Token);
        await Assert.ThrowsAsync<ScenarioValidationException>(() => service.RecommendAsync(new(baseline.Id, "Wait", 10, "Lower wait", [new(failed.Id, 1, 1)]), Token));
        var newLayout = await new ScenarioService(test.Context).PublishLayoutAsync(SyntheticScenarioSeeder.SceneId, Token);
        var (_, different) = await Prepare(test, "different", newLayout.Version.Id);
        await Complete(service, different.Id);
        var error = await Assert.ThrowsAsync<ScenarioValidationException>(() => service.RecommendAsync(new(baseline.Id, "Wait", 10, "Lower wait", [new(different.Id, 1, 1)]), Token));
        Assert.Contains("candidates[0].runId", error.Errors.Keys);
    }

    [Fact]
    public async Task Explicit_retention_policies_preserve_or_delete_the_owned_graph_atomically()
    {
        await using var test = await TestDatabase.CreateAsync();
        var (scenario, run) = await Prepare(test, "owned");
        var (_, otherRun) = await Prepare(test, "other");
        var service = new RunStorageService(test.Context);
        await Assert.ThrowsAsync<ScenarioValidationException>(() => service.DeleteScenarioAsync(scenario.Scenario.Id, "unknown", 1, Token));
        await Assert.ThrowsAsync<ScenarioValidationException>(() => service.DeleteScenarioAsync(scenario.Scenario.Id, "archive", 0, Token));
        await Assert.ThrowsAsync<ScenarioConflictException>(() => service.DeleteScenarioAsync(scenario.Scenario.Id, "archive", 2, Token));
        await Assert.ThrowsAsync<ScenarioConflictException>(() => service.DeleteScenarioAsync(scenario.Scenario.Id, "restrict", 1, Token));
        await service.TransitionAsync(run.Id, new("running", 1), Token);
        await Assert.ThrowsAsync<ScenarioConflictException>(() => service.DeleteScenarioAsync(scenario.Scenario.Id, "purge", 1, Token));
        await service.AddMetricAsync(run.Id, Metric(), Token);
        await service.AddSnapshotAsync(run.Id, new(Start, "checkpoint", Cp1, "{}"), Token);
        test.Context.Events.Add(new SceneEvent { Id = Guid.NewGuid(), SceneId = run.SceneId, SimulationRunId = run.Id, OccurredAtUtc = Start, Type = "test-result" });
        await test.Context.SaveChangesAsync(Token);
        await service.TransitionAsync(run.Id, new("completed", 2), Token);
        var deleted = await service.DeleteScenarioAsync(scenario.Scenario.Id, "purge", 1, Token);
        Assert.Equal(1, deleted.DeletedRuns);
        Assert.False(deleted.Archived);
        Assert.Null(await service.GetAsync(run.Id, Token));
        Assert.NotNull(await service.GetAsync(otherRun.Id, Token));
        Assert.Equal(0, await test.Context.Metrics.CountAsync(Token));
        Assert.Equal(0, await test.Context.Snapshots.CountAsync(Token));
        Assert.False(await test.Context.Events.AnyAsync(x => x.SimulationRunId == run.Id, Token));
        Assert.NotNull(await test.Context.LayoutVersions.SingleOrDefaultAsync(x => x.Id == scenario.Scenario.LayoutVersionId, Token));
        var empty = await CreateScenario(test, "empty");
        var restricted = await service.DeleteScenarioAsync(empty.Scenario.Id, "restrict", 1, Token);
        Assert.Equal(0, restricted.DeletedRuns);
        Assert.Null(await new ScenarioService(test.Context).GetAsync(empty.Scenario.Id, Token));
    }

    [Fact]
    public async Task Concurrent_launch_and_failure_writes_are_serialized()
    {
        await using var test = await TestDatabase.CreateAsync();
        var (_, run) = await Prepare(test);
        var launched = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            await using var context = test.NewContext();
            try { await new RunStorageService(context).TransitionAsync(run.Id, new("running", 1), Token); return true; }
            catch (ScenarioConflictException) { return false; }
        }));
        Assert.Single(launched, x => x);
        Assert.Single(launched, x => !x);
        await Task.WhenAll(Enumerable.Range(0, 2).Select(async index =>
        {
            await using var context = test.NewContext();
            var service = new RunStorageService(context);
            if (index == 0) await service.TransitionAsync(run.Id, new("failed", 2, "Concurrent failure"), Token);
            else
                try { await service.AddMetricAsync(run.Id, Metric(), Token); }
                catch (ScenarioConflictException) { }
        }));
        var results = await new RunStorageService(test.Context).MetricsAsync(run.Id, new(IncludeInvalid: true), Token);
        Assert.Equal("failed", results.Status);
        Assert.False(results.IsValid);
        Assert.True(results.Items.Count <= 1);
    }

    [Fact]
    public async Task Concurrent_purge_and_run_creation_leave_no_orphan_or_late_execution()
    {
        await using var test = await TestDatabase.CreateAsync();
        var (scenario, _) = await Prepare(test);
        await Task.WhenAll(Enumerable.Range(0, 2).Select(async index =>
        {
            await using var context = test.NewContext();
            if (index == 0) await new RunStorageService(context).DeleteScenarioAsync(scenario.Scenario.Id, "purge", 1, Token);
            else
                try { await new ScenarioService(context).CreateRunAsync(scenario.Scenario.Id, Token); }
                catch (ScenarioNotFoundException) { }
        }));
        Assert.False(await test.Context.Scenarios.AnyAsync(x => x.Id == scenario.Scenario.Id, Token));
        Assert.False(await test.Context.Runs.AnyAsync(x => x.ScenarioId == scenario.Scenario.Id, Token));
    }

    [Fact]
    public async Task Upgrade_preserves_input_snapshots_and_backfills_legacy_failure_metadata()
    {
        await using var test = await TestDatabase.CreateAsync();
        var (_, run) = await Prepare(test);
        var input = (await new RunStorageService(test.Context).GetAsync(run.Id, Token))!.InputSnapshotJson;
        await test.Context.GetService<IMigrator>().MigrateAsync("20261011131529_PersistScenarioInputs", Token);
        test.Context.ChangeTracker.Clear();
        var legacyId = Guid.NewGuid();
        await test.Context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO twin.runs (\"Id\", \"SceneId\", \"StartedAtUtc\", \"Status\", \"Seed\", \"ParametersJson\", \"IsSynthetic\") VALUES ({legacyId}, {run.SceneId}, {Start}, 'failed', 1, '{{}}'::jsonb, true)", Token);
        await test.Context.Database.MigrateAsync(Token);
        var service = new RunStorageService(test.Context);
        Assert.Equal(input, (await service.GetAsync(run.Id, Token))!.InputSnapshotJson);
        var legacy = (await service.GetAsync(legacyId, Token))!;
        Assert.False(legacy.ResultsValid);
        Assert.NotNull(legacy.FailureReason);
        Assert.NotNull(legacy.LaunchedAtUtc);
        Assert.NotNull(legacy.FinishedAtUtc);
        Assert.False(test.Context.Database.HasPendingModelChanges());
    }

    [Theory]
    [InlineData("cross-scene", "23503")]
    [InlineData("unknown-zone", "23514")]
    [InlineData("outside-window", "23514")]
    [InlineData("failed-run", "23514")]
    [InlineData("edit-sample", "23514")]
    public async Task PostgreSQL_rejects_invalid_result_ownership_time_zone_and_lifecycle(string invalid, string sqlState)
    {
        await using var test = await TestDatabase.CreateAsync();
        var (_, run) = await Prepare(test);
        var service = new RunStorageService(test.Context);
        await service.TransitionAsync(run.Id, new("running", 1), Token);
        if (invalid == "failed-run") await service.TransitionAsync(run.Id, new("failed", 2, "Failed engine"), Token);
        if (invalid == "edit-sample")
        {
            var sample = await service.AddMetricAsync(run.Id, Metric(), Token);
            sample.Value = 99;
        }
        else test.Context.Metrics.Add(new RunMetric
        {
            Id = Guid.NewGuid(), SceneId = invalid == "cross-scene" ? Guid.NewGuid() : run.SceneId, RunId = run.Id,
            ZoneType = "checkpoint", ZoneId = invalid == "unknown-zone" ? Guid.NewGuid() : Cp1,
            RecordedAtUtc = invalid == "outside-window" ? Start.AddMinutes(61) : Start, Name = "wait", Unit = "seconds", Value = 10
        });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => test.Context.SaveChangesAsync(Token));
        Assert.Equal(sqlState, Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }

    [Fact]
    public async Task PostgreSQL_requires_compared_candidates_and_immutable_recommendation_records()
    {
        await using var test = await TestDatabase.CreateAsync();
        var (_, baseline) = await Prepare(test, "baseline");
        var (_, candidate) = await Prepare(test, "candidate");
        var service = new RunStorageService(test.Context);
        await Complete(service, baseline.Id);
        await Complete(service, candidate.Id);
        var incomplete = new RunRecommendation
        {
            Id = Guid.NewGuid(), SceneId = baseline.SceneId, BaselineRunId = baseline.Id,
            Objective = "Wait", Rationale = "Incomplete comparison", BaselineScore = 10, CreatedAtUtc = DateTime.UtcNow
        };
        test.Context.Recommendations.Add(incomplete);
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => test.Context.SaveChangesAsync(Token));
        Assert.Equal(PostgresErrorCodes.CheckViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        test.Context.ChangeTracker.Clear();
        Assert.False(await test.Context.Recommendations.AnyAsync(x => x.Id == incomplete.Id, Token));
        var recommendation = await service.RecommendAsync(new(baseline.Id, "Wait", 10, "Lower wait", [new(candidate.Id, 1, 5)]), Token);
        recommendation.Candidates[0].Score = 99;
        var changed = await Assert.ThrowsAsync<DbUpdateException>(() => test.Context.SaveChangesAsync(Token));
        Assert.Equal(PostgresErrorCodes.CheckViolation, Assert.IsType<PostgresException>(changed.InnerException).SqlState);
    }

    [Fact]
    public async Task Demo_reset_removes_new_dependencies_and_preserves_other_scene_results()
    {
        await using var test = await TestDatabase.CreateAsync();
        var (_, baseline) = await Prepare(test, "baseline");
        var (_, candidate) = await Prepare(test, "candidate");
        var service = new RunStorageService(test.Context);
        await Complete(service, baseline.Id);
        await Complete(service, candidate.Id);
        await service.RecommendAsync(new(baseline.Id, "Wait", 10, "Lower wait", [new(candidate.Id, 1, 5)]), Token);
        var otherScene = new Scene { Id = Guid.NewGuid(), Code = "preserved-scene", Name = "Preserved" };
        test.Context.Scenes.Add(otherScene);
        await test.Context.SaveChangesAsync(Token);
        var scenarios = new ScenarioService(test.Context);
        var layout = await scenarios.PublishLayoutAsync(otherScene.Id, Token);
        var otherScenario = await scenarios.CreateAsync(new("preserved", "Preserved", "", otherScene.Id, layout.Version.Id,
            new ScenarioInputs { StartsAtUtc = Start, DurationMinutes = 60 }), Token);
        var other = await scenarios.CreateRunAsync(otherScenario.Scenario.Id, Token);
        await service.TransitionAsync(other.Id, new("running", 1), Token);
        await service.AddMetricAsync(other.Id, new(Start, "scene", otherScene.Id, "wait", 10, "seconds"), Token);
        await service.TransitionAsync(other.Id, new("completed", 2), Token);
        await SyntheticScenarioSeeder.ResetAsync(test.Context, Token);
        Assert.Equal(0, await test.Context.Recommendations.CountAsync(Token));
        Assert.Equal(0, await test.Context.RecommendationCandidates.CountAsync(Token));
        Assert.Equal(1, await test.Context.Metrics.CountAsync(Token));
        Assert.Equal(10, Assert.Single((await service.MetricsAsync(other.Id, new(), Token)).Items).Value);
        Assert.Null(await service.GetAsync(baseline.Id, Token));
    }

    [Fact]
    public async Task Archiving_allows_existing_run_to_finish_and_legacy_running_inputs_are_protected()
    {
        await using var test = await TestDatabase.CreateAsync();
        var (scenario, run) = await Prepare(test);
        var service = new RunStorageService(test.Context);
        await service.TransitionAsync(run.Id, new("running", 1), Token);
        await service.DeleteScenarioAsync(scenario.Scenario.Id, "archive", 1, Token);
        await service.AddMetricAsync(run.Id, Metric(), Token);
        await service.TransitionAsync(run.Id, new("completed", 2), Token);
        Assert.True((await service.MetricsAsync(run.Id, new(), Token)).IsValid);
        var now = DateTime.UtcNow;
        var legacy = new SimulationRun { Id = Guid.NewGuid(), SceneId = run.SceneId, StartedAtUtc = now, LaunchedAtUtc = now, Status = "running", Seed = 42 };
        test.Context.Runs.Add(legacy);
        await test.Context.SaveChangesAsync(Token);
        legacy.Seed = 99;
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => test.Context.SaveChangesAsync(Token));
        Assert.Equal(PostgresErrorCodes.CheckViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }

    private static RunMetricRequest Metric() => new(Start.AddMinutes(1), "checkpoint", Cp1, "queue_length", 10, "people");
    private static async Task Complete(RunStorageService service, Guid id)
    {
        await service.TransitionAsync(id, new("running", 1), Token);
        await service.AddMetricAsync(id, Metric(), Token);
        await service.TransitionAsync(id, new("completed", 2), Token);
    }
    private static async Task<(ScenarioDetails Scenario, SimulationRun Run)> Prepare(TestDatabase test, string code = "baseline", Guid? version = null)
    {
        var scenario = await CreateScenario(test, code, version);
        var run = await new ScenarioService(test.Context).CreateRunAsync(scenario.Scenario.Id, Token);
        return (scenario, run);
    }
    private static async Task<ScenarioDetails> CreateScenario(TestDatabase test, string code, Guid? version = null)
    {
        var service = new ScenarioService(test.Context);
        var versionId = version ?? await test.Context.LayoutVersions.Select(x => (Guid?)x.Id).FirstOrDefaultAsync(Token)
            ?? (await service.PublishLayoutAsync(SyntheticScenarioSeeder.SceneId, Token)).Version.Id;
        return await service.CreateAsync(new(code, code, "Synthetic run storage test", SyntheticScenarioSeeder.SceneId, versionId,
            new ScenarioInputs { StartsAtUtc = Start, DurationMinutes = 60, Populations = [new(Guid.NewGuid(), "Passengers", 120)] }), Token);
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using AeroSynapse.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AeroSynapse.Management.Tests;

public sealed class CopilotExplanationTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static async Task<(ScenarioDetails Scenario, SimulationRun Run, string Path)> Create(ManagementHost host)
    {
        using var publish = await host.Client.PostAsync($"/scenes/{SyntheticScenarioSeeder.SceneId}/layout-versions", null, Token);
        var layout = (await publish.Content.ReadFromJsonAsync<PublishedLayout>(Json, Token))!;
        var request = JsonSerializer.Deserialize<ScenarioWriteRequest>(File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "scenario-request.json")), Json)!;
        using var created = await host.Client.PostAsJsonAsync("/scenarios", request with { Code = "explain-" + Guid.NewGuid().ToString("N"), LayoutVersionId = layout.Version.Id }, Json, Token);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var scenario = (await created.Content.ReadFromJsonAsync<ScenarioDetails>(Json, Token))!;
        var run = await Run(host, scenario);
        return (scenario, run, Path(scenario, run));
    }
    private static string Path(ScenarioDetails s, SimulationRun run) => $"/scenarios/{s.Scenario.Id}/runs/{run.Id}/copilot";
    private static async Task<SimulationRun> Run(ManagementHost host, ScenarioDetails scenario)
    {
        using var started = await host.Client.PostAsync($"/scenarios/{scenario.Scenario.Id}/runs", null, Token);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var run = (await started.Content.ReadFromJsonAsync<SimulationRun>(Json, Token))!;
        Assert.True(await host.Dispatch(Token)); return run;
    }
    private static async Task<CopilotAnswer> Ask(ManagementHost host, ScenarioDetails scenario, string path, string question = "Rezumă situația zonei.")
    {
        using var response = await host.Client.PostAsJsonAsync(path, new CopilotQuestion(scenario.Inputs.StaffAllocations[0].CheckpointId, question), Json, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        return (await response.Content.ReadFromJsonAsync<CopilotAnswer>(Json, Token))!;
    }
    private static async Task<CopilotTranscript> Save(ManagementHost host, string path, string receipt, bool hide = true, int hours = 24)
    {
        using var response = await host.Client.PostAsJsonAsync(path + "/transcripts", new SaveCopilotTranscript(receipt, hide, hours), Json, Token);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Token));
        return (await response.Content.ReadFromJsonAsync<CopilotTranscript>(Json, Token))!;
    }
    private static void VerifyFacts<T>(T output, CopilotExplanation explanation)
    {
        var root = JsonSerializer.SerializeToElement(output, Json);
        foreach (var fact in explanation.Facts)
        {
            var current = root;
            foreach (var part in fact.SourcePath.Split('/').Skip(1)) current = current.ValueKind == JsonValueKind.Array ? current[int.Parse(part)] : current.GetProperty(part);
            Assert.Equal(fact.Value, current.ValueKind == JsonValueKind.Null ? null : current.GetDouble());
            Assert.Contains(explanation.Runs, r => r.RunId == fact.RunId);
        }
        Assert.Contains("DEMO", explanation.Disclosure); Assert.Contains("nu este conectat", explanation.Disclosure);
    }

    [Fact]
    public async Task Answers_and_inspection_have_exact_source_paths_hashes_and_never_save_implicitly()
    {
        await using var host = await ManagementHost.CreateAsync(); var (s, run, path) = await Create(host);
        var answer = await Ask(host, s, path); Assert.NotNull(answer.Explanation); VerifyFacts(answer, answer.Explanation);
        var canonical = answer with { Explanation = null, Receipt = null };
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical, Json))), answer.Explanation.OutputHash);
        using var response = await host.Client.PostAsJsonAsync(path + "/tools", new { intent = "diagnose", zoneId = answer.Scope.ZoneId, minute = answer.Scope.Minute,
            call = new { name = "inspect_zone", arguments = new { question = "Rezumă situația zonei." } } }, Json, Token);
        var tool = (await response.Content.ReadFromJsonAsync<CopilotToolResult>(Json, Token))!;
        VerifyFacts(tool, tool.Explanation!); Assert.NotNull(tool.Receipt);
        await using var scope = host.Services.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<TwinDbContext>().CopilotTranscripts.ToArrayAsync(Token));
        var missing = await Ask(host, s, path, "De ce a crescut coada?");
        Assert.Empty(missing.Explanation!.Facts); Assert.Contains("Nu pot concluziona", missing.Explanation.Summary);
    }

    [Fact]
    public async Task Redaction_is_default_and_retries_cannot_extend_retention_or_restore_questions()
    {
        var clock = new Clock(); await using var host = await ManagementHost.CreateAsync(services: services => services.AddSingleton<TimeProvider>(clock));
        var (s, _, path) = await Create(host); var answer = await Ask(host, s, path, "Rezumă coada. operator-secret");
        var saved = await Save(host, path, answer.Receipt!); Assert.True(saved.Redacted); Assert.Null(saved.Question);
        clock.Now += TimeSpan.FromMinutes(1);
        var retry = await Save(host, path, answer.Receipt!, false, 168);
        Assert.Equal(saved.Id, retry.Id); Assert.Equal(saved.ExpiresAtUtc, retry.ExpiresAtUtc); Assert.True(retry.Redacted);
        using var revealed = await host.Client.GetAsync(path + $"/transcripts/{saved.Id}?includeSensitive=true", Token);
        Assert.DoesNotContain("operator-secret", await revealed.Content.ReadAsStringAsync(Token));
        var second = await Ask(host, s, path, "Rezumă coada. explicitly-kept-secret");
        var kept = await Save(host, path, second.Receipt!, false); Assert.True(kept.SensitiveDetailsHidden); Assert.Null(kept.Question);
        var shown = (await host.Client.GetFromJsonAsync<CopilotTranscript>(path + $"/transcripts/{kept.Id}?includeSensitive=true", Json, Token))!;
        Assert.Contains("explicitly-kept-secret", shown.Question); Assert.False(shown.SensitiveDetailsHidden);
        var hidden = (await host.Client.GetFromJsonAsync<CopilotTranscript>(path + $"/transcripts/{kept.Id}", Json, Token))!; Assert.Null(hidden.Question);
        using var list = await host.Client.GetAsync(path + "/transcripts", Token);
        Assert.DoesNotContain("secret", await list.Content.ReadAsStringAsync(Token));
        using var redact = await host.Client.PostAsync(path + $"/transcripts/{kept.Id}/redact", null, Token); Assert.Equal(HttpStatusCode.NoContent, redact.StatusCode);
        await Save(host, path, second.Receipt!, false);
        shown = (await host.Client.GetFromJsonAsync<CopilotTranscript>(path + $"/transcripts/{kept.Id}?includeSensitive=true", Json, Token))!;
        Assert.True(shown.Redacted); Assert.Null(shown.Question);
        using var delete = await host.Client.DeleteAsync(path + $"/transcripts/{kept.Id}", Token); Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        using var gone = await host.Client.GetAsync(path + $"/transcripts/{kept.Id}", Token); Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        using var replayDeleted = await host.Client.PostAsJsonAsync(path + "/transcripts", new SaveCopilotTranscript(second.Receipt!, false), Json, Token);
        Assert.Equal(HttpStatusCode.NotFound, replayDeleted.StatusCode);
        await using var scope = host.Services.CreateAsyncScope();
        var tombstone = await scope.ServiceProvider.GetRequiredService<TwinDbContext>().CopilotTranscripts.SingleAsync(t => t.Id == kept.Id, Token);
        Assert.Null(tombstone.Question); Assert.Equal("{}", tombstone.ExplanationJson); Assert.Equal(Guid.Empty, tombstone.ZoneId);
        Assert.True(tombstone.ExpiresAtUtc <= clock.GetUtcNow().UtcDateTime.AddMinutes(15));
    }

    [Fact]
    public async Task Receipts_reject_tampering_wrong_context_and_expiration_and_retention_is_enforced()
    {
        var clock = new Clock(); await using var host = await ManagementHost.CreateAsync(services: services => services.AddSingleton<TimeProvider>(clock));
        var (s, run, path) = await Create(host); var answer = await Ask(host, s, path);
        foreach (var (target, request) in new[] {
            (path, new SaveCopilotTranscript("forged receipt")),
            (path.Replace(run.Id.ToString(), Guid.NewGuid().ToString()), new SaveCopilotTranscript(answer.Receipt!)),
            (path.Replace(s.Scenario.Id.ToString(), Guid.NewGuid().ToString()), new SaveCopilotTranscript(answer.Receipt!)),
            (path, new SaveCopilotTranscript(answer.Receipt!, RetentionHours: 0)),
            (path, new SaveCopilotTranscript(answer.Receipt!, RetentionHours: 169)) })
        {
            using var denied = await host.Client.PostAsJsonAsync(target + "/transcripts", request, Json, Token);
            Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode); Assert.Contains("validation_failed", await denied.Content.ReadAsStringAsync(Token));
        }
        var saved = await Save(host, path, answer.Receipt!, hours: 1);
        using var foreign = await host.Client.GetAsync(path.Replace(run.Id.ToString(), Guid.NewGuid().ToString()) + $"/transcripts/{saved.Id}", Token);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        clock.Now += TimeSpan.FromMinutes(16);
        using var expired = await host.Client.PostAsJsonAsync(path + "/transcripts", new SaveCopilotTranscript(answer.Receipt!), Json, Token);
        Assert.Equal(HttpStatusCode.BadRequest, expired.StatusCode);
        clock.Now += TimeSpan.FromHours(1);
        // Identity's session has also expired; inspect transcript retention using
        // a freshly authenticated member of the same organization.
        using var renewed = await host.SignInAsync("Operator");
        using var get = await renewed.GetAsync(path + $"/transcripts/{saved.Id}", Token); Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Empty((await renewed.GetFromJsonAsync<CopilotTranscriptSummary[]>(path + "/transcripts", Json, Token))!);
        await using var scope = host.Services.CreateAsyncScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<ICopilotTranscriptStore>().PurgeExpiredAsync(Token));
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<TwinDbContext>().CopilotTranscripts.ToArrayAsync(Token));
    }

    [Fact]
    public async Task Archive_retains_transcripts_until_expiration_and_purge_cascades_from_run()
    {
        await using var host = await ManagementHost.CreateAsync(); var (s, _, path) = await Create(host);
        var answer = await Ask(host, s, path); var saved = await Save(host, path, answer.Receipt!);
        var retried = await Save(host, path, answer.Receipt!);
        Assert.Equal(saved.CreatedAtUtc, retried.CreatedAtUtc); Assert.Equal(saved.ExpiresAtUtc, retried.ExpiresAtUtc);
        using var archive = await host.Client.DeleteAsync($"/scenarios/{s.Scenario.Id}?retention=archive&revision=1", Token); Assert.Equal(HttpStatusCode.OK, archive.StatusCode);
        using var retained = await host.Client.GetAsync(path + $"/transcripts/{saved.Id}", Token); Assert.Equal(HttpStatusCode.OK, retained.StatusCode);
        using var purge = await host.Client.DeleteAsync($"/scenarios/{s.Scenario.Id}?retention=purge&revision=2", Token); Assert.Equal(HttpStatusCode.OK, purge.StatusCode);
        await using var scope = host.Services.CreateAsyncScope(); Assert.Empty(await scope.ServiceProvider.GetRequiredService<TwinDbContext>().CopilotTranscripts.ToArrayAsync(Token));
    }

    [Fact]
    public async Task Real_solver_and_simulator_comparisons_cite_both_runs_and_rejected_KPI_reasons()
    {
        await using var host = await ManagementHost.CreateAsync(realEngine: true);
        var s = await StaffingEvaluationTests.Create(host, scheduledSurge: true, multipleCheckpoints: true);
        var run = await Run(host, s); var request = await StaffingEvaluationTests.Request(host, s);
        await StaffingEvaluationTests.Start(host, s.Scenario.Id, request); await StaffingEvaluationTests.Drain(host);
        using var response = await host.Client.PostAsJsonAsync(Path(s, run) + "/tools", new { intent = "diagnose", zoneId = s.Inputs.StaffAllocations[0].CheckpointId,
            minute = 60, call = new { name = "read_evaluation", arguments = new { evaluationId = request.CommandId } } }, Json, Token);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Token));
        var tool = (await response.Content.ReadFromJsonAsync<CopilotToolResult>(Json, Token))!; var explanation = tool.Explanation!;
        VerifyFacts(tool, explanation); Assert.NotEmpty(explanation.Comparisons);
        foreach (var comparison in explanation.Comparisons)
        {
            Assert.Equal(comparison.After - comparison.Before, comparison.Delta);
            if (comparison.Before > 0) Assert.Equal(comparison.Delta / comparison.Before * 100, comparison.Percent);
            else { Assert.Null(comparison.Percent); Assert.NotNull(comparison.PercentUnavailableReason); }
            using var replay = await host.Client.GetAsync($"/runs/{comparison.CandidateRunId}/replay", Token); Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        }
        Assert.Contains(explanation.Outcomes, o => o.Status == "rejected" && o.Reasons.Any(r => r.Contains("degradează")));
        Assert.Contains(explanation.Limitations, text => text.Contains("semnificație statistică"));
        var variants = tool.Evaluation!.Variants;
        var empty = variants[0].Metrics! with { MeanWaitMinutes = 0, P95WaitMinutes = null, BoardingRisk = 0, UnprocessedCount = 0 };
        var inconclusive = tool with { Evaluation = tool.Evaluation with { RecommendedRunId = null, Variants = [
            variants[0] with { Metrics = empty },
            variants[1] with { Metrics = empty, Assessment = new("unchanged", ["no_simulated_improvement"]) },
            variants[2] with { RunStatus = "failed", Metrics = null, Assessment = new("failed", ["private-error-token"]) },
            variants[3] with { Metrics = null, Assessment = new("unassessable", ["insufficient_observed_metrics"]) } ] } };
        var limited = CopilotExplanationBuilder.Tool(inconclusive);
        Assert.All(limited.Comparisons, c => Assert.Null(c.Percent));
        Assert.Contains(limited.Comparisons, c => c.PercentUnavailableReason == "missing_data");
        Assert.Contains(limited.Comparisons, c => c.PercentUnavailableReason == "nonpositive_baseline");
        Assert.Contains(limited.Outcomes, o => o.Status == "unchanged" && o.Reasons.Any(r => r.Contains("nu arată")));
        Assert.Contains(limited.Outcomes, o => o.Status == "failed" && o.Reasons.Any(r => r.Contains("eșuat")));
        Assert.DoesNotContain(limited.Facts, f => f.RunId == variants[2].RunId || f.RunId == variants[3].RunId);
        Assert.DoesNotContain("private-error-token", JsonSerializer.Serialize(limited));
    }
}

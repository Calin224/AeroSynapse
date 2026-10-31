using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
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

public sealed class CopilotWorkflowTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed class Clock : TimeProvider { public DateTimeOffset Value = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Value; }
    private sealed class ToolControl
    {
        public bool BlockForecast; public bool FailForecast; public int Calls;
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class ControlledTools(CopilotTools inner, ToolControl control) : ICopilotTools
    {
        public async Task<CopilotToolResult> ExecuteAsync(Guid scenario, Guid run, CopilotToolRequest request, CancellationToken token)
        {
            Interlocked.Increment(ref control.Calls);
            if (request.Call.Name == "forecast_queues")
            {
                if (control.FailForecast) throw new CopilotToolRejectedException("tool_unavailable", "call", "private-provider-token", 502);
                if (control.BlockForecast) { control.Started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            }
            return await inner.ExecuteAsync(scenario, run, request, token);
        }
    }
    private static Task<ManagementHost> Host(ToolControl control) => ManagementHost.CreateAsync(services: services => {
        services.AddScoped<CopilotTools>(); services.RemoveAll<ICopilotTools>();
        services.AddScoped<ICopilotTools>(provider => new ControlledTools(provider.GetRequiredService<CopilotTools>(), control)); });
    private static async Task<(ScenarioDetails Scenario, SimulationRun Run)> Source(ManagementHost host)
    {
        var scenario = await host.CreateScenarioAsync(request => request with { Code = "playbook-" + Guid.NewGuid().ToString("N") });
        return (scenario, await Run(host, scenario));
    }
    private static Task<SimulationRun> Run(ManagementHost host, ScenarioDetails scenario) => host.StartRunAsync(scenario.Scenario.Id);
    private static string Path(ScenarioDetails s, SimulationRun r) => $"/scenarios/{s.Scenario.Id}/runs/{r.Id}/copilot/workflows";
    private static StartCopilotWorkflow Request(ScenarioDetails s) => new(Guid.NewGuid(), "queue-diagnosis", "1.0", s.Inputs.StaffAllocations[0].CheckpointId, 30);
    private static async Task<CopilotWorkflow> Start(ManagementHost host, string path, StartCopilotWorkflow request)
    {
        using var response = await host.Client.PostAsJsonAsync(path, request, Json, Token);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync(Token));
        return (await response.Content.ReadFromJsonAsync<CopilotWorkflow>(Json, Token))!;
    }
    private static Task<CopilotWorkflow?> Get(ManagementHost host, string path, Guid id) => host.Client.GetFromJsonAsync<CopilotWorkflow>(path + "/" + id, Json, Token);
    private static async Task Pump(ManagementHost host)
    {
        await using var scope = host.Services.CreateAsyncScope();
        Assert.True(await scope.ServiceProvider.GetRequiredService<CopilotWorkflowProcessor>().ProcessNextAsync(Token));
    }
    private static async Task<CopilotWorkflow> Drain(ManagementHost host, string path, Guid id)
    {
        for (var i = 0; i < 80; i++)
        {
            await using var scope = host.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<CopilotWorkflowProcessor>().ProcessNextAsync(Token);
            await scope.ServiceProvider.GetRequiredService<BatchProcessor>().ProcessNextAsync(Token);
            var value = (await Get(host, path, id))!;
            if (value.Status is not ("pending" or "running") && !value.CancellationPending) return value;
            await Task.Delay(50, Token);
        }
        Assert.Fail("Workflow did not terminate."); return null!;
    }
    private static async Task<int> RunCount(ManagementHost host)
    { await using var scope = host.Services.CreateAsyncScope(); return await scope.ServiceProvider.GetRequiredService<TwinDbContext>().Runs.CountAsync(Token); }
    private static void VerifySources(CopilotWorkflow workflow)
    {
        var explanation = workflow.Conclusion!.Explanation!; var root = JsonSerializer.SerializeToElement(workflow, Json);
        double? Value(string path)
        {
            var value = root;
            foreach (var part in path.Split('/').Skip(1)) value = value.ValueKind == JsonValueKind.Array ? value[int.Parse(part)] : value.GetProperty(part);
            return value.ValueKind == JsonValueKind.Null ? null : value.GetDouble();
        }
        Assert.NotEmpty(explanation.Facts);
        Assert.All(explanation.Facts, fact => Assert.Equal(fact.Value, Value(fact.SourcePath)));
        Assert.All(explanation.Comparisons, c => { Assert.Equal(c.Before, Value(c.BeforeSource)); Assert.Equal(c.After, Value(c.AfterSource)); });
    }

    [Fact]
    public async Task Diagnostic_steps_are_durable_read_only_and_reproducible_with_an_identical_manifest()
    {
        await using var host = await ManagementHost.CreateAsync(); var (s, run) = await Source(host); var path = Path(s, run);
        var count = await RunCount(host); var request = Request(s); var workflow = await Start(host, path, request);
        Assert.Equal("pending", workflow.Status); Assert.Equal(3, workflow.Steps.Count); Assert.All(workflow.Steps, step => Assert.Equal("pending", step.Status));
        var retry = await Start(host, path, request); Assert.Equal(workflow.Id, retry.Id);
        using var conflict = await host.Client.PostAsJsonAsync(path, request with { HorizonMinutes = 16 }, Json, Token); Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("pending", (await Get(host, path, workflow.Id))!.Steps[0].Status); // GET never advances.
        await Pump(host); var observed = (await Get(host, path, workflow.Id))!;
        Assert.Equal("completed", observed.Steps[0].Status); Assert.Equal("pending", observed.Steps[1].Status); Assert.NotNull(observed.Steps[0].Output);
        workflow = await Drain(host, path, workflow.Id); Assert.Equal("completed", workflow.Status); Assert.Equal("inconclusive", workflow.Conclusion!.Status); VerifySources(workflow);
        using var replay = await host.Client.PostAsJsonAsync(path + $"/{workflow.Id}/replay", new ReplayCopilotWorkflow(Guid.NewGuid()), Json, Token);
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode); var copied = (await replay.Content.ReadFromJsonAsync<CopilotWorkflow>(Json, Token))!;
        Assert.Equal(workflow.ManifestHash, copied.ManifestHash); Assert.Equal(workflow.SourceHash, copied.SourceHash); Assert.Equal(workflow.Seed, copied.Seed);
        copied = await Drain(host, path, copied.Id); VerifySources(copied);
        Assert.Equal(workflow.Conclusion.Explanation!.Facts, copied.Conclusion!.Explanation!.Facts);
        Assert.Equal(workflow.Conclusion.Explanation.OutputHash, copied.Conclusion.Explanation.OutputHash);
        Assert.Equal(count, await RunCount(host));
        await using var scope = host.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<TwinDbContext>();
        Assert.Empty(await db.Recommendations.ToArrayAsync(Token)); Assert.Empty(await db.CopilotTranscripts.ToArrayAsync(Token));
        using var foreign = await host.Client.GetAsync(path.Replace(run.Id.ToString(), Guid.NewGuid().ToString()) + "/" + workflow.Id, Token);
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    [Theory]
    [InlineData("investigator", "evaluate_staffing")][InlineData("reviewer", "approve_recommendation")]
    [InlineData("optimizer", "shell")][InlineData("optimizer", "http_request")][InlineData("optimizer", "sql")]
    [InlineData("coordinator", "inspect_zone")][InlineData("intruder", "inspect_zone")]
    public void Agent_policy_refuses_tools_outside_role_capabilities(string agent, string tool)
    {
        var definition = CopilotPlaybookPolicy.Definition("staffing-assessment", "1.0");
        Assert.Equal("playbook_tool_denied", Assert.Throws<CopilotToolRejectedException>(() => CopilotPlaybookPolicy.Admit(definition, agent, tool)).Code);
        foreach (var playbook in CopilotPlaybookPolicy.Catalog)
        {
            Assert.False(string.IsNullOrWhiteSpace(playbook.Trigger)); Assert.NotEmpty(playbook.StopConditions);
            foreach (var step in playbook.Steps.Where(s => s.Tool is not null)) CopilotPlaybookPolicy.Admit(playbook, step.Agent, step.Tool!);
        }
    }
    [Theory]
    [InlineData("\"agent\":\"optimizer\"")][InlineData("\"tools\":[\"approve_recommendation\"]")]
    [InlineData("\"steps\":[]")][InlineData("\"playbookVersion\":\"99\"")]
    public async Task HTTP_rejects_caller_supplied_roles_plans_tools_and_duplicate_fields(string extra)
    {
        await using var host = await ManagementHost.CreateAsync();
        var body = "{\"commandId\":\"" + Guid.NewGuid() + "\",\"playbookId\":\"queue-diagnosis\",\"playbookVersion\":\"1.0\",\"zoneId\":\"" + Guid.NewGuid()
            + "\",\"minute\":15," + extra + "}";
        using var response = await host.Client.PostAsync($"/scenarios/{Guid.NewGuid()}/runs/{Guid.NewGuid()}/copilot/workflows", new StringContent(body, Encoding.UTF8, "application/json"), Token);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode); Assert.Contains("invalid_tool_arguments", await response.Content.ReadAsStringAsync(Token));
        Assert.Null(host.Engine.LastRequest);
    }
    [Fact]
    public async Task Invalid_time_scope_versions_and_diagnostic_mutations_are_rejected_before_work_is_created()
    {
        await using var host = await ManagementHost.CreateAsync(); var (s, run) = await Source(host); var path = Path(s, run);
        var request = Request(s);
        foreach (var invalid in new[] { request with { Minute = int.MaxValue }, request with { Minute = -1 },
            request with { PlaybookVersion = "99" }, request with { ZoneId = Guid.NewGuid() },
            request with { Proposal = (await StaffingEvaluationTests.Request(host, s)).Proposal } })
        {
            using var response = await host.Client.PostAsJsonAsync(path, invalid, Json, Token);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        }
        await using var scope = host.Services.CreateAsyncScope(); Assert.Empty(await scope.ServiceProvider.GetRequiredService<TwinDbContext>().CopilotWorkflows.ToArrayAsync(Token));
    }
    [Fact]
    public async Task Cancellation_before_dispatch_and_deadline_stop_future_steps_and_purge_respects_active_workflows()
    {
        var clock = new Clock(); await using var host = await ManagementHost.CreateAsync(services: services => services.AddSingleton<TimeProvider>(clock));
        var (s, run) = await Source(host); var path = Path(s, run); var workflow = await Start(host, path, Request(s));
        using var blocked = await host.Client.DeleteAsync($"/scenarios/{s.Scenario.Id}?retention=purge&revision=1", Token); Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        using var cancel = await host.Client.PostAsync(path + $"/{workflow.Id}/cancel", null, Token); Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        await Pump(host); workflow = (await Get(host, path, workflow.Id))!;
        Assert.Equal("cancelled", workflow.Status); Assert.All(workflow.Steps, step => Assert.Equal("skipped", step.Status)); Assert.Equal("inconclusive", workflow.Conclusion!.Status);
        var timed = await Start(host, path, Request(s) with { TimeLimitSeconds = 15 });
        clock.Value += TimeSpan.FromSeconds(16); await Pump(host); timed = (await Get(host, path, timed.Id))!;
        Assert.Equal("timed_out", timed.Status); Assert.All(timed.Steps, step => Assert.Equal(0, step.Attempts));
        using var purge = await host.Client.DeleteAsync($"/scenarios/{s.Scenario.Id}?retention=purge&revision=1", Token); Assert.Equal(HttpStatusCode.OK, purge.StatusCode);
        using var gone = await host.Client.GetAsync(path + "/" + workflow.Id, Token); Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }
    [Fact]
    public async Task Inflight_tool_is_observable_and_operator_cancellation_interrupts_it()
    {
        var control = new ToolControl { BlockForecast = true }; await using var host = await Host(control);
        var (s, run) = await Source(host); var path = Path(s, run); var workflow = await Start(host, path, Request(s)); await Pump(host);
        var work = Pump(host); await control.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        var running = (await Get(host, path, workflow.Id))!; Assert.Equal("running", running.Steps[1].Status);
        using var cancel = await host.Client.PostAsync(path + $"/{workflow.Id}/cancel", null, Token); Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        await work.WaitAsync(TimeSpan.FromSeconds(5), Token);
        workflow = (await Get(host, path, workflow.Id))!; Assert.Equal("cancelled", workflow.Status);
        Assert.Equal("cancelled", workflow.Steps[1].Status); Assert.Equal("skipped", workflow.Steps[2].Status); Assert.Equal(2, control.Calls);
    }
    [Fact]
    public async Task Tool_failures_and_tampered_plans_are_terminal_and_sanitized()
    {
        var control = new ToolControl { FailForecast = true }; await using var host = await Host(control);
        var (s, run) = await Source(host); var path = Path(s, run); var failed = await Start(host, path, Request(s)); failed = await Drain(host, path, failed.Id);
        Assert.Equal("failed", failed.Status); Assert.Equal("tool_unavailable", failed.StopCode); Assert.Equal("inconclusive", failed.Conclusion!.Status);
        Assert.DoesNotContain("private-provider-token", JsonSerializer.Serialize(failed));
        var poisoned = await Start(host, path, Request(s)); var before = control.Calls;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TwinDbContext>(); var row = await db.CopilotWorkflows.SingleAsync(w => w.Id == poisoned.Id, Token);
            row.StepsJson = JsonSerializer.Serialize(poisoned.Steps.Select((step, index) => index == 0 ? step with { Agent = "reviewer", Tool = "approve_recommendation" } : step), Json);
            await db.SaveChangesAsync(Token);
        }
        await Pump(host); poisoned = (await Get(host, path, poisoned.Id))!;
        Assert.Equal("playbook_plan_denied", poisoned.StopCode); Assert.Equal(before, control.Calls);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Evaluation_step_recovery_is_idempotent_and_cancellation_only_stops_its_owned_batch(bool revokeTools)
    {
        await using var host = await ManagementHost.CreateAsync(realEngine: true);
        var s = await StaffingEvaluationTests.Create(host); var run = await Run(host, s); var path = Path(s, run);
        var unrelatedRequest = (await StaffingEvaluationTests.Request(host, s)) with { CandidateCount = 1 };
        var proposal = unrelatedRequest.Proposal;
        await StaffingEvaluationTests.Start(host, s.Scenario.Id, unrelatedRequest);
        var workflow = await Start(host, path, Request(s) with { PlaybookId = "staffing-assessment", Proposal = proposal, CandidateCount = 1 });
        for (var i = 0; i < 4; i++) await Pump(host); workflow = (await Get(host, path, workflow.Id))!;
        Assert.True(workflow.Steps[3].Status == "completed", JsonSerializer.Serialize(workflow, Json)); Assert.NotNull(workflow.Steps[3].Arguments);
        var count = await RunCount(host);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TwinDbContext>(); var row = await db.CopilotWorkflows.SingleAsync(w => w.Id == workflow.Id, Token);
            row.StepsJson = JsonSerializer.Serialize(workflow.Steps.Select(s => s.Id == "evaluate" ? s with { Status = "running", Output = null } : s), Json);
            await db.SaveChangesAsync(Token);
        }
        await Pump(host); Assert.Equal(count, await RunCount(host));
        if (revokeTools)
        {
            await using var policyScope = host.Services.CreateAsyncScope();
            var policyDb = policyScope.ServiceProvider.GetRequiredService<TwinDbContext>();
            var policy = await policyDb.AiPolicies.AsNoTracking().OrderByDescending(p => p.Version).FirstAsync(Token);
            policy.Id = Guid.NewGuid(); policy.Version++; policy.RecordedAtUtc = DateTime.UtcNow;
            policy.ActorId = "test-revocation"; policy.AllowedToolsJson = "[]";
            policyDb.AiPolicies.Add(policy); await policyDb.SaveChangesAsync(Token);
            using var denied = await host.Client.PostAsJsonAsync($"/scenarios/{s.Scenario.Id}/runs/{run.Id}/copilot/tools",
                new { intent = "optimize", zoneId = s.Inputs.StaffAllocations[0].CheckpointId, minute = 30,
                    call = new { name = "cancel_evaluation", arguments = new { evaluationId = workflow.EvaluationId } } }, Json, Token);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        using var cancel = await host.Client.PostAsync(path + $"/{workflow.Id}/cancel", null, Token); Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        workflow = await Drain(host, path, workflow.Id); Assert.Equal("cancelled", workflow.Status); Assert.Equal("skipped", workflow.Steps[4].Status);
        var batch = (await host.Client.GetFromJsonAsync<BatchDetails>($"/simulation-batches/{workflow.EvaluationId}", Json, Token))!;
        Assert.Equal("cancelled", batch.Batch.Status); Assert.All(batch.Replications, r => Assert.False(r.ResultsValid));
        var unrelated = (await host.Client.GetFromJsonAsync<BatchDetails>($"/simulation-batches/{unrelatedRequest.CommandId}", Json, Token))!;
        Assert.False(unrelated.Batch.CancellationRequested); Assert.NotEqual("cancelled", unrelated.Batch.Status);
        await using var auditScope = host.Services.CreateAsyncScope();
        var cleanup = await auditScope.ServiceProvider.GetRequiredService<TwinDbContext>().AiAgentEvents.AsNoTracking()
            .Where(e => e.AttemptId == workflow.Id && e.Kind == "workflow_cleanup").ToArrayAsync(Token);
        var entry = Assert.Single(cleanup);
        Assert.Equal("cancellation_requested", entry.Status); Assert.Equal("system", entry.ActorId);
    }
    [Fact]
    public async Task Real_multi_agent_evaluation_has_numeric_evidence_and_reproduces_with_independent_runs()
    {
        await using var host = await ManagementHost.CreateAsync(realEngine: true);
        var s = await StaffingEvaluationTests.Create(host, scheduledSurge: true); var run = await Run(host, s); var path = Path(s, run);
        var proposal = (await StaffingEvaluationTests.Request(host, s)).Proposal;
        var original = await Start(host, path, Request(s) with { PlaybookId = "staffing-assessment", Proposal = proposal, CandidateCount = 1, TimeLimitSeconds = 180 });
        original = await Drain(host, path, original.Id); Assert.Equal("completed", original.Status); Assert.Equal("justified", original.Conclusion!.Status); VerifySources(original);
        Assert.Equal(4, original.Steps.Select(step => step.Agent).Distinct().Count());
        using var response = await host.Client.PostAsJsonAsync(path + $"/{original.Id}/replay", new ReplayCopilotWorkflow(Guid.NewGuid()), Json, Token);
        var replayed = (await response.Content.ReadFromJsonAsync<CopilotWorkflow>(Json, Token))!; replayed = await Drain(host, path, replayed.Id);
        Assert.Equal(original.ManifestHash, replayed.ManifestHash); Assert.Equal(original.Conclusion.Status, replayed.Conclusion!.Status);
        var before = original.Steps[4].Output!.Evaluation!; var after = replayed.Steps[4].Output!.Evaluation!;
        Assert.NotEqual(original.EvaluationId, replayed.EvaluationId);
        Assert.Equal(before.Variants.Select(v => JsonSerializer.Serialize(v.Metrics, Json)), after.Variants.Select(v => JsonSerializer.Serialize(v.Metrics, Json)));
        Assert.DoesNotContain(after.Variants, v => before.Variants.Any(other => other.RunId == v.RunId));
        await using var scope = host.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<TwinDbContext>();
        Assert.Empty(await db.Recommendations.ToArrayAsync(Token)); Assert.Empty(await db.RecommendationReviews.ToArrayAsync(Token));
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using AeroSynapse.Infrastructure.Execution;
using AeroSynapse.Infrastructure.Governance;
using AeroSynapse.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace AeroSynapse.Management.Tests;

public sealed class GovernanceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly CopilotModelInput Input = new(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 10), "Queue summary", [new("queuedPassengers", "Queue", 7, "passengers")]);
    private sealed class Provider(string mode = "available") : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            if (mode == "network") throw new HttpRequestException("Never expose provider credentials");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
                done = true, message = new { content = mode == "injection" ? "{\"topic\":\"queue\",\"answer\":\"99999 real passengers\"}" : "{\"topic\":\"queue\"}" }, prompt_eval_count = 88, eval_count = 6 }), Encoding.UTF8, "application/json") });
        }
    }
    private static async Task<AiPolicyRecord> Policy(ManagementHost host, string[]? allowed = null, int calls = 100, decimal budget = 1, decimal reservation = 0.01m, bool enabled = true)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TwinDbContext>();
        var version = await db.AiPolicies.MaxAsync(row => row.Version, Token);
        return new() { Id = Guid.NewGuid(), Version = version + 1, RecordedAtUtc = DateTime.UtcNow,
            ActorId = "test-fixture", ModelEnabled = enabled, PromptVersion = CopilotPrompts.Version,
            AllowedToolsJson = JsonSerializer.Serialize(allowed ?? CopilotToolPolicy.Catalog.Select(t => t.Name).ToArray()),
            DailyCallLimit = calls, DailyBudgetUsd = budget, CallReservationUsd = reservation };
    }
    private static async Task Activate(ManagementHost host, AiPolicyRecord policy)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TwinDbContext>();
        db.AiPolicies.Add(policy);
        await db.SaveChangesAsync(Token);
    }
    private static CopilotModelClient Model(IServiceProvider services, HttpClient http) => new(http,
        Options.Create(new CopilotOptions { Enabled = true, Model = "fixture", Endpoint = "http://fixture/api/chat", TimeoutSeconds = 1 }),
        services.GetRequiredService<AiGovernance>());

    [Fact]
    public async Task Revoked_tools_are_denied_even_with_explicit_optimization_intent_before_engine_or_domain_access()
    {
        await using var host = await ManagementHost.CreateAsync();
        await Activate(host, await Policy(host, ["inspect_zone"]));
        using var response = await host.Client.PostAsJsonAsync($"/scenarios/{Guid.NewGuid()}/runs/{Guid.NewGuid()}/copilot/tools",
            new { intent = "optimize", zoneId = Guid.NewGuid(), minute = 0, call = new { name = "evaluate_staffing", arguments = new { } } }, Json, Token);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("tool_policy_denied", await response.Content.ReadAsStringAsync(Token));
        Assert.Null(host.Engine.LastRequest);
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TwinDbContext>();
        var events = await db.AiAgentEvents.AsNoTracking().ToArrayAsync(Token);
        var denied = Assert.Single(events!);
        Assert.Equal("tool_policy_denied", denied.Status); Assert.Equal(2, denied.PolicyVersion);
        Assert.Equal(CopilotPrompts.Version, denied.PromptVersion); Assert.Equal(CopilotPrompts.Hash, denied.PromptHash);
        Assert.NotEqual("system", denied.ActorId); Assert.NotEmpty(denied.CorrelationId);
    }

    [Fact]
    public async Task Concurrent_LLM_reservations_obey_persisted_budget_and_survive_new_scopes()
    {
        await using var host = await ManagementHost.CreateAsync();
        await Activate(host, await Policy(host, budget: 0.02m, reservation: 0.01m));
        var provider = new Provider(); using var http = new HttpClient(provider);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            await using var scope = host.Services.CreateAsyncScope(); return await Model(scope.ServiceProvider, http).SelectTopicAsync(Input, Token);
        }));
        Assert.Equal(2, provider.Calls); Assert.Equal(2, results.Count(r => r.Status == "available"));
        Assert.Equal(4, results.Count(r => r.Status == "policy_or_budget_denied"));
        await using var restarted = host.Services.CreateAsyncScope();
        Assert.Equal("policy_or_budget_denied", (await Model(restarted.ServiceProvider, http).SelectTopicAsync(Input, Token)).Status);
    }

    [Fact]
    public async Task Policy_and_agent_evidence_cannot_be_updated_deleted_or_truncated()
    {
        await using var host = await ManagementHost.CreateAsync();
        await using var scope = host.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<TwinDbContext>();
        await scope.ServiceProvider.GetRequiredService<AiGovernance>().AuthorizeToolAsync(Input.Scope.ScenarioId, Input.Scope.RunId, "inspect_zone", Token);
        foreach (var table in new[] { "ai_policies", "ai_agent_events" })
        {
            foreach (var sql in new[] { $"UPDATE twin.{table} SET \"ActorId\" = 'changed'", $"DELETE FROM twin.{table}", $"TRUNCATE twin.{table}" })
            {
                var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql, Token));
                Assert.Equal("42501", error.SqlState);
            }
        }
    }

    [Fact]
    public void Numeric_gate_rejects_fabricated_claims_and_foreign_citations_and_accepts_explicit_limitations()
    {
        var scope = Input.Scope;
        var answer = new CopilotAnswer("1.0", scope, "answered", "observed_metrics", "Queue: 7 passengers [queuedPassengers].", "available", new string('a',64),
            [new("queuedPassengers", "Queue", 7, "passengers", scope.ScenarioId, scope.RunId, scope.ZoneId, scope.Minute, DateTime.UtcNow)]);
        Assert.True(CopilotEvidencePolicy.Accepts(answer));
        Assert.False(CopilotEvidencePolicy.Accepts(answer with { Answer = "Queue: 99999 passengers." }));
        Assert.False(CopilotEvidencePolicy.Accepts(answer with { Citations = [answer.Citations[0] with { RunId = Guid.NewGuid() }] }));
        var fraction = answer with { Answer = "Utilizare: 0.5 fracție [0,1].", Citations = [answer.Citations[0] with { Value = 0.5, Unit = "fracție [0,1]" }] };
        Assert.True(CopilotEvidencePolicy.Accepts(fraction));
        Assert.False(CopilotEvidencePolicy.Accepts(fraction with { Answer = "Utilizare: 1 fracție [0,1]." }));
        Assert.True(CopilotEvidencePolicy.Accepts(answer with { Status = "insufficient_data", Answer = "Nu pot concluziona: nu există date suficiente.", Citations = [] }));
        // A prompt/vocabulary edit requires an explicit reviewed version update.
        Assert.Equal("topic-selector/1.0", CopilotPrompts.Version);
        Assert.Equal("4e94f9d6faf6874145ad75b8b5efc675f59f4d7a6412104afbaa0d0ba22da2d4", CopilotPrompts.Hash);
    }

    [Fact]
    public async Task Disabled_LLM_policy_preserves_grounded_answers_and_simulation_endpoints()
    {
        await using var host = await ManagementHost.CreateAsync();
        await Activate(host, await Policy(host, enabled: false));
        var provider = new Provider(); using var http = new HttpClient(provider);
        await using var scope = host.Services.CreateAsyncScope();
        Assert.Equal("policy_or_budget_denied", (await Model(scope.ServiceProvider, http).SelectTopicAsync(Input, Token)).Status);
        Assert.Equal(0, provider.Calls);
        using var scenes = await host.Client.GetAsync("/scenes", Token); Assert.Equal(HttpStatusCode.OK, scenes.StatusCode);
    }

    [Fact]
    public async Task Queued_workflow_pins_reviewed_versions_and_stops_when_a_tool_is_revoked_before_execution()
    {
        await using var host = await ManagementHost.CreateAsync();
        var scenario = await TenantIsolationTests.Scenario(host.Client);
        using var startRun = await host.Client.PostAsync($"/scenarios/{scenario.Scenario.Id}/runs", null, Token);
        var run = (await startRun.Content.ReadFromJsonAsync<SimulationRun>(Json, Token))!;
        Assert.True(await host.Dispatch(Token));
        var path = $"/scenarios/{scenario.Scenario.Id}/runs/{run.Id}/copilot/workflows";
        using var start = await host.Client.PostAsJsonAsync(path, new StartCopilotWorkflow(Guid.NewGuid(), "queue-diagnosis", "1.0", scenario.Inputs.StaffAllocations[0].CheckpointId, 30), Json, Token);
        Assert.True(start.StatusCode == HttpStatusCode.Accepted, await start.Content.ReadAsStringAsync(Token));
        var workflow = (await start.Content.ReadFromJsonAsync<CopilotWorkflow>(Json, Token))!;
        Assert.Equal(1, workflow.PolicyVersion); Assert.Equal(CopilotPrompts.Version, workflow.PromptVersion);
        await Activate(host, await Policy(host, []));
        await using (var scope = host.Services.CreateAsyncScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<CopilotWorkflowProcessor>().ProcessNextAsync(Token));
        var stopped = await host.Client.GetFromJsonAsync<CopilotWorkflow>($"{path}/{workflow.Id}", Json, Token);
        Assert.Equal("failed", stopped!.Status); Assert.Equal("tool_policy_denied", stopped.StopCode);
        Assert.Equal("inconclusive", stopped.Conclusion!.Status); Assert.Equal(workflow.ManifestHash, stopped.ManifestHash);
        Assert.Equal(1, stopped.PolicyVersion); Assert.All(stopped.Steps, step => Assert.Null(step.Output));
    }

    [Fact]
    public async Task Historical_policy_cannot_silently_use_a_different_prompt_after_an_upgrade()
    {
        await using var host = await ManagementHost.CreateAsync();
        await using var scope = host.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<TwinDbContext>();
        db.AiPolicies.Add(new() { Id = Guid.NewGuid(), Version = 2, RecordedAtUtc = DateTime.UtcNow,
            PromptVersion = "topic-selector/older", AllowedToolsJson = "[]", ModelEnabled = true, DailyCallLimit = 10, DailyBudgetUsd = 1, CallReservationUsd = 0.01m });
        await db.SaveChangesAsync(Token);
        var provider = new Provider(); using var http = new HttpClient(provider);
        Assert.Equal("policy_or_budget_denied", (await Model(scope.ServiceProvider, http).SelectTopicAsync(Input, Token)).Status);
        Assert.Equal(0, provider.Calls);
        Assert.True(await db.AiAgentEvents.AnyAsync(e => e.Status == "prompt_version_unavailable", Token));
    }

    [Fact]
    public async Task HTTP_Copilot_uses_governed_model_and_keeps_cited_answers_when_provider_fails_or_budget_is_spent()
    {
        var provider = new Provider("network");
        await using var host = await ManagementHost.CreateAsync(services: services =>
        {
            services.Configure<CopilotOptions>(options => { options.Enabled = true; options.Model = "fixture"; options.Endpoint = "http://fixture/api/chat"; });
            services.AddHttpClient<ICopilotModel, CopilotModelClient>().ConfigurePrimaryHttpMessageHandler(() => provider);
        });
        await Activate(host, await Policy(host, calls: 1));
        var scenario = await TenantIsolationTests.Scenario(host.Client);
        using var started = await host.Client.PostAsync($"/scenarios/{scenario.Scenario.Id}/runs", null, Token);
        var run = (await started.Content.ReadFromJsonAsync<SimulationRun>(Json, Token))!; Assert.True(await host.Dispatch(Token));
        var path = $"/scenarios/{scenario.Scenario.Id}/runs/{run.Id}/copilot";
        var question = new CopilotQuestion(scenario.Inputs.StaffAllocations[0].CheckpointId, "Câți pasageri sunt în coadă?");
        foreach (var status in new[] { "unavailable", "policy_or_budget_denied" })
        {
            using var response = await host.Client.PostAsJsonAsync(path, question, Json, Token);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Token));
            var answer = (await response.Content.ReadFromJsonAsync<CopilotAnswer>(Json, Token))!;
            Assert.Equal("answered", answer.Status); Assert.Equal(status, answer.ModelStatus);
            Assert.NotEmpty(answer.Citations); Assert.True(CopilotEvidencePolicy.Accepts(answer));
        }
        Assert.Equal(1, provider.Calls);
        using var read = await host.Client.GetAsync($"/runs/{run.Id}/replay", Token); Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TwinDbContext>();
        Assert.Equal(1, await db.AiAgentEvents.CountAsync(row => row.Kind == "llm_result" && row.Status != "available", Token));
    }

}

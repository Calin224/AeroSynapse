using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using AeroSynapse.Infrastructure.Audit;
using AeroSynapse.Infrastructure.Security;
using AeroSynapse.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace AeroSynapse.Management.Tests;

public sealed class AuditTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Scenario_incident_and_run_transitions_have_authenticated_actor_correlation_versions_and_virtual_clock()
    {
        await using var host = await ManagementHost.CreateAsync();
        host.Client.DefaultRequestHeaders.Add("X-Correlation-ID", "audit-incident-launch");
        var scenario = await host.CreateScenarioAsync();
        var incident = new ScenarioInputEvent(Guid.NewGuid(), "note", scenario.Inputs.StartsAtUtc.AddMinutes(9), null, null, null, "{\"note\":\"private-free-text\"}");
        using var changed = await host.Client.PostAsJsonAsync($"/scenarios/{scenario.Scenario.Id}/events", new EventWriteRequest(1, incident), Json, Token);
        Assert.Equal(HttpStatusCode.Created, changed.StatusCode);
        var run = await host.StartRunAsync(scenario.Scenario.Id);
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TwinDbContext>();
        var events = await db.AuditEvents.AsNoTracking().Where(row => row.ScenarioId == scenario.Scenario.Id).OrderBy(row => row.Sequence).ToArrayAsync(Token);
        var change = Assert.Single(events, row => row.Action == "scenarios.update");
        Assert.Equal("test-admin", change.Actor); Assert.NotEqual("system", change.ActorId);
        Assert.Equal("audit-incident-launch", change.CorrelationId);
        Assert.Equal(1, change.ScenarioVersionBefore); Assert.Equal(2, change.ScenarioVersionAfter);
        Assert.Equal(9, change.VirtualMinute); Assert.True(change.IsSynthetic);
        Assert.DoesNotContain("private-free-text", JsonSerializer.Serialize(events, Json));
        var transitions = events.Where(row => row.ResourceType == "runs" && row.RunId == run.Id).ToArray();
        Assert.True(transitions.Length >= 3);
        Assert.All(transitions, row => Assert.Equal("audit-incident-launch", row.CorrelationId));
        Assert.Contains(transitions, row => row.Actor == "system" && row.ChangesJson.Contains("completed"));
        Assert.All(transitions, row => Assert.Equal(2, row.ScenarioVersionAfter));
        Assert.Contains(transitions, row => row.VirtualMinute == scenario.Inputs.DurationMinutes);
        Assert.Equal(events.OrderBy(row => row.Sequence).Select(row => row.Sequence), events.Select(row => row.Sequence));
    }

    [Fact]
    public async Task Revisions_say_who_changed_what_newest_first_with_the_runs_made_on_each()
    {
        await using var host = await ManagementHost.CreateAsync();
        var scenario = await host.CreateScenarioAsync();
        var id = scenario.Scenario.Id;
        var first = await host.StartRunAsync(id);
        var flight = scenario.Inputs.Flights[0] with { Id = Guid.NewGuid(), Code = "RO301", GateId = SyntheticScenarioSeeder.Id(202) };
        using (var added = await host.Client.PostAsJsonAsync($"/scenarios/{id}/flights", new FlightWriteRequest(1, flight), Json, Token))
            Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var incident = new ScenarioInputEvent(Guid.NewGuid(), "note", scenario.Inputs.StartsAtUtc.AddMinutes(9), null, null, null, "{\"note\":\"private-free-text\"}");
        using (var added = await host.Client.PostAsJsonAsync($"/scenarios/{id}/events", new EventWriteRequest(2, incident), Json, Token))
            Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var current = (await host.Client.GetFromJsonAsync<ScenarioDetails>($"/scenarios/{id}", Json, Token))!;
        using (var renamed = await host.Client.PutAsJsonAsync($"/scenarios/{id}", new ScenarioWriteRequest(current.Scenario.Code, "Renamed",
            current.Scenario.Description, current.Scenario.SceneId, current.Scenario.LayoutVersionId, current.Inputs with { Seed = current.Inputs.Seed + 1 }, 3), Json, Token))
            Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        using (var removed = await host.Client.DeleteAsync($"/scenarios/{id}/flights/{flight.Id}?revision=4", Token))
            Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        var second = await host.StartRunAsync(id);

        var revisions = (await host.Client.GetFromJsonAsync<ScenarioRevision[]>($"/scenarios/{id}/revisions", Json, Token))!;
        Assert.Equal([5, 4, 3, 2, 1], revisions.Select(row => row.Revision));
        Assert.All(revisions, row => Assert.Equal("test-admin", row.Actor));
        Assert.Equal(revisions.Select(row => row.RecordedAtUtc).OrderDescending(), revisions.Select(row => row.RecordedAtUtc));
        Assert.Equal(new ScenarioRevisionChange("flight", "removed", [flight.Id]), Assert.Single(revisions[0].Changes), Equivalent);
        Assert.Equal(second.Id, Assert.Single(revisions[0].Runs).Id);
        Assert.Equal(["settings", "details"], revisions[1].Changes.Select(change => change.Kind));
        Assert.Equal(new ScenarioRevisionChange("event", "added", [incident.Id]), Assert.Single(revisions[2].Changes), Equivalent);
        Assert.Equal(new ScenarioRevisionChange("flight", "added", [flight.Id]), Assert.Single(revisions[3].Changes), Equivalent);
        Assert.Equal("created", Assert.Single(revisions[4].Changes).Kind);
        var run = Assert.Single(revisions[4].Runs);
        Assert.Equal((first.Id, "completed"), (run.Id, run.Status));
        Assert.Empty(revisions[1].Runs);
    }

    private static readonly IEqualityComparer<ScenarioRevisionChange> Equivalent = EqualityComparer<ScenarioRevisionChange>.Create(
        (a, b) => a!.Kind == b!.Kind && a.Action == b.Action && a.Ids.SequenceEqual(b.Ids), change => change.Kind.GetHashCode());

    [Fact]
    public async Task Rolled_back_mutations_leave_no_false_evidence_and_database_updates_deletes_and_truncate_are_refused()
    {
        await using var host = await ManagementHost.CreateAsync();
        var scenario = await host.CreateScenarioAsync();
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TwinDbContext>();
        var before = await db.AuditEvents.CountAsync(Token);
        await using (var transaction = await db.Database.BeginTransactionAsync(Token))
        {
            await db.Scenarios.Where(row => row.Id == scenario.Scenario.Id).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Name, "rolled-back"), Token);
            Assert.Equal(before + 1, await db.AuditEvents.CountAsync(Token));
            await transaction.RollbackAsync(Token);
        }
        Assert.Equal(before, await db.AuditEvents.CountAsync(Token));
        var entry = await db.AuditEvents.OrderBy(row => row.Sequence).FirstAsync(Token);
        var update = await Assert.ThrowsAsync<PostgresException>(() => db.AuditEvents.Where(row => row.Sequence == entry.Sequence)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Actor, "forged"), Token));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, update.SqlState);
        await Assert.ThrowsAsync<PostgresException>(() => db.AuditEvents.Where(row => row.Sequence == entry.Sequence).ExecuteDeleteAsync(Token));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("TRUNCATE twin.audit_events", Token));
        Assert.Equal(before, await db.AuditEvents.CountAsync(Token));
    }

    [Fact]
    public async Task Unreferenced_scenario_purge_keeps_evidence_and_retention_can_only_expire_old_rows()
    {
        await using var host = await ManagementHost.CreateAsync();
        var scenario = await host.CreateScenarioAsync();
        await using var scope = host.Services.CreateAsyncScope();
        var run = await scope.ServiceProvider.GetRequiredService<IScenarioService>().CreateRunAsync(scenario.Scenario.Id, Token);
        var storage = scope.ServiceProvider.GetRequiredService<IRunStorageService>();
        await storage.TransitionAsync(run.Id, new("running", run.Revision), Token);
        var failed = await storage.TransitionAsync(run.Id, new("failed", run.Revision + 1, "engine rejected api_key='private-failure-key'"), Token);
        Assert.DoesNotContain("private-failure-key", failed.FailureReason!);
        Assert.Contains("REDACTED", failed.FailureReason!);
        using var purge = await host.Client.DeleteAsync($"/scenarios/{scenario.Scenario.Id}?retention=purge&revision=1", Token);
        Assert.True(purge.IsSuccessStatusCode);
        var db = scope.ServiceProvider.GetRequiredService<TwinDbContext>();
        Assert.Contains(await db.AuditEvents.Where(row => row.ScenarioId == scenario.Scenario.Id).ToArrayAsync(Token), row => row.Action == "scenarios.delete");
        var deletedRun = await db.AuditEvents.SingleAsync(row => row.RunId == run.Id && row.Action == "runs.delete", Token);
        Assert.Equal(scenario.Scenario.Id, deletedRun.ScenarioId);
        Assert.Equal(1, deletedRun.ScenarioVersionAfter);
        var oldId = Guid.NewGuid();
        db.AuditEvents.Add(new AuditEvent { Id = oldId, ResourceId = oldId, ResourceType = "retention-fixture", Action = "fixture.created",
            RecordedAtUtc = DateTime.UtcNow.AddDays(-100), CorrelationId = "retention-test", IsSynthetic = true });
        await db.SaveChangesAsync(Token);
        var before = await db.AuditEvents.CountAsync(Token);
        await db.Database.ExecuteSqlRawAsync("SELECT twin.audit_expire(90)", Token);
        Assert.False(await db.AuditEvents.AnyAsync(row => row.Id == oldId, Token));
        Assert.Equal(before - 1, await db.AuditEvents.CountAsync(Token));
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("SELECT twin.audit_expire(1)", Token));
    }

    [Theory]
    [InlineData("{\"code\":\"one\",\"Code\":\"two\"}")]
    [InlineData("{\"unknown\":true}")]
    [InlineData("{\"code\":")]
    public async Task Unknown_duplicate_and_invalid_JSON_cannot_mutate_or_disclose_the_payload(string body)
    {
        await using var host = await ManagementHost.CreateAsync();
        using var result = await host.Client.PostAsync("/scenarios", new StringContent(body, Encoding.UTF8, "application/json"), Token);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.DoesNotContain(body, await result.Content.ReadAsStringAsync(Token));
    }

    [Fact]
    public async Task Request_size_and_write_rate_limits_are_enforced_with_codes_and_no_queued_API_work()
    {
        await using var host = await ManagementHost.CreateAsync(services: services => services.PostConfigure<AeroSynapse.API.Security.OperationalSecurityOptions>(options =>
        { options.WritePerMinute = 3; options.MaxJsonBytes = 1024; }));
        // Account bootstrap performs one login write. The oversized command is rejected before binding.
        using var oversized = await host.Client.PostAsync("/scenarios", new StringContent("{\"code\":\"" + new string('x', 1100) + "\"}", Encoding.UTF8, "application/json"), Token);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        using var rejected = await host.Client.PostAsJsonAsync("/scenarios", new { }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var limited = await host.Client.PostAsJsonAsync("/scenarios", new { }, Token);
        // Authenticated commands use a separate partition from anonymous login.
        if (limited.StatusCode != HttpStatusCode.TooManyRequests)
        {
            using var fourth = await host.Client.PostAsJsonAsync("/scenarios", new { }, Token);
            Assert.Equal(HttpStatusCode.TooManyRequests, fourth.StatusCode);
            Assert.Equal("rate_limited", (await fourth.Content.ReadFromJsonAsync<JsonElement>(Json, Token)).GetProperty("code").GetString());
            Assert.NotNull(fourth.Headers.RetryAfter);
        }
    }

    [Fact]
    public void Redaction_removes_configured_secrets_assignments_tokens_and_Bearer_values()
    {
        var secret = "configured-private-material-123";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Copilot:ApiKey"] = secret }).Build();
        var redactor = new SecretRedactor(configuration);
        var result = redactor.Redact($"upstream {secret}; Password=database-password; token=opaque-token; Authorization: Bearer abcdef123456; api_key='private-key'");
        foreach (var value in new[] { secret, "database-password", "opaque-token", "abcdef123456", "private-key" }) Assert.DoesNotContain(value, result);
    }

    [Fact]
    public async Task Upstream_validation_errors_redact_rejected_input_before_it_reaches_the_browser()
    {
        await using var host = await ManagementHost.CreateAsync(services: services =>
            services.AddHttpClient("BrowserIntelligence").ConfigurePrimaryHttpMessageHandler(() => new TaintedTransport()));
        using var result = await host.Client.PostAsJsonAsync("/intelligence/baggage/v1/run", new { seed = 42 }, Token);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, result.StatusCode);
        var text = await result.Content.ReadAsStringAsync(Token);
        Assert.DoesNotContain("upstream-private-input", text);
        Assert.Contains("REDACTED", text);
        using var json = JsonDocument.Parse(text);
        Assert.Equal("invalid value", json.RootElement.GetProperty("detail")[0].GetProperty("msg").GetString());
    }

    [Fact]
    public async Task Log_providers_receive_redacted_state_scopes_and_exception_diagnostics()
    {
        var capture = new CaptureProvider();
        await using var host = await ManagementHost.CreateAsync(services: services => services.AddSingleton<ILoggerProvider>(capture));
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SecurityProbe");
        using (logger.BeginScope(new Dictionary<string, object?> { ["Password"] = "scope-secret" }))
            logger.LogError(new InvalidOperationException("raw-exception-secret"), "Probe password={Password} bearer={Value}", "field-secret", "Bearer private-authorization");
        var output = string.Join("\n", capture.Entries);
        foreach (var value in new[] { "scope-secret", "field-secret", "raw-exception-secret", "private-authorization" }) Assert.DoesNotContain(value, output);
        Assert.Contains("REDACTED", output);
    }
    private sealed class CaptureProvider : ILoggerProvider
    {
        public List<string> Entries { get; } = [];
        public ILogger CreateLogger(string category) => new Capture(Entries);
        public void Dispose() { }
        private sealed class Capture(List<string> entries) : ILogger
        {
            public bool IsEnabled(LogLevel level) => true;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull { entries.Add(JsonSerializer.Serialize(state)); return null; }
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter) => entries.Add(formatter(state, error) + JsonSerializer.Serialize(state) + error);
        }
    }
    private sealed class TaintedTransport : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.UnprocessableEntity) { Content = new StringContent("{\"detail\":[{\"input\":\"upstream-private-input\",\"msg\":\"invalid value\",\"loc\":[\"body\",\"seed\"]}]}", Encoding.UTF8, "application/json") });
    }
}

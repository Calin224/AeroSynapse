using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using AeroSynapse.API.Identity;
using AeroSynapse.API.Operations;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using AeroSynapse.Infrastructure.Governance;
using AeroSynapse.Infrastructure.Identity;
using AeroSynapse.Infrastructure.Storage;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AeroSynapse.Management.Tests;

public sealed class TenantIsolationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static async Task<ScenarioDetails> Scenario(HttpClient client)
    {
        using var publish = await client.PostAsync($"/scenes/{SyntheticScenarioSeeder.SceneId}/layout-versions", null, Token);
        publish.EnsureSuccessStatusCode();
        var layout = (await publish.Content.ReadFromJsonAsync<PublishedLayout>(Json, Token))!;
        var request = JsonSerializer.Deserialize<ScenarioWriteRequest>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scenario-request.json")), Json)!;
        using var response = await client.PostAsJsonAsync("/scenarios", request with { LayoutVersionId = layout.Version.Id }, Json, Token);
        response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync<ScenarioDetails>(Json, Token))!;
    }
    private static async Task<SimulationRun> Run(HttpClient client, ScenarioDetails scenario)
    {
        using var response = await client.PostAsync($"/scenarios/{scenario.Scenario.Id}/runs", null, Token);
        response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync<SimulationRun>(Json, Token))!;
    }

    [Fact]
    public async Task Foreign_scenario_run_layout_and_audit_IDs_do_not_cross_tenant_databases()
    {
        await using var host = await ManagementHost.CreateAsync(multiTenant: true);
        using var other = await host.SignInAsync("Admin", tenantId: "other");
        var own = await Scenario(host.Client); var foreign = await Scenario(other);
        var run = await Run(host.Client, own); Assert.True(await host.Dispatch(Token));
        other.DefaultRequestHeaders.Add("X-Tenant-ID", "demo"); // Never routing authority.
        foreach (var path in new[] { $"/scenarios/{own.Scenario.Id}", $"/runs/{run.Id}", $"/runs/{run.Id}/metrics", $"/runs/{run.Id}/snapshots", $"/runs/{run.Id}/replay",
            $"/scenarios/{own.Scenario.Id}/operations", $"/scenes/{SyntheticScenarioSeeder.SceneId}/layout-versions/{own.Scenario.LayoutVersionId}",
            $"/scenarios/{own.Scenario.Id}/runs/{run.Id}/copilot/context" })
        {
            using var denied = await other.GetAsync(path, Token);
            Assert.True(denied.StatusCode == HttpStatusCode.NotFound, path + " => " + denied.StatusCode + " " + await denied.Content.ReadAsStringAsync(Token));
        }
        var scenarios = await other.GetFromJsonAsync<ScenarioSummary[]>("/scenarios", Json, Token);
        Assert.Equal(foreign.Scenario.Id, Assert.Single(scenarios!).Id);
        using (TenantContext.Begin("other"))
        {
            await using var scope = host.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TwinDbContext>();
            Assert.False(await db.AuditEvents.AnyAsync(row => row.ScenarioId == own.Scenario.Id, Token));
        }
        using var mutation = await other.PostAsJsonAsync($"/scenarios/{own.Scenario.Id}/events", new EventWriteRequest(1,
            new(Guid.NewGuid(), "note", own.Inputs.StartsAtUtc, null, null, null, "{}")), Json, Token);
        Assert.Equal(HttpStatusCode.NotFound, mutation.StatusCode);
        var session = await other.GetFromJsonAsync<SessionView>("/auth/session", Json, Token); Assert.Equal("other", session!.TenantId);
        var unchanged = await host.Client.GetFromJsonAsync<ScenarioDetails>($"/scenarios/{own.Scenario.Id}", Json, Token);
        Assert.Equal(own.Scenario.Revision, unchanged!.Scenario.Revision);
    }

    [Fact]
    public async Task Worker_scopes_execute_runs_in_both_tenants_and_policies_and_usage_remain_independent()
    {
        await using var host = await ManagementHost.CreateAsync(worker: true, multiTenant: true);
        using var other = await host.SignInAsync("Admin", tenantId: "other");
        var a = await Scenario(host.Client); var b = await Scenario(other);
        var first = await Run(host.Client, a); var second = await Run(other, b);
        foreach (var (client, run) in new[] { (host.Client, first), (other, second) })
        {
            JsonElement result = default;
            for (var i = 0; i < 80; i++)
            {
                result = await client.GetFromJsonAsync<JsonElement>($"/runs/{run.Id}", Json, Token);
                if (result.GetProperty("status").GetString() == "completed") break;
                await Task.Delay(100, Token);
            }
            Assert.True(result.GetProperty("status").GetString() == "completed", result.GetRawText());
        }
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TwinDbContext>();
            var current = await db.AiPolicies.OrderByDescending(row => row.Version).FirstAsync(Token);
            db.AiPolicies.Add(new() { Id = Guid.NewGuid(), Version = current.Version + 1, RecordedAtUtc = DateTime.UtcNow,
                PromptVersion = CopilotPrompts.Version, AllowedToolsJson = "[]", ModelEnabled = false,
                DailyCallLimit = 0, DailyBudgetUsd = 0, CallReservationUsd = 0.01m });
            await db.SaveChangesAsync(Token);
        }
        using (TenantContext.Begin("other"))
        {
            await using var scope = host.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TwinDbContext>();
            Assert.True((await db.AiPolicies.OrderByDescending(row => row.Version).FirstAsync(Token)).ModelEnabled);
            Assert.Equal("completed", (await db.Runs.SingleAsync(r => r.Id == second.Id, Token)).Status);
            Assert.False(await db.Runs.AnyAsync(r => r.Id == first.Id, Token));
            Assert.False(await db.AuditEvents.AnyAsync(e => e.RunId == first.Id, Token));
        }
    }

    [Fact]
    public async Task Changing_membership_revokes_existing_sessions_and_unknown_tenants_cannot_login()
    {
        await using var host = await ManagementHost.CreateAsync(multiTenant: true);
        using var user = await host.SignInAsync("Viewer", "moving-user");
        await using var scope = host.Services.CreateAsyncScope(); var identity = scope.ServiceProvider.GetRequiredService<IdentityStore>();
        await identity.Users.Where(u => u.UserName == "moving-user").ExecuteUpdateAsync(set => set.SetProperty(u => u.TenantId, "other"), Token);
        using var revoked = await user.GetAsync("/scenarios", Token); Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        await Assert.ThrowsAsync<HttpRequestException>(() => host.SignInAsync("Viewer", tenantId: "unregistered"));
    }

    [Fact]
    public async Task Map_files_isolate_even_identical_airport_IDs_and_engine_clients_route_to_their_own_endpoint()
    {
        await using var host = await ManagementHost.CreateAsync(multiTenant: true);
        using var other = await host.SignInAsync("Viewer", tenantId: "other");
        var id = Guid.NewGuid();
        foreach (var tenantId in new[] { "demo", "other" })
        {
            using var tenant = TenantContext.Begin(tenantId);
            await using var scope = host.Services.CreateAsyncScope();
            var maps = scope.ServiceProvider.GetRequiredService<AeroSynapse.Infrastructure.Airports.AirportMapDbContext>();
            maps.Airports.Add(new() { Id = id, Name = "Map for " + tenantId });
            await maps.SaveChangesAsync(Token);
            var client = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>().CreateClient("BrowserIntelligence");
            Assert.Equal($"http://engine-{tenantId}:8000/", client.BaseAddress!.AbsoluteUri);
        }
        var first = await host.Client.GetStringAsync($"/api/airports/{id}/snapshot", Token);
        var second = await other.GetStringAsync($"/api/airports/{id}/snapshot", Token);
        Assert.Contains("Map for demo", first); Assert.DoesNotContain("Map for other", first);
        Assert.Contains("Map for other", second); Assert.DoesNotContain("Map for demo", second);
    }

    [Fact]
    public async Task SignalR_rejects_foreign_scenario_and_partitions_identical_demo_IDs()
    {
        await using var host = await ManagementHost.CreateAsync(multiTenant: true);
        var scenario = await Scenario(host.Client);
        using var other = await host.SignInAsync("Admin", tenantId: "other");
        var ownOther = await Scenario(other);
        await using var hub = new HubConnectionBuilder().WithUrl("http://localhost/hubs/operations", options =>
        {
            options.Transports = HttpTransportType.LongPolling;
            options.HttpMessageHandlerFactory = _ => new TenantCookies(other, host.AnonymousHandler());
        }).Build();
        await hub.StartAsync(Token);
        await hub.InvokeAsync("SubscribeScenario", ownOther.Scenario.Id, Token);
        await Task.Delay(260, Token);
        var error = await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync("SubscribeScenario", scenario.Scenario.Id, Token));
        Assert.Contains("scenario_access_denied", error.Message);
        var subscriptions = host.Services.GetRequiredService<ScenarioSubscriptions>();
        using (TenantContext.Begin("demo")) Assert.Empty(subscriptions.Active());
        using (TenantContext.Begin("other")) Assert.Single(subscriptions.Active());
    }

    private sealed class TenantCookies(HttpClient browser, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            // The factory's browser handler owns its cookie jar; ask it to send
            // negotiate/poll requests directly so no test-only auth bypass exists.
            using var copy = new HttpRequestMessage(request.Method, new Uri(browser.BaseAddress!, request.RequestUri!.PathAndQuery));
            foreach (var header in request.Headers) copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
            if (request.Content is not null) copy.Content = new ByteArrayContent(await request.Content.ReadAsByteArrayAsync(token));
            return await browser.SendAsync(copy, HttpCompletionOption.ResponseHeadersRead, token);
        }
    }

    [Fact]
    public void Server_registry_rejects_shared_storage_and_engine_endpoints_instead_of_falling_back()
    {
        var settings = new Dictionary<string, string?> {
            ["Tenancy:Tenants:demo:Database"] = "airport_a", ["Tenancy:Tenants:demo:AirportMaps"] = "Data Source=a.db", ["Tenancy:Tenants:demo:IntelligenceBaseUrl"] = "http://engine-a:8000",
            ["Tenancy:Tenants:other:Database"] = "airport_a", ["Tenancy:Tenants:other:AirportMaps"] = "Data Source=b.db", ["Tenancy:Tenants:other:IntelligenceBaseUrl"] = "http://engine-b:8000" };
        Assert.Throws<InvalidOperationException>(() => new TenantCatalog(new ConfigurationBuilder().AddInMemoryCollection(settings).Build()));
        settings["Tenancy:Tenants:other:Database"] = "airport_b"; settings["Tenancy:Tenants:other:AirportMaps"] = "Data Source=./a.db";
        Assert.Throws<InvalidOperationException>(() => new TenantCatalog(new ConfigurationBuilder().AddInMemoryCollection(settings).Build()));
        settings["Tenancy:Tenants:other:AirportMaps"] = "Data Source=b.db"; settings["Tenancy:Tenants:other:IntelligenceBaseUrl"] = "http://engine-a:8000/";
        Assert.Throws<InvalidOperationException>(() => new TenantCatalog(new ConfigurationBuilder().AddInMemoryCollection(settings).Build()));
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using AeroSynapse.Infrastructure.Identity;
using AeroSynapse.Infrastructure.Storage;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AeroSynapse.Management.Tests;

public sealed class IdentityTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Anonymous_requests_are_401_roleless_requests_are_403_and_public_probes_stay_available()
    {
        await using var host = await ManagementHost.CreateAsync();
        using var anonymous = host.AnonymousClient();
        foreach (var path in new[] { "/scenarios", "/scenes", "/runs/" + Guid.NewGuid(), "/api/airports" })
        {
            using var response = await anonymous.GetAsync(path, Token);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Null(response.Headers.Location);
        }
        using var write = await anonymous.PostAsJsonAsync($"/scenarios/{Guid.NewGuid()}/events", new { }, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, write.StatusCode);
        using var health = await anonymous.GetAsync("/health", Token);
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        using var roleless = await host.SignInAsync(null);
        using var denied = await roleless.GetAsync("/scenarios", Token);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task Retired_exploration_commands_and_contracts_are_not_exposed()
    {
        await using var host = await ManagementHost.CreateAsync();
        foreach (var path in new[] { "/passenger-feedback", "/intelligence/evacuation/v1/run", "/contracts/v1/evacuation/validate" })
        {
            using var response = await host.Client.PostAsJsonAsync(path, new { }, Token);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        using var schema = await host.Client.GetAsync("/contracts/v1/schemas/EvacuationRequest", Token);
        Assert.Equal(HttpStatusCode.NotFound, schema.StatusCode);
    }

    [Fact]
    public async Task Viewer_reads_real_scenario_and_results_but_cannot_create_incidents_or_approve()
    {
        await using var host = await ManagementHost.CreateAsync();
        var scenario = await Scenario(host);
        using var viewer = await host.SignInAsync("Viewer");
        using var read = await viewer.GetAsync($"/scenarios/{scenario.Scenario.Id}", Token);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(scenario.Scenario.Id, (await read.Content.ReadFromJsonAsync<ScenarioDetails>(Json, Token))!.Scenario.Id);
        foreach (var path in new[] { $"/scenarios/{scenario.Scenario.Id}/events", $"/scenarios/{scenario.Scenario.Id}/runs", $"/recommendation-reviews/{Guid.NewGuid()}/approve" })
        {
            using var forbidden = await viewer.PostAsJsonAsync(path, new { }, Token);
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }
    }

    [Fact]
    public async Task Operator_can_persist_an_incident()
    {
        await using var host = await ManagementHost.CreateAsync();
        var scenario = await Scenario(host);
        using var actor = await host.SignInAsync("Operator");
        var note = new ScenarioInputEvent(Guid.NewGuid(), "note", scenario.Inputs.StartsAtUtc.AddMinutes(2), null, null, null, "{}");
        using var created = await actor.PostAsJsonAsync($"/scenarios/{scenario.Scenario.Id}/events", new EventWriteRequest(scenario.Scenario.Revision, note), Json, Token);
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Token));
        var details = await actor.GetFromJsonAsync<ScenarioDetails>($"/scenarios/{scenario.Scenario.Id}", Json, Token);
        Assert.Contains(details!.Inputs.Events, item => item.Id == note.Id);
    }

    [Fact]
    public async Task Missing_and_forged_CSRF_are_refused_before_a_write_or_login()
    {
        await using var host = await ManagementHost.CreateAsync();
        host.Client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        using var missing = await host.Client.PostAsync($"/scenarios/{Guid.NewGuid()}/runs", null, Token);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal("csrf_invalid", (await missing.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("code").GetString());
        host.Client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", "forged");
        using var forged = await host.Client.PostAsync("/auth/logout", null, Token);
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        using var anonymous = host.AnonymousClient();
        using var login = await anonymous.PostAsJsonAsync("/auth/login", new { username = "nobody", password = Guid.NewGuid().ToString() }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, login.StatusCode);
    }

    [Fact]
    public async Task Session_logout_and_security_stamp_changes_revoke_persisted_access()
    {
        await using var host = await ManagementHost.CreateAsync();
        using var viewer = await host.SignInAsync("Viewer", "viewer-test");
        await using var scope = host.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<WorkspaceUser>>();
        var target = (await users.FindByNameAsync("viewer-test"))!;
        Assert.True((await users.UpdateSecurityStampAsync(target)).Succeeded);
        using var oldSession = await viewer.GetAsync("/scenarios", Token);
        Assert.Equal(HttpStatusCode.Unauthorized, oldSession.StatusCode);
        using var logout = await host.Client.PostAsync("/auth/logout", null, Token);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        using var after = await host.Client.GetAsync("/scenarios", Token);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task Expiry_is_enforced_on_the_server_without_waiting_for_the_browser()
    {
        var clock = new SessionClock();
        await using var host = await ManagementHost.CreateAsync(services: services => services.AddSingleton<TimeProvider>(clock));
        using var initial = await host.Client.GetAsync("/scenarios", Token);
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        clock.Now = clock.Now.AddMinutes(31);
        using var expired = await host.Client.GetAsync("/scenarios", Token);
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
    }

    [Fact]
    public async Task Login_lockout_does_not_expose_account_existence_or_password_hashes()
    {
        await using var host = await ManagementHost.CreateAsync();
        using var known = await host.SignInAsync("Viewer", "lockout-test");
        using var anonymous = host.AnonymousClient();
        var csrf = await anonymous.GetFromJsonAsync<JsonElement>("/auth/csrf", Token);
        anonymous.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf.GetProperty("requestToken").GetString());
        for (var attempt = 0; attempt < 6; attempt++)
        {
            using var rejected = await anonymous.PostAsJsonAsync("/auth/login", new { username = "lockout-test", password = Guid.NewGuid().ToString() }, Token);
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
            Assert.Equal("login_failed", (await rejected.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("code").GetString());
        }
        await using var scope = host.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<WorkspaceUser>>();
        Assert.True(await users.IsLockedOutAsync((await users.FindByNameAsync("lockout-test"))!));
        using var session = await host.Client.GetAsync("/auth/session", Token);
        var body = await session.Content.ReadAsStringAsync(Token);
        Assert.DoesNotContain("passwordHash", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("securityStamp", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cookie_flags_follow_HTTPS_and_local_HTTP_context_and_cookies_contain_no_browser_readable_ticket()
    {
        await using var host = await ManagementHost.CreateAsync();
        await using var scope = host.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<WorkspaceUser>>();
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) + "aA1!";
        var user = new WorkspaceUser { UserName = "cookie-test" };
        Assert.True((await users.CreateAsync(user, password)).Succeeded);
        await users.AddToRoleAsync(user, "Viewer");
        foreach (var scheme in new[] { "http", "https" })
        {
            using var browser = host.AnonymousClient(); browser.BaseAddress = new Uri(scheme + "://localhost");
            var csrf = await browser.GetFromJsonAsync<JsonElement>("/auth/csrf", Token);
            browser.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf.GetProperty("requestToken").GetString());
            using var reply = await browser.PostAsJsonAsync("/auth/login", new { username = user.UserName, password }, Token);
            Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
            var cookie = Assert.Single(reply.Headers.GetValues("Set-Cookie"), value => value.StartsWith("AeroSynapse.Session="));
            Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(scheme == "https", cookie.Contains("; secure", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain("expires=", cookie, StringComparison.OrdinalIgnoreCase);
            var body = await reply.Content.ReadAsStringAsync(Token);
            Assert.DoesNotContain(password, body); Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);
            var expires = (await reply.Content.ReadFromJsonAsync<JsonElement>(Token)).GetProperty("expiresAtUtc").GetDateTime();
            Assert.InRange(expires - DateTime.UtcNow, TimeSpan.FromMinutes(29), TimeSpan.FromMinutes(31));
        }
    }

    [Fact]
    public async Task Production_always_requires_Secure_host_cookies_and_does_not_expose_configuration_secrets()
    {
        await using var host = await ManagementHost.CreateAsync(environment: "Production");
        var options = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);
        Assert.Equal("__Host-AeroSynapse.Session", options.Cookie.Name);
        Assert.Equal(Microsoft.AspNetCore.Http.CookieSecurePolicy.Always, options.Cookie.SecurePolicy);
        Assert.True(options.Cookie.HttpOnly);
        Assert.Null(options.Cookie.Domain);
        var csrf = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Antiforgery.AntiforgeryOptions>>().Value;
        Assert.Equal("__Host-AeroSynapse.Csrf", csrf.Cookie.Name);
        Assert.Equal(Microsoft.AspNetCore.Http.CookieSecurePolicy.Always, csrf.Cookie.SecurePolicy);
        using var read = await host.Client.GetAsync("/scenarios", Token);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    [Theory]
    [InlineData("baggage/v1/run")]
    [InlineData("airside/v1/run")]
    [InlineData("airside/v1/optimize")]
    [InlineData("energy/v1/compare")]
    [InlineData("energy/v1/optimize")]
    public async Task Browser_FastAPI_access_requires_Operator_and_CSRF_and_does_not_relay_arbitrary_engine_endpoints(string path)
    {
        await using var host = await ManagementHost.CreateAsync();
        using var anonymous = host.AnonymousClient();
        using var unauthenticated = await anonymous.PostAsJsonAsync("/intelligence/" + path, new { }, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        using var viewer = await host.SignInAsync("Viewer");
        using var forbidden = await viewer.PostAsJsonAsync("/intelligence/" + path, new { }, Token);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        host.Client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        using var csrf = await host.Client.PostAsJsonAsync("/intelligence/" + path, new { }, Token);
        Assert.Equal(HttpStatusCode.BadRequest, csrf.StatusCode);
        using var notRelayed = await host.Client.GetAsync("/intelligence/simulations", Token);
        Assert.Equal(HttpStatusCode.NotFound, notRelayed.StatusCode);
    }

    [Theory]
    [InlineData("baggage/v1/run")]
    [InlineData("airside/v1/run")]
    [InlineData("airside/v1/optimize")]
    [InlineData("energy/v1/compare")]
    [InlineData("energy/v1/optimize")]
    public async Task Demo_relay_preserves_JSON_status_and_audit_without_forwarding_workspace_credentials(string path)
    {
        var transport = new RelayTransport();
        await using var host = await ManagementHost.CreateAsync(services: services =>
            services.AddHttpClient("BrowserIntelligence").ConfigurePrimaryHttpMessageHandler(() => transport));
        using var response = await host.Client.PostAsJsonAsync("/intelligence/" + path, new { seed = 42 }, Token);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("{\"status\":\"pending\"}", await response.Content.ReadAsStringAsync(Token));
        Assert.Equal("{\"seed\":42}", transport.Body);
        Assert.False(transport.ForwardedCredentials);
        Assert.NotNull(transport.CorrelationId);
        Assert.Equal("/" + path, transport.Path);
        var auditId = Guid.Parse(response.Headers.GetValues("X-Audit-ID").Single());
        await using var scope = host.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<TwinDbContext>();
        var evidence = await database.AuditEvents.SingleAsync(row => row.Id == auditId, Token);
        Assert.Equal(path.EndsWith("optimize", StringComparison.Ordinal) ? "optimization.calculated" : "simulation.calculated", evidence.Action);
        Assert.Equal(transport.CorrelationId, evidence.CorrelationId);
        Assert.Equal("test-admin", evidence.Actor);
    }

    [Fact]
    public async Task SignalR_requires_authentication_checks_the_requested_scenario_and_rejects_foreign_origins()
    {
        await using var host = await ManagementHost.CreateAsync();
        using var anonymous = host.AnonymousClient();
        using var rejected = await anonymous.PostAsync("/hubs/operations/negotiate?negotiateVersion=1", null, Token);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        using var foreign = new HttpRequestMessage(HttpMethod.Post, "/hubs/operations/negotiate?negotiateVersion=1");
        foreign.Headers.Add("Origin", "https://untrusted.invalid");
        using var origin = await host.Client.SendAsync(foreign, Token);
        Assert.Equal(HttpStatusCode.Forbidden, origin.StatusCode);
        var scenario = await Scenario(host);
        await using var hub = Connect(host);
        await hub.StartAsync(Token);
        await hub.InvokeAsync("SubscribeScenario", scenario.Scenario.Id, Token);
        await Task.Delay(260, Token);
        var error = await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync("SubscribeScenario", Guid.NewGuid(), Token));
        Assert.Contains("scenario_access_denied", error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Open_SignalR_connections_are_closed_after_logout_or_session_expiry(bool expire)
    {
        var clock = new SessionClock();
        await using var host = await ManagementHost.CreateAsync(services: services => services.AddSingleton<TimeProvider>(clock));
        var scenario = await Scenario(host);
        await using var hub = Connect(host);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
        await hub.StartAsync(Token);
        await hub.InvokeAsync("SubscribeScenario", scenario.Scenario.Id, Token);
        if (expire) clock.Now = clock.Now.AddMinutes(31);
        else { using var logout = await host.Client.PostAsync("/auth/logout", null, Token); logout.EnsureSuccessStatusCode(); }
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(6), Token);
        Assert.Equal(HubConnectionState.Disconnected, hub.State);
    }

    private static HubConnection Connect(ManagementHost host) => new HubConnectionBuilder().WithUrl("http://localhost/hubs/operations", options =>
    {
        options.Transports = HttpTransportType.LongPolling;
        options.HttpMessageHandlerFactory = _ => host.HubHandler();
    }).Build();

    private static async Task<ScenarioDetails> Scenario(ManagementHost host)
    {
        using var publish = await host.Client.PostAsync($"/scenes/{SyntheticScenarioSeeder.SceneId}/layout-versions", null, Token);
        publish.EnsureSuccessStatusCode();
        var layout = (await publish.Content.ReadFromJsonAsync<PublishedLayout>(Json, Token))!;
        var request = JsonSerializer.Deserialize<ScenarioWriteRequest>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scenario-request.json")), Json)!;
        using var save = await host.Client.PostAsJsonAsync("/scenarios", request with { LayoutVersionId = layout.Version.Id }, Json, Token);
        save.EnsureSuccessStatusCode();
        return (await save.Content.ReadFromJsonAsync<ScenarioDetails>(Json, Token))!;
    }

    private sealed class SessionClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class RelayTransport : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public string? Body { get; private set; }
        public string? CorrelationId { get; private set; }
        public bool ForwardedCredentials { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Path = request.RequestUri!.AbsolutePath;
            Body = await request.Content!.ReadAsStringAsync(token);
            ForwardedCredentials = request.Headers.Contains("Cookie") || request.Headers.Contains("Authorization") || request.Headers.Contains("X-CSRF-TOKEN");
            CorrelationId = request.Headers.TryGetValues("X-Correlation-ID", out var values) ? values.Single() : null;
            return new(HttpStatusCode.Accepted) { Content = new StringContent("{\"status\":\"pending\"}", System.Text.Encoding.UTF8, "application/json") };
        }
    }
}

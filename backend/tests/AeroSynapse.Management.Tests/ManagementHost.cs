using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Management;
using AeroSynapse.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using AeroSynapse.Infrastructure.Storage;
using AeroSynapse.Infrastructure.Airports;
using Npgsql;
using AeroSynapse.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using System.Security.Cryptography;
using System.Net.Http.Json;
using AeroSynapse.API.Identity;
using AeroSynapse.Infrastructure.Governance;

namespace AeroSynapse.Management.Tests;

internal sealed class RecordingEngine : ISimulationEngine
{
    public SimulationRequest? LastRequest { get; private set; }
    public AeroSynapse.Application.Simulation.EngineDiagnostics? Diagnostics { get; private set; }
    public string Build { get; set; } = new string('c', 64);
    public Func<SimulationRequest, CancellationToken, Task<SimulationResponse>>? Behavior { get; set; }
    public async Task<SimulationResponse> ExecuteAsync(SimulationRequest request, CancellationToken token)
    {
        LastRequest = request;
        Diagnostics = null;
        var response = Behavior is not null ? await Behavior(request, token) : Response(request);
        Diagnostics = new(RunFingerprint.Input(request), response.Status == "completed" ? RunFingerprint.Result(response) : null,
            RunFingerprint.Engine(request), Build, 2.5);
        return response;
    }
    public static SimulationResponse Response(SimulationRequest request)
    {
        var zone = request.Checkpoints[0].CheckpointId;
        if (request.Engine is not null)
        {
            var report = new ProcessResult("1.0", request.Engine, request.PassengerGeneration is null ? "aggregate" : "flight_profiles", request.DurationMinutes,
                request.PassengerCount, 0, request.PassengerCount, 0, 0, 0, 0, 0, 0,
                request.Checkpoints.Select(cp => new ProcessCheckpoint(cp.CheckpointId, cp.StaffCount, 0, 0, 0, 0, 0, 0, 0)).ToArray(), [], 0, 0, false);
            return new("1.0", request.RunId, "completed", request.DurationMinutes,
                request.Checkpoints.Select(cp => new SimulationMetric(cp.CheckpointId, request.StartsAtUtc.AddMinutes(request.DurationMinutes), 0, 0, 0)).ToArray(),
                request.Checkpoints.Select(cp => new ZoneSnapshot("1.0", cp.CheckpointId, request.DurationMinutes, 0, 0)).ToArray(), null, ProcessResult: report,
                KpiResult: request.Engine == KpiConventions.Engine ? KpiFixture.EmptyObserved(request) : null);
        }
        return new SimulationResponse("1.0", request.RunId, "completed", request.DurationMinutes,
            [new(zone, request.StartsAtUtc.AddMinutes(request.DurationMinutes), 2.5, 1, 60)],
            [new("1.0", zone, request.DurationMinutes, 5, 60)], null);
    }
}

internal sealed class ManagementHost : IAsyncDisposable
{
    private readonly PostgresSettings settings = PostgresSettings.FromEnvironment();
    private readonly string name = "aerosynapse_management_test_" + Guid.NewGuid().ToString("N");
    private readonly string sqlite = Path.Combine(Path.GetTempPath(), "aerosynapse-management-" + Guid.NewGuid().ToString("N") + ".db");
    private WebApplicationFactory<Program> factory = null!;
    private string environmentName = "Testing";
    private bool multiTenant;
    private string OtherDatabase => name[..50] + "_other";
    public RecordingEngine Engine { get; } = new();
    public HttpClient Client { get; private set; } = null!;
    public IServiceProvider Services => factory.Services;
    public string CookieHeader { get; private set; } = "";
    public HttpMessageHandler HubHandler() => new CookieHandler(CookieHeader) { InnerHandler = factory.Server.CreateHandler() };
    public HttpMessageHandler AnonymousHandler() => factory.Server.CreateHandler();
    public HttpClient AnonymousClient() => factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri(environmentName == "Production" ? "https://localhost" : "http://localhost"),
        AllowAutoRedirect = false
    });
    public static async Task<ManagementHost> CreateAsync(bool worker = false, bool realEngine = false, AeroSynapse.Application.Simulation.IIntelligenceClient? client = null,
        Action<IServiceCollection>? services = null, string environment = "Testing", bool multiTenant = false)
    {
        var host = new ManagementHost { environmentName = environment, multiTenant = multiTenant };
        await host.Admin($"CREATE DATABASE \"{host.name}\"");
        if (multiTenant) await host.Admin($"CREATE DATABASE \"{host.OtherDatabase}\"");
        try
        {
            host.factory = new Factory(host, worker, realEngine, client, services);
            host.Client = await host.SignInAsync("Admin", "test-admin");
            return host;
        }
        catch { await host.DisposeAsync(); throw; }
    }
    public async Task<HttpClient> SignInAsync(string? role, string? username = null, string tenantId = "demo")
    {
        username ??= "test-" + Guid.NewGuid().ToString("N");
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) + "!aA1";
        await using (var scope = Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<WorkspaceUser>>();
            var user = new WorkspaceUser { UserName = username, TenantId = tenantId };
            var result = await users.CreateAsync(user, password);
            if (!result.Succeeded) throw new InvalidOperationException(string.Join(",", result.Errors.Select(error => error.Code)));
            if (role is not null) await users.AddToRoleAsync(user, role);
        }
        var client = AnonymousClient();
        var csrf = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/auth/csrf");
        using var login = new HttpRequestMessage(HttpMethod.Post, "/auth/login") { Content = JsonContent.Create(new { username, password }) };
        login.Headers.Add("X-CSRF-TOKEN", csrf.GetProperty("requestToken").GetString());
        using var response = await client.SendAsync(login);
        response.EnsureSuccessStatusCode();
        if (username == "test-admin") CookieHeader = string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(value => value.Split(';')[0]));
        csrf = await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/auth/csrf");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf.GetProperty("requestToken").GetString());
        return client;
    }
    private sealed class CookieHandler(string cookie) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
            return base.SendAsync(request, token);
        }
    }
    public async Task<bool> Dispatch(CancellationToken token = default)
    {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IRunExecutionService>().DispatchNextAsync(token);
    }
    private async Task Admin(string sql)
    {
        await using var connection = new NpgsqlConnection((settings with { Database = "postgres" }).ConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (factory is not null) await factory.DisposeAsync();
        if (name.StartsWith("aerosynapse_management_test_", StringComparison.Ordinal))
        {
            if (multiTenant) await Admin($"DROP DATABASE IF EXISTS \"{OtherDatabase}\" WITH (FORCE)");
            await Admin($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");
        }
        foreach (var suffix in new[] { "", "-shm", "-wal" }) { File.Delete(sqlite + suffix); File.Delete(sqlite + ".other" + suffix); File.Delete(sqlite + ".identity" + suffix); }
    }
    private sealed class Factory(ManagementHost host, bool worker, bool realEngine, AeroSynapse.Application.Simulation.IIntelligenceClient? client,
        Action<IServiceCollection>? configure) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(host.environmentName);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Postgres:Host"] = host.settings.Host, ["Postgres:Port"] = host.settings.Port.ToString(),
                ["Postgres:Database"] = host.name, ["Postgres:Username"] = host.settings.Username, ["Postgres:Password"] = host.settings.Password,
                ["Database:ApplyMigrations"] = "true", ["Database:SeedSynthetic"] = "true",
                ["ConnectionStrings:AirportMaps"] = $"Data Source={host.sqlite};Pooling=False",
                ["Simulation:WorkerEnabled"] = worker.ToString(),
                ["Simulation:BaseUrl"] = Environment.GetEnvironmentVariable("SIMULATION_TEST_URL") ?? "http://intelligence:8000",
                ["Logging:LogLevel:Default"] = "Critical"
            }));
            if (host.multiTenant) builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tenancy:Tenants:demo:Database"] = host.name, ["Tenancy:Tenants:demo:AirportMaps"] = $"Data Source={host.sqlite};Pooling=False",
                ["Tenancy:Tenants:demo:IntelligenceBaseUrl"] = "http://engine-demo:8000",
                ["Tenancy:Tenants:other:Database"] = host.OtherDatabase, ["Tenancy:Tenants:other:AirportMaps"] = $"Data Source={host.sqlite}.other;Pooling=False",
                ["Tenancy:Tenants:other:IntelligenceBaseUrl"] = "http://engine-other:8000"
            }));
            builder.ConfigureServices(services =>
            {
                if (client is not null) { services.RemoveAll<AeroSynapse.Application.Simulation.IIntelligenceClient>(); services.AddSingleton(client); }
                // Minimal hosting reads storage options before ConfigureAppConfiguration runs.
                // Replace both DbContext option registrations, so every host owns its database.
                services.RemoveAll<DbContextOptions<TwinDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<TwinDbContext>>();
                services.AddDbContext<TwinDbContext>((provider, options) => StorageRegistration.Configure(options, host.settings with { Database = provider.GetRequiredService<TenantCatalog>().Current.Database }, seedSynthetic: true));
                services.RemoveAll<DbContextOptions<AirportMapDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<AirportMapDbContext>>();
                services.AddDbContext<AirportMapDbContext>((provider, options) => options.UseSqlite(provider.GetRequiredService<TenantCatalog>().Current.AirportMaps));
                services.RemoveAll<DbContextOptions<IdentityStore>>();
                services.RemoveAll<IDbContextOptionsConfiguration<IdentityStore>>();
                services.AddDbContext<IdentityStore>(options => options.UseSqlite($"Data Source={host.sqlite}.identity;Pooling=False"));
                if (!worker)
                    foreach (var registration in services.Where(x => x.ServiceType == typeof(IHostedService)
                        && (x.ImplementationType?.IsGenericType == true ? x.ImplementationType.GenericTypeArguments[0].Name : x.ImplementationType?.Name)
                            is "RunDispatcher" or "SimulationWorker" or "BatchDispatcher" or "CopilotWorkflowDispatcher").ToArray())
                        services.Remove(registration);
                if (!realEngine)
                {
                    services.RemoveAll<ISimulationEngine>();
                    if (host.multiTenant) services.AddScoped<ISimulationEngine>(_ => new RecordingEngine());
                    else services.AddSingleton<ISimulationEngine>(host.Engine);
                }
                configure?.Invoke(services);
            });
        }
    }
}

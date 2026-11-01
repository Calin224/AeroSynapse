using AeroSynapse.API.Endpoints;
using AeroSynapse.Application;
using AeroSynapse.Application.Contracts;
using AeroSynapse.API;
using AeroSynapse.Application.Health;
using AeroSynapse.Application.Storage;
using AeroSynapse.Infrastructure;
using AeroSynapse.Infrastructure.Airports;
using AeroSynapse.Infrastructure.Health;
using AeroSynapse.Infrastructure.Storage;
using AeroSynapse.Infrastructure.Simulation;
using AeroSynapse.Application.Simulation;
using MediatR;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using AeroSynapse.Application.Management;
using AeroSynapse.Infrastructure.Execution;
using AeroSynapse.API.Operations;
using AeroSynapse.Application.Operations;
using AeroSynapse.Infrastructure.Operations;
using AeroSynapse.API.Identity;
using AeroSynapse.API.Security;
using AeroSynapse.API.Audit;
using AeroSynapse.Infrastructure.Audit;
using AeroSynapse.Infrastructure.Governance;

var storageCommand = args.SingleOrDefault(argument => argument is "--migrate" or "--seed" or "--reset-synthetic");
var builder = WebApplication.CreateBuilder(args.Where(argument => argument != storageCommand).ToArray());
builder.AddTenancy();
builder.AddWorkspaceIdentity();
builder.AddOperationalSecurity();
builder.Services.AddHttpClient("BrowserIntelligence", (provider, client) =>
{
    client.BaseAddress = new Uri((provider.GetRequiredService<TenantCatalog>().Current.IntelligenceBaseUrl ?? builder.Configuration["Intelligence:BaseUrl"] ?? "http://localhost:8000").TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(45);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });

var postgresHost = builder.Configuration["Postgres:Host"];
builder.Services.AddApplication(includeOperationalManagement: !string.IsNullOrWhiteSpace(postgresHost));
builder.Services.AddOpenApi();
builder.Services.AddInfrastructure(builder.Configuration.GetConnectionString("AirportMaps")
    ?? "Data Source=airport-maps.db", provider => provider.GetRequiredService<TenantCatalog>().Current.AirportMaps);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));

builder.Services.AddHealthChecks();
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
    options.SerializerOptions.RespectRequiredConstructorParameters = true;
    options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
    // Preserve airport-map binding so nonfinite coordinates reach its field validator (422).
    // Operational management keeps strict numeric binding (400 for invalid wire values).
    var resolver = options.SerializerOptions.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver();
    options.SerializerOptions.TypeInfoResolver = resolver.WithAddedModifier(type =>
    {
        if (type.Type.Namespace != typeof(AeroSynapse.Domain.Airports.AirportMap).Namespace) return;
        foreach (var property in type.Properties.Where(property => property.PropertyType == typeof(double)))
            property.NumberHandling = JsonNumberHandling.AllowReadingFromString;
    });
});
if (!string.IsNullOrWhiteSpace(postgresHost))
{
    var postgresSettings = new PostgresSettings(
        postgresHost,
        builder.Configuration.GetValue("Postgres:Port", 5432),
        builder.Configuration["Postgres:Database"] ?? "aerosynapse",
        builder.Configuration["Postgres:Username"] ?? "aerosynapse",
        builder.Configuration["Postgres:Password"] ?? "");
    builder.Services.AddPostgresReadiness(postgresSettings);
    builder.Services.AddPostgresStorage(postgresSettings, builder.Configuration.GetValue("Database:SeedSynthetic", false),
        provider => postgresSettings with { Database = provider.GetRequiredService<TenantCatalog>().Current.Database });
    builder.Services.AddOptions<AuditOptions>().Bind(builder.Configuration.GetSection("Audit"))
        .Validate(options => options.RetentionDays is >= 7 and <= 3650, "Audit retention must be 7–3650 days.").ValidateOnStart();
    if (storageCommand is null) builder.Services.AddHostedService<TenantWorker<AuditRetention>>();
    builder.Services.AddOptions<OperationsOptions>().Bind(builder.Configuration.GetSection("Operations"))
        .Validate(settings => settings.BroadcastIntervalMilliseconds is >= 100 and <= 5000
            && settings.MaxEventsPerBatch is >= 1 and <= 64 && settings.EnginePollMilliseconds is >= 500 and <= 10000,
            "Invalid Operations settings.").ValidateOnStart();
    builder.Services.AddSignalR(options =>
    {
        options.MaximumReceiveMessageSize = 8192;
        options.MaximumParallelInvocationsPerClient = 1;
        options.KeepAliveInterval = TimeSpan.FromSeconds(5);
        options.ClientTimeoutInterval = TimeSpan.FromSeconds(20);
        options.HandshakeTimeout = TimeSpan.FromSeconds(10);
    });
    builder.Services.AddSingleton<ScenarioSubscriptions>();
    if (storageCommand is null && builder.Configuration.GetValue("Operations:Enabled", true))
        builder.Services.AddHostedService<TenantWorker<OperationsBroadcaster>>();
    builder.Services.AddOptions<SimulationOptions>().Bind(builder.Configuration.GetSection("Intelligence"))
        .Validate(settings => Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            && settings.TimeoutSeconds is >= 1 and <= 60 && settings.ReconciliationSeconds > settings.TimeoutSeconds
            && settings.ReconciliationSeconds <= 3600 && settings.PollSeconds is >= 1 and <= 60
            && settings.SafeRetryCount is >= 0 and <= 3, "Invalid Intelligence settings.").ValidateOnStart();
    builder.Services.AddHttpClient<IIntelligenceClient, IntelligenceClient>((provider, client) =>
    {
        var settings = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<SimulationOptions>>().Value;
        client.BaseAddress = new Uri((provider.GetRequiredService<TenantCatalog>().Current.IntelligenceBaseUrl ?? settings.BaseUrl).TrimEnd('/') + "/");
        client.Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds);
    });
    builder.Services.AddScoped<ISimulationService, SimulationService>();
    // Optional dependency: invalid or missing model configuration never blocks API startup/readiness.
    builder.Services.AddOptions<CopilotOptions>().Bind(builder.Configuration.GetSection("Copilot"));
    builder.Services.AddScoped<CopilotReceipts>();
    builder.Services.AddScoped<AiGovernance>();
    builder.Services.AddOptions<CopilotTranscriptOptions>().Bind(builder.Configuration.GetSection("CopilotTranscripts"))
        .Validate(settings => settings.DefaultRetentionHours >= 1 && settings.DefaultRetentionHours <= settings.MaxRetentionHours
            && settings.MaxRetentionHours <= 168, "Transcript retention must be between one hour and one week.").ValidateOnStart();
    if (storageCommand is null) builder.Services.AddHostedService<TenantWorker<CopilotTranscriptCleanup>>();
    builder.Services.AddHttpClient<ICopilotModel, CopilotModelClient>(client => client.Timeout = Timeout.InfiniteTimeSpan)
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
        .RemoveAllLoggers(); // Provider URL, credentials and prompt are not application log fields.
    builder.Services.AddHttpClient<AeroSynapse.Application.Contracts.IPassengerGenerator, PassengerGeneratorClient>((provider, client) =>
    {
        var settings = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<SimulationOptions>>().Value;
        client.BaseAddress = new Uri((provider.GetRequiredService<TenantCatalog>().Current.IntelligenceBaseUrl ?? settings.BaseUrl).TrimEnd('/') + "/");
        client.Timeout = TimeSpan.FromSeconds(30);
    });
    builder.Services.AddScoped<SimulationProcessor>();
    // Gemini explains and ranks the post-run optimizer's already simulated options; a missing key reports "unconfigured".
    builder.Services.AddOptions<AgentOptions>().Bind(builder.Configuration.GetSection("Agent"));
    builder.Services.AddHttpClient<IAgentModel, GeminiAgentModel>(client => client.Timeout = Timeout.InfiniteTimeSpan)
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
        .RemoveAllLoggers(); // The API key travels in a header; requests are not application log fields.
    builder.Services.AddScoped<AgentImpactService>();
    builder.Services.AddScoped<RunOptimizationService>();
    builder.Services.AddMemoryCache();
    builder.Services.AddHttpClient<INavigationPlanner,NavigationPlannerClient>((provider,client) =>
    {
        var settings=provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<SimulationOptions>>().Value;
        client.BaseAddress=new Uri((provider.GetRequiredService<TenantCatalog>().Current.IntelligenceBaseUrl ?? settings.BaseUrl).TrimEnd('/')+"/"); client.Timeout=TimeSpan.FromSeconds(30);
    });
    builder.Services.AddHttpClient<IStaffingPlanner, StaffingPlannerClient>((provider, client) =>
    {
        var settings = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<SimulationOptions>>().Value;
        client.BaseAddress = new Uri((provider.GetRequiredService<TenantCatalog>().Current.IntelligenceBaseUrl ?? settings.BaseUrl).TrimEnd('/') + "/");
        client.Timeout = TimeSpan.FromSeconds(40); // The solver's validated budget is at most 30 seconds.
    });
    builder.Services.AddHttpClient<IResourcePlanner, ResourcePlannerClient>((provider, client) =>
    {
        var settings = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<SimulationOptions>>().Value;
        client.BaseAddress = new Uri((provider.GetRequiredService<TenantCatalog>().Current.IntelligenceBaseUrl ?? settings.BaseUrl).TrimEnd('/') + "/");
        client.Timeout = TimeSpan.FromSeconds(40);
    });
    builder.Services.AddHttpClient<IQueueForecaster, QueueForecasterClient>((provider, client) =>
    {
        var settings = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<SimulationOptions>>().Value;
        client.BaseAddress = new Uri((provider.GetRequiredService<TenantCatalog>().Current.IntelligenceBaseUrl ?? settings.BaseUrl).TrimEnd('/') + "/");
        client.Timeout = TimeSpan.FromSeconds(30);
    });
    builder.Services.AddHttpClient<IAnomalyDetector, AnomalyDetectorClient>((provider, client) =>
    {
        var settings = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<SimulationOptions>>().Value;
        client.BaseAddress = new Uri((provider.GetRequiredService<TenantCatalog>().Current.IntelligenceBaseUrl ?? settings.BaseUrl).TrimEnd('/') + "/");
        client.Timeout = TimeSpan.FromSeconds(30);
    });
    builder.Services.AddHttpClient<IQueuePredictor, QueuePredictorClient>((provider, client) =>
    {
        var settings = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<SimulationOptions>>().Value;
        client.BaseAddress = new Uri((provider.GetRequiredService<TenantCatalog>().Current.IntelligenceBaseUrl ?? settings.BaseUrl).TrimEnd('/') + "/");
        client.Timeout = TimeSpan.FromSeconds(30); // A model still training answers at once with the deterministic forecast.
    });
    builder.Services.PostConfigure<SimulationOptions>(settings =>
    {
        if (builder.Configuration["Intelligence:BaseUrl"] is null && builder.Configuration["Simulation:BaseUrl"] is { } legacyUrl)
            settings.BaseUrl = legacyUrl;
    });
    builder.Services.AddScoped<ISimulationEngine, HttpSimulationEngine>();
    if (storageCommand is null && builder.Configuration.GetValue("Simulation:WorkerEnabled", true))
    {
        builder.Services.AddHostedService<TenantWorker<SimulationWorker>>();
        builder.Services.AddHostedService<TenantWorker<RunDispatcher>>();
        builder.Services.AddHostedService<TenantWorker<BatchDispatcher>>();
        builder.Services.AddHostedService<TenantWorker<CopilotWorkflowDispatcher>>();
    }
}

var app = builder.Build();
app.Use(async (context, next) =>
{
    var supplied = context.Request.Headers["X-Correlation-ID"].ToString();
    var correlationId = context.RequestServices.GetRequiredService<AeroSynapse.Infrastructure.Security.SecretRedactor>().Redact(supplied) == supplied
        && supplied.Length is > 0 and <= 64 && supplied.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
        ? supplied : Guid.NewGuid().ToString("N");
    context.Items["CorrelationId"] = correlationId;
    context.Response.Headers["X-Correlation-ID"] = correlationId;
    using var scope = app.Logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId });
    var started = System.Diagnostics.Stopwatch.GetTimestamp();
    await next(context);
    // Health probes run every few seconds; logging them buries the simulation requests.
    if (!context.Request.Path.StartsWithSegments("/health"))
        app.Logger.LogInformation("{Method} {Path} -> {StatusCode} ({ElapsedMs:0} ms) correlationId={CorrelationId}", context.Request.Method,
            context.Request.Path, context.Response.StatusCode, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, correlationId);
});
app.UseErrorResponseRedaction();
app.UseWorkspaceIdentity();
app.UseOperationalSecurity();
app.MapContractEndpoints();
app.MapOpenApi();
app.MapIdentityEndpoints();
app.MapIntelligenceBrowserEndpoints();

// Binding errors occur before endpoint filters, so return an explicit error here too.
app.Use(async (context, next) =>
{
    try { await next(context); }
    catch (BadHttpRequestException exception)
    {
        await Results.Problem(statusCode: exception.StatusCode, title: "Invalid request.",
            extensions: new Dictionary<string, object?> { ["code"] = "invalid_request" }).ExecuteAsync(context);
    }
});

if (storageCommand is not null && string.IsNullOrWhiteSpace(postgresHost))
{
    Console.Error.WriteLine("Postgres__Host must be configured for storage commands.");
    Environment.ExitCode = 2;
    return;
}
if (!string.IsNullOrWhiteSpace(postgresHost) &&
    (storageCommand is not null || builder.Configuration.GetValue("Database:ApplyMigrations", false)))
{
    foreach (var tenantStorage in app.Services.GetRequiredService<TenantCatalog>().All)
    {
        using var tenant = TenantContext.Begin(tenantStorage.Id);
        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<TwinDbContext>();
        await database.Database.MigrateAsync();
        if (storageCommand == "--seed") await SyntheticScenarioSeeder.SeedAsync(database);
        if (storageCommand == "--reset-synthetic") await SyntheticScenarioSeeder.ResetAsync(database);
        app.Logger.LogInformation("Storage migrations complete; tenantId={TenantId} command={Command}, syntheticSeed={SyntheticSeed}",
            tenantStorage.Id, storageCommand ?? "startup", builder.Configuration.GetValue("Database:SeedSynthetic", false));
    }
    if (storageCommand is not null) return;
}

if (!string.IsNullOrWhiteSpace(postgresHost))
{
    app.MapScenarioEndpoints();
    app.MapPassengerEndpoints();
    app.MapNavigationEndpoints();
    app.MapStaffingEndpoints();
    app.MapResourceEndpoints();
    app.MapKpiEndpoints();
    app.MapAnomalyEndpoints();
    app.MapNotificationEndpoints();
    app.MapCopilotEndpoints();
    app.MapCopilotPlaybookEndpoints();
    app.MapRunStorageEndpoints();
    app.MapBatchEndpoints();
    app.MapSimulationEndpoints();
    app.MapSceneEndpoints();
    app.MapGet("/scenarios/{id:guid}/operations", async (Guid id, Guid? runId, long? afterSequence, IOperationsReader reader, CancellationToken token) =>
        Results.Ok(await reader.StateAsync(id, runId, afterSequence, token))).AddEndpointFilter<ScenarioErrorFilter>();
    if (builder.Configuration.GetValue("Operations:Enabled", true))
        app.MapHub<OperationsHub>("/hubs/operations", options => options.CloseOnAuthenticationExpiration = true)
            .RequireAuthorization(WorkspaceRoles.Read);
}

foreach (var tenantStorage in app.Services.GetRequiredService<TenantCatalog>().All)
{
    using var tenant = TenantContext.Begin(tenantStorage.Id);
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AirportMapDbContext>();
    await db.Database.EnsureCreatedAsync();
}

app.MapAirportEndpoints();
await app.InitializeIdentityAsync();

app.MapGet("/health", async (ISender sender, CancellationToken cancellationToken) =>
    Results.Ok(await sender.Send(new GetHealthQuery(), cancellationToken))).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    ResponseWriter = (context, report) => context.Response.WriteAsJsonAsync(new
    {
        status = report.Status.ToString(),
        checks = report.Entries.ToDictionary(entry => entry.Key, entry => entry.Value.Status.ToString())
    })
}).AllowAnonymous();

app.Run();

public partial class Program;

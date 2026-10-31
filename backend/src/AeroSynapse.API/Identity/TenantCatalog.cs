using System.Security.Claims;
using AeroSynapse.Infrastructure.Governance;
using Microsoft.Data.Sqlite;

namespace AeroSynapse.API.Identity;

public sealed record TenantStorage(string Id, string Database, string AirportMaps, string? IntelligenceBaseUrl = null);

// Database-per-organization keeps SQL, historical evidence and workers within the
// same boundary, including raw SQL that would bypass EF query filters.
public sealed class TenantCatalog
{
    public IReadOnlyList<TenantStorage> All { get; }
    public TenantCatalog(IConfiguration config)
    {
        var configured = config.GetSection("Tenancy:Tenants").GetChildren().ToArray();
        All = configured.Length == 0
            ? [new(TenantContext.Default, config["Postgres:Database"] ?? "aerosynapse", config.GetConnectionString("AirportMaps") ?? "Data Source=airport-maps.db")]
            : configured.Select(section => new TenantStorage(section.Key, section["Database"] ?? "", section["AirportMaps"] ?? "", section["IntelligenceBaseUrl"])).ToArray();
        if (!All.Any(t => t.Id == TenantContext.Default) || All.Any(t => t.Id.Length is < 1 or > 40 || t.Id.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-')
            || t.Database.Length is < 1 or > 63 || t.Database.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_') || string.IsNullOrWhiteSpace(t.AirportMaps))
            || All.Select(t => t.Database).Distinct(StringComparer.OrdinalIgnoreCase).Count() != All.Count)
            throw new InvalidOperationException("Each tenant needs a unique database, a map database and a lowercase ID; demo must be registered.");
        var paths = All.Select(t => new SqliteConnectionStringBuilder(t.AirportMaps)).ToArray();
        if (paths.Any(p => p.Mode == SqliteOpenMode.Memory || p.DataSource is "" or ":memory:") && All.Count > 1
            || paths.Select(p => Path.GetFullPath(p.DataSource)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != All.Count)
            throw new InvalidOperationException("Tenant map databases must use distinct physical files.");
        // FastAPI persists executions too: a multi-tenant installation must isolate
        // those stores through independently deployed engines, never a shared cache.
        if (All.Count > 1 && (All.Any(t => !Uri.TryCreate(t.IntelligenceBaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            || All.Select(t => new Uri(t.IntelligenceBaseUrl!).AbsoluteUri.TrimEnd('/')).Distinct(StringComparer.OrdinalIgnoreCase).Count() != All.Count))
            throw new InvalidOperationException("Each tenant must have an independent FastAPI endpoint and execution database.");
    }
    public bool Contains(string? id) => All.Any(t => t.Id == id);
    public TenantStorage Current => All.SingleOrDefault(t => t.Id == TenantContext.Id)
        ?? throw new InvalidOperationException("Tenant storage is not registered.");
}

internal static class TenantRegistration
{
    public static void AddTenancy(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<TenantCatalog>();
        builder.Services.AddHttpContextAccessor();
    }
    public static void UseTenancy(this WebApplication app) => app.Use(async (context, next) =>
    {
        if (context.User.Identity?.IsAuthenticated != true) { await next(context); return; }
        var id = context.User.FindFirstValue(TenantContext.Claim);
        if (!context.RequestServices.GetRequiredService<TenantCatalog>().Contains(id))
        {
            await WorkspaceIdentity.Problem(403, "tenant_unavailable", "Organization access is unavailable.").ExecuteAsync(context);
            return;
        }
        context.Response.Headers.CacheControl = "no-store";
        using var tenant = TenantContext.Begin(id!);
        using var log = app.Logger.BeginScope(new Dictionary<string, object> { ["TenantId"] = id! });
        await next(context);
    });
}

// One worker instance per registered tenant. ExecutionContext flows to child
// tasks and their independent DI scopes; no polling job uses a request's tenant.
internal sealed class TenantWorker<T>(IServiceProvider provider, TenantCatalog tenants, ILogger<TenantWorker<T>> logger) : BackgroundService where T : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken token) => Task.WhenAll(tenants.All.Select(t => Run(t.Id, token)));
    private async Task Run(string id, CancellationToken token)
    {
        using var tenant = TenantContext.Begin(id);
        using var log = logger.BeginScope(new Dictionary<string, object> { ["TenantId"] = id });
        using var worker = ActivatorUtilities.CreateInstance<T>(provider);
        await worker.StartAsync(token);
        try { if (worker.ExecuteTask is { } work) await work.WaitAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { await worker.StopAsync(CancellationToken.None); }
    }
}

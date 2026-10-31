using System.Security.Claims;
using AeroSynapse.Infrastructure.Identity;
using AeroSynapse.Infrastructure.Storage;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using System.Net;
using AeroSynapse.Infrastructure.Governance;

namespace AeroSynapse.API.Identity;

public static class WorkspaceRoles
{
    public const string Viewer = "Viewer", Operator = "Operator", Admin = "Admin";
    public static readonly string[] All = [Viewer, Operator, Admin];
    public const string Read = "WorkspaceRead", Write = "WorkspaceWrite";
}

public sealed class SessionOptions
{
    public int DurationMinutes { get; set; } = 30;
}

public sealed class WorkspaceIdentity(IdentityStore database, TimeProvider clock)
{
    public const string SessionClaim = "aerosynapse_session";

    public async Task<bool> IsCurrentAsync(ClaimsPrincipal? principal, CancellationToken token = default)
    {
        if (principal?.Identity?.IsAuthenticated != true ||
            !Guid.TryParse(principal.FindFirstValue(SessionClaim), out var sessionId)) return false;
        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var tenantId = principal.FindFirstValue(TenantContext.Claim);
        var now = clock.GetUtcNow().UtcDateTime;
        return await database.Sessions.AsNoTracking().AnyAsync(session => session.Id == sessionId
            && session.UserId == userId && session.ExpiresAtUtc > now
            && database.Users.Any(user => user.Id == session.UserId && !user.Disabled
                && user.SecurityStamp == session.SecurityStamp && user.TenantId == tenantId), token);
    }

    // A subscription must belong to the same tenant as the storage scope.
    public async Task<bool> CanReadScenarioAsync(ClaimsPrincipal? principal, Guid scenarioId, TwinDbContext scenarios,
        CancellationToken token = default) => scenarioId != Guid.Empty
        && WorkspaceRoles.All.Any(role => principal?.IsInRole(role) == true)
        && principal?.FindFirstValue(TenantContext.Claim) == TenantContext.Id
        && await IsCurrentAsync(principal, token)
        && await scenarios.Scenarios.AsNoTracking().AnyAsync(scenario => scenario.Id == scenarioId, token);

    public static IResult Problem(int status, string code, string title) => Results.Problem(statusCode: status,
        title: title, extensions: new Dictionary<string, object?> { ["code"] = code });
}

internal sealed class OperationalAccess : IAuthorizationRequirement;

internal sealed class OperationalAccessHandler : AuthorizationHandler<OperationalAccess>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, OperationalAccess requirement)
    {
        var read = context.Resource is HttpContext http && HttpMethods.IsGet(http.Request.Method)
            || context.Resource is HttpContext head && HttpMethods.IsHead(head.Request.Method);
        if ((read ? WorkspaceRoles.All : [WorkspaceRoles.Operator, WorkspaceRoles.Admin]).Any(context.User.IsInRole))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

internal static class IdentityRegistration
{
    public static void AddWorkspaceIdentity(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        services.TryAddSingleton(TimeProvider.System);
        services.AddDbContext<IdentityStore>(options => options.UseSqlite(
            builder.Configuration.GetConnectionString("Identity") ?? "Data Source=identity.db"));
        services.AddIdentity<WorkspaceUser, IdentityRole>(options =>
        {
            options.Password.RequiredLength = 12;
            options.Password.RequiredUniqueChars = 4;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            options.User.RequireUniqueEmail = false;
        }).AddEntityFrameworkStores<IdentityStore>();
        services.AddOptions<SessionOptions>().Bind(builder.Configuration.GetSection("Identity:Session"))
            .Validate(options => options.DurationMinutes is >= 5 and <= 120, "Session duration must be 5–120 minutes.").ValidateOnStart();
        services.AddScoped<WorkspaceIdentity>();
        services.AddHostedService<SessionCleanup>();
        services.AddRateLimiter(options =>
        {
            options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
                { PermitLimit = 15, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.OnRejected = (context, _) => new ValueTask(WorkspaceIdentity.Problem(429, "login_rate_limited", "Too many sign-in attempts. Try again later.").ExecuteAsync(context.HttpContext));
        });
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            foreach (var address in builder.Configuration.GetSection("Identity:TrustedProxies").Get<string[]>() ?? [])
            {
                if (!IPAddress.TryParse(address, out var proxy)) throw new InvalidOperationException("Identity trusted proxies must be explicit IP addresses.");
                options.KnownProxies.Add(proxy);
            }
        });
        var insecureLocal = builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing");
        var secure = insecureLocal ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = insecureLocal ? "AeroSynapse.Session" : "__Host-AeroSynapse.Session";
            options.Cookie.Path = "/";
            options.Cookie.HttpOnly = true;
            options.Cookie.IsEssential = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = secure;
            options.SlidingExpiration = false;
            options.Events.OnRedirectToLogin = context => WorkspaceIdentity.Problem(401, "authentication_required", "Sign in to the workspace.").ExecuteAsync(context.HttpContext);
            options.Events.OnRedirectToAccessDenied = context => WorkspaceIdentity.Problem(403, "role_required", "Your role cannot perform this action.").ExecuteAsync(context.HttpContext);
            options.Events.OnValidatePrincipal = async context =>
            {
                if (!await context.HttpContext.RequestServices.GetRequiredService<WorkspaceIdentity>()
                    .IsCurrentAsync(context.Principal, context.HttpContext.RequestAborted))
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
                }
            };
        });
        services.AddOptions<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme)
            .Configure<TimeProvider>((options, clock) => options.TimeProvider = clock);
        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-TOKEN";
            options.Cookie.Name = insecureLocal ? "AeroSynapse.Csrf" : "__Host-AeroSynapse.Csrf";
            options.Cookie.Path = "/";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = secure;
        });
        services.AddAuthorization(options =>
        {
            options.AddPolicy(WorkspaceRoles.Read, policy => policy.RequireAuthenticatedUser().RequireRole(WorkspaceRoles.All));
            options.AddPolicy(WorkspaceRoles.Write, policy => policy.RequireAuthenticatedUser().RequireRole(WorkspaceRoles.Operator, WorkspaceRoles.Admin));
            options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().AddRequirements(new OperationalAccess()).Build();
        });
        services.AddSingleton<IAuthorizationHandler, OperationalAccessHandler>();
        var protection = services.AddDataProtection().SetApplicationName("AeroSynapse");
        var keyDirectory = builder.Configuration["Identity:KeyDirectory"] ?? builder.Configuration["CopilotTranscripts:KeyDirectory"];
        if (!string.IsNullOrWhiteSpace(keyDirectory)) protection.PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
    }

    public static async Task InitializeIdentityAsync(this WebApplication app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IdentityStore>();
        await store.Database.MigrateAsync();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var name in WorkspaceRoles.All)
            if (!await roles.RoleExistsAsync(name)) Ensure(await roles.CreateAsync(new(name)));
        // Only explicit server-side opt-in provisions fictional accounts. No password defaults.
        if (!app.Configuration.GetValue("Identity:Demo:Enabled", false)) return;
        var users = scope.ServiceProvider.GetRequiredService<UserManager<WorkspaceUser>>();
        foreach (var role in WorkspaceRoles.All)
        {
            var username = role.ToLowerInvariant() + ".demo";
            if (await users.FindByNameAsync(username) is not null) continue;
            var password = app.Configuration[$"Identity:Demo:{role}Password"];
            if (string.IsNullOrWhiteSpace(password)) throw new InvalidOperationException($"Identity demo provisioning requires a server-side {role} password.");
            var user = new WorkspaceUser { UserName = username };
            Ensure(await users.CreateAsync(user, password));
            Ensure(await users.AddToRoleAsync(user, role));
        }
    }

    internal static void Ensure(IdentityResult result)
    {
        if (!result.Succeeded) throw new InvalidOperationException("Identity operation failed: " + string.Join(", ", result.Errors.Select(error => error.Code)));
    }

    public static void UseWorkspaceIdentity(this WebApplication app)
    {
        app.UseForwardedHeaders();
        app.UseRouting();
        app.Use((context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/auth"))
            {
                context.Response.Headers.CacheControl = "no-cache, no-store";
                context.Response.Headers.Pragma = "no-cache";
            }
            return next(context);
        });
        app.UseAuthentication();
        app.UseTenancy();
        app.UseRateLimiter();
        app.UseAuthorization();
        app.Use(async (context, next) =>
        {
            if (context.GetEndpoint() is null) { await next(context); return; }
            var writes = !(HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method) || HttpMethods.IsOptions(context.Request.Method));
            var anonymous = context.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            // SignalR has its own origin protection; negotiate/long-poll POSTs aren't
            // application commands and cannot carry the application antiforgery header.
            var hub = context.Request.Path.StartsWithSegments("/hubs");
            if (writes && !hub && (!anonymous || context.Request.Path == "/auth/login"))
            {
                try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); }
                catch (AntiforgeryValidationException)
                {
                    await WorkspaceIdentity.Problem(400, "csrf_invalid", "Refresh the session and retry the request.").ExecuteAsync(context);
                    return;
                }
            }
            if (hub && context.Request.Headers.Origin is { Count: > 0 } origin &&
                (!Uri.TryCreate(origin.ToString(), UriKind.Absolute, out var source) || source.Authority != context.Request.Host.Value || source.Scheme != context.Request.Scheme))
            {
                await WorkspaceIdentity.Problem(403, "origin_denied", "The connection origin is not allowed.").ExecuteAsync(context);
                return;
            }
            await next(context);
        });
    }
}

internal sealed class SessionCleanup(IServiceScopeFactory scopes, TimeProvider clock, ILogger<SessionCleanup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var now = clock.GetUtcNow().UtcDateTime;
                await scope.ServiceProvider.GetRequiredService<IdentityStore>().Sessions.Where(session => session.ExpiresAtUtc <= now).ExecuteDeleteAsync(token);
                await Task.Delay(TimeSpan.FromHours(1), token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                logger.LogWarning(error, "Expired session cleanup will retry.");
                await Task.Delay(TimeSpan.FromMinutes(1), token);
            }
        }
    }
}

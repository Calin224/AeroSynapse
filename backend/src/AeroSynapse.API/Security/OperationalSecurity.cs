using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;
using AeroSynapse.API.Identity;
using AeroSynapse.Infrastructure.Audit;
using AeroSynapse.Infrastructure.Security;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace AeroSynapse.API.Security;

public sealed class OperationalSecurityOptions
{
    public int ReadPerMinute { get; set; } = 600;
    public int WritePerMinute { get; set; } = 120;
    public int OptimizationPerMinute { get; set; } = 20;
    public int MaxJsonBytes { get; set; } = 2 * 1024 * 1024;
}

internal static class OperationalSecurity
{
    public static void AddOperationalSecurity(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        services.AddSingleton<SecretRedactor>();
        services.Replace(ServiceDescriptor.Singleton<ILoggerFactory, RedactingLoggerFactory>());
        services.AddOptions<OperationalSecurityOptions>().Bind(builder.Configuration.GetSection("Security"))
            .Validate(options => options.ReadPerMinute is >= 1 and <= 10000 && options.WritePerMinute is >= 1 and <= 10000
                && options.OptimizationPerMinute is >= 1 and <= 1000 && options.MaxJsonBytes is >= 1024 and <= 16 * 1024 * 1024,
                "Invalid operational security limits.").ValidateOnStart();
        services.Configure<RateLimiterOptions>(options =>
        {
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                if (context.Request.Path.StartsWithSegments("/health") || context.Request.Path.Value?.EndsWith("/health", StringComparison.Ordinal) == true)
                    return RateLimitPartition.GetNoLimiter("health");
                if (context.Request.Path == "/auth/logout") return RateLimitPartition.GetNoLimiter("logout");
                var limits = context.RequestServices.GetRequiredService<IOptions<OperationalSecurityOptions>>().Value;
                var write = context.Request.Method is not ("GET" or "HEAD" or "OPTIONS");
                var expensive = write && (context.Request.Path.Value?.Contains("/proposals", StringComparison.Ordinal) == true
                    || context.Request.Path.Value?.Contains("/optimize", StringComparison.Ordinal) == true);
                var bucket = expensive ? "optimization" : write ? "write" : "read";
                var actor = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(actor + ":" + bucket, _ => new FixedWindowRateLimiterOptions
                { PermitLimit = expensive ? limits.OptimizationPerMinute : write ? limits.WritePerMinute : limits.ReadPerMinute,
                    Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });
            });
            options.OnRejected = async (context, token) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = "60";
                await WorkspaceIdentity.Problem(429, "rate_limited", "Request limit reached. Retry later.").ExecuteAsync(context.HttpContext);
            };
        });
    }

    public static void UseOperationalSecurity(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var redactor = context.RequestServices.GetRequiredService<SecretRedactor>();
            var identity = new AuditIdentity(context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "system",
                redactor.Redact(context.User.Identity?.Name ?? "system"), redactor.Redact(context.Items["CorrelationId"]?.ToString()));
            using var audit = AuditContext.Begin(identity);
            try
            {
                if (context.Request.HasJsonContentType() && context.Request.Method is "POST" or "PUT" or "PATCH")
                {
                    var maximum = context.RequestServices.GetRequiredService<IOptions<OperationalSecurityOptions>>().Value.MaxJsonBytes;
                    if (context.Request.ContentLength > maximum) throw new BadHttpRequestException("JSON exceeds the request limit.", 413);
                    context.Request.EnableBuffering(bufferThreshold: Math.Min(65536, maximum), bufferLimit: maximum);
                    try
                    {
                        // Versioned validators own duplicate-field errors and the shared 64-level wire limit.
                        // Keep the common byte budget without replacing their public contract semantics.
                        var contract = context.Request.Path.StartsWithSegments("/contracts");
                        using var document = await JsonDocument.ParseAsync(context.Request.Body, new() { MaxDepth = contract ? 64 : 32 }, context.RequestAborted);
                        if (!contract) CheckKeys(document.RootElement);
                    }
                    catch (IOException) { throw new BadHttpRequestException("JSON exceeds the request limit.", 413); }
                    catch (JsonException)
                    {
                        if (context.Request.Path.Value?.Contains("/copilot", StringComparison.Ordinal) == true)
                        {
                            await Results.Problem(statusCode: 422, title: "Invalid tool JSON: duplicate fields and malformed envelopes are refused.",
                                extensions: new Dictionary<string, object?> { ["code"] = "invalid_tool_arguments", ["field"] = "request" })
                                .ExecuteAsync(context);
                            return;
                        }
                        if (context.Request.Path.StartsWithSegments("/contracts"))
                        {
                            await Results.Json(AeroSynapse.Application.Contracts.VersionedContract.Response([
                                AeroSynapse.Application.Contracts.VersionedContract.Error("", "malformed_json")]), statusCode: 422).ExecuteAsync(context);
                            return;
                        }
                        throw new BadHttpRequestException("Invalid JSON.", 400);
                    }
                    finally { context.Request.Body.Position = 0; }
                }
                await next(context);
            }
            catch (BadHttpRequestException error) when (!context.Response.HasStarted)
            {
                await WorkspaceIdentity.Problem(error.StatusCode, "invalid_request", "Invalid request.").ExecuteAsync(context);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            catch (Exception error) when (!context.Response.HasStarted)
            {
                app.Logger.LogError("Request failed errorType={ErrorType} correlationId={CorrelationId}", error.GetType().Name, context.Items["CorrelationId"]);
                await WorkspaceIdentity.Problem(500, "request_failed", "Request failed. Use the correlation ID to inspect the server diagnostics.").ExecuteAsync(context);
            }
        });
    }

    private static void CheckKeys(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!keys.Add(property.Name)) throw new JsonException("Duplicate JSON field.");
                CheckKeys(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) CheckKeys(item);
    }
}

using System.Security.Cryptography;
using System.Text.Json;
using AeroSynapse.Infrastructure.Audit;
using AeroSynapse.Infrastructure.Storage;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace AeroSynapse.API.Audit;

// Read-only solver calls do not touch domain tables, so record their calculated
// output explicitly. State-changing commands are audited atomically by triggers.
public sealed class AuditCalculationFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var database = context.HttpContext.RequestServices.GetService<TwinDbContext>();
        if (database is null) return AeroSynapse.API.Identity.WorkspaceIdentity.Problem(503, "audit_unavailable", "Operational calculation audit storage is unavailable.");
        var result = await next(context);
        var status = (result as IStatusCodeHttpResult)?.StatusCode ?? 200;
        if (status is < 200 or >= 300) return result;
        var json = result is IValueHttpResult value ? JsonSerializer.Serialize(value.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            : result is ContentHttpResult content ? content.ResponseContent ?? "null" : "null";
        var responseHash = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)));
        var evidenceJson = json;
        if (json.Length > 65536)
        {
            using var output = JsonDocument.Parse(json);
            evidenceJson = JsonSerializer.Serialize(Trim(output.RootElement));
        }
        var request = context.HttpContext.Request;
        var position = request.Body.Position;
        request.Body.Position = 0;
        using var input = await JsonDocument.ParseAsync(request.Body, cancellationToken: context.HttpContext.RequestAborted);
        request.Body.Position = position;
        var identity = AuditContext.Current;
        var id = Guid.NewGuid();
        var scenarioId = context.HttpContext.Request.RouteValues.TryGetValue("id", out var resource) && Guid.TryParse(resource?.ToString(), out var scenario) ? (Guid?)scenario : null;
        var action = request.Path.Value?.Contains("optimize", StringComparison.Ordinal) == true || request.Path.Value?.Contains("proposals", StringComparison.Ordinal) == true
            ? "optimization.calculated" : "simulation.calculated";
        int? version = input.RootElement.ValueKind == JsonValueKind.Object && input.RootElement.TryGetProperty("revision", out var revision)
            && revision.TryGetInt32(out var versionValue) ? versionValue : null;
        if (scenarioId.HasValue && version is null) version = await database.Scenarios.Where(row => row.Id == scenarioId).Select(row => (int?)row.Revision).SingleOrDefaultAsync(context.HttpContext.RequestAborted);
        // Database projection applies the same closed redaction rules as row audit.
        await database.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO twin.audit_events ("Id","ActorId","Actor","CorrelationId","Action","ResourceType","ResourceId",
              "ScenarioId","ScenarioVersionAfter","RecordedAtUtc","IsSynthetic","ChangesJson")
            VALUES ({id},{identity.ActorId},{identity.Actor},{identity.CorrelationId},{action},'calculation',{id},{scenarioId},{version},
              clock_timestamp(),true,jsonb_build_object('input',twin.audit_safe({input.RootElement.GetRawText()}::jsonb),
                'output',twin.audit_safe({evidenceJson}::jsonb),'resultHash',{responseHash}))
            """, context.HttpContext.RequestAborted);
        context.HttpContext.Response.Headers["X-Audit-ID"] = id.ToString();
        return result;
    }
    private static object? Trim(JsonElement value, int depth = 0) => depth > 8 ? new { detailOmitted = true }
        : value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 50 ? new { itemCount = value.GetArrayLength(), detailOmitted = true }
        : value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Select(item => Trim(item, depth + 1)).ToArray()
        : value.ValueKind == JsonValueKind.Object ? value.EnumerateObject().ToDictionary(property => property.Name, property => Trim(property.Value, depth + 1))
        : value.Clone();
}

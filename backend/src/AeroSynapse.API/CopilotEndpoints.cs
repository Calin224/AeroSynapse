using AeroSynapse.Application.Management;
using System.Text.Json;

namespace AeroSynapse.API;

internal static class CopilotEndpoints
{
    public static void MapCopilotEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/scenarios/{scenarioId:guid}/runs/{runId:guid}/copilot")
            .WithTags("Copilot").AddEndpointFilter<ScenarioErrorFilter>();
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            try { return await next(context); }
            catch (CopilotToolRejectedException error)
            { return AeroSynapse.API.Identity.WorkspaceIdentity.Problem(error.Status, error.Code, error.Message); }
        });
        group.MapGet("/tools", () => Results.Ok(new { contractVersion = "1.0", maxCallsPerRequest = 1, tools = CopilotToolPolicy.Catalog }));
        group.MapPost("/tools", async (Guid scenarioId, Guid runId, JsonElement payload, ICopilotTools tools, CopilotReceipts receipts, CancellationToken token) =>
        {
            try { return Results.Ok(receipts.Attach(await tools.ExecuteAsync(scenarioId, runId, CopilotToolPolicy.Parse<CopilotToolRequest>(payload), token))); }
            catch (CopilotToolRejectedException error)
            {
                app.Logger.LogWarning("Copilot tool rejected code={Code} scenarioId={ScenarioId} runId={RunId}", error.Code, scenarioId, runId);
                return Results.Problem(statusCode: error.Status, title: error.Message,
                    extensions: new Dictionary<string, object?> { ["code"] = error.Code, ["field"] = error.Field,
                        ["errors"] = new Dictionary<string, string[]> { [error.Field] = [error.Message] } });
            }
        }).Document<CopilotToolResult>("Execute one validated, intent-restricted simulation tool; no approval/apply tool").ProducesProblem(422).ProducesProblem(502);
        group.MapGet("/context", async (Guid scenarioId, Guid runId, ICopilotService copilot, CancellationToken token) =>
            Results.Ok(await copilot.ContextAsync(scenarioId, runId, token)))
            .Document<CopilotContext>("Get immutable scenario/run context for the read-only Copilot");
        group.MapPost("", async (Guid scenarioId, Guid runId, CopilotQuestion request, ICopilotService copilot, CopilotReceipts receipts, CancellationToken token) =>
            Results.Ok(receipts.Attach(await copilot.AskAsync(scenarioId, runId, request, token), request.Question)))
            .Document<CopilotAnswer>("Explain observed metrics with run/zone/time citations and optional server-side LLM topic selection");
        group.MapPost("/transcripts", async (Guid scenarioId, Guid runId, SaveCopilotTranscript request, CopilotReceipts receipts, ICopilotTranscriptStore store, CancellationToken token) =>
            Results.Ok(await store.SaveAsync(receipts.Read(request.Receipt, scenarioId, runId), request.HideSensitive, request.RetentionHours, token)))
            .Document<CopilotTranscript>("Explicitly save a server-issued explanation with retention; questions are redacted by default");
        group.MapGet("/transcripts", async (Guid scenarioId, Guid runId, ICopilotTranscriptStore store, CancellationToken token) =>
            Results.Ok(await store.ListAsync(scenarioId, runId, token))).Document<IReadOnlyList<CopilotTranscriptSummary>>("List unexpired transcript metadata without questions");
        group.MapGet("/transcripts/{id:guid}", async (Guid scenarioId, Guid runId, Guid id, bool? includeSensitive, ICopilotTranscriptStore store, CancellationToken token) =>
            Results.Ok(await store.GetAsync(scenarioId, runId, id, includeSensitive ?? false, token))).Document<CopilotTranscript>("Read an unexpired transcript; sensitive details require explicit opt-in");
        group.MapPost("/transcripts/{id:guid}/redact", async (Guid scenarioId, Guid runId, Guid id, ICopilotTranscriptStore store, CancellationToken token) =>
        { await store.RedactAsync(scenarioId, runId, id, token); return Results.NoContent(); }).WithSummary("Permanently remove the free question");
        group.MapDelete("/transcripts/{id:guid}", async (Guid scenarioId, Guid runId, Guid id, ICopilotTranscriptStore store, CancellationToken token) =>
        { await store.DeleteAsync(scenarioId, runId, id, token); return Results.NoContent(); }).WithSummary("Delete a transcript explicitly");
    }
}

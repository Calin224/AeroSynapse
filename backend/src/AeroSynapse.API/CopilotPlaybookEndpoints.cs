using System.Text.Json;
using AeroSynapse.Application.Management;

namespace AeroSynapse.API;

internal static class CopilotPlaybookEndpoints
{
    public static void MapCopilotPlaybookEndpoints(this WebApplication app)
    {
        app.MapGet("/copilot/playbooks", () => Results.Ok(new { contractVersion = "1.0", agents = CopilotPlaybookPolicy.Agents,
            playbooks = CopilotPlaybookPolicy.Catalog, maxConcurrentSteps = 1, maxTimeLimitSeconds = 180 }))
            .WithTags("Playbooks").WithSummary("Versioned synthetic playbooks, agent capabilities, triggers and stop conditions");
        var group = app.MapGroup("/scenarios/{scenarioId:guid}/runs/{runId:guid}/copilot/workflows")
            .WithTags("Playbooks").AddEndpointFilter<ScenarioErrorFilter>();
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            try { return await next(context); }
            catch (CopilotToolRejectedException error)
            {
                app.Logger.LogWarning("Playbook policy rejected code={Code}", error.Code);
                return Results.Problem(statusCode: error.Status, title: error.Message, extensions: new Dictionary<string, object?> {
                    ["code"] = error.Code, ["field"] = error.Field, ["errors"] = new Dictionary<string, string[]> { [error.Field] = [error.Message] } });
            }
        });
        group.MapPost("", async (Guid scenarioId, Guid runId, JsonElement payload, ICopilotWorkflows workflows, CancellationToken token) =>
        {
            var value = await workflows.StartAsync(scenarioId, runId, CopilotToolPolicy.Parse<StartCopilotWorkflow>(payload), token);
            return Results.Accepted($"/scenarios/{scenarioId}/runs/{runId}/copilot/workflows/{value.Id}", value);
        }).Document<CopilotWorkflow>("Start an idempotent controlled synthetic workflow", 202);
        group.MapGet("", async (Guid scenarioId, Guid runId, ICopilotWorkflows workflows, CancellationToken token) =>
            Results.Ok(await workflows.ListAsync(scenarioId, runId, token))).Document<IReadOnlyList<CopilotWorkflowSummary>>("List retained workflow metadata for this run");
        group.MapGet("/{id:guid}", async (Guid scenarioId, Guid runId, Guid id, ICopilotWorkflows workflows, CancellationToken token) =>
            Results.Ok(await workflows.GetAsync(scenarioId, runId, id, token))).Document<CopilotWorkflow>("Read the manifest, agent steps, outputs and grounded conclusion");
        group.MapPost("/{id:guid}/cancel", async (Guid scenarioId, Guid runId, Guid id, ICopilotWorkflows workflows, CancellationToken token) =>
            Results.Ok(await workflows.CancelAsync(scenarioId, runId, id, token))).Document<CopilotWorkflow>("Request cancellation of the workflow and its owned evaluation");
        group.MapPost("/{id:guid}/replay", async (Guid scenarioId, Guid runId, Guid id, JsonElement payload, ICopilotWorkflows workflows, CancellationToken token) =>
        {
            var value = await workflows.ReplayAsync(scenarioId, runId, id, CopilotToolPolicy.Parse<ReplayCopilotWorkflow>(payload), token);
            return Results.Accepted($"/scenarios/{scenarioId}/runs/{runId}/copilot/workflows/{value.Id}", value);
        }).Document<CopilotWorkflow>("Reproduce a stopped workflow with the same captured run, seed and validated parameters", 202);
    }
}

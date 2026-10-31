using AeroSynapse.Application.Simulation;

namespace AeroSynapse.API;

internal static class SimulationEndpoints
{
    public static void MapSimulationEndpoints(this WebApplication app)
    {
        app.MapPost("/scenarios/{id:guid}/simulations", async (Guid id, StartSimulationRequest request,
            HttpContext context, ISimulationService service, CancellationToken token) =>
        {
            if (!Guid.TryParse(context.Request.Headers["Idempotency-Key"], out var key) || key == Guid.Empty)
                return Results.Problem(statusCode: 400, title: "Idempotency-Key must be a non-empty UUID.");
            var execution = await service.StartAsync(id, key, request.ScenarioRevision, (string)context.Items["CorrelationId"]!, token);
            context.Response.Headers["X-Correlation-ID"] = execution.CorrelationId;
            return Results.Accepted($"/simulations/{key}", execution);
        }).AddEndpointFilter<ScenarioErrorFilter>();
        app.MapGet("/simulations/{commandId:guid}", async (Guid commandId, ISimulationService service, CancellationToken token) =>
        {
            var execution = await service.GetAsync(commandId, token);
            return execution is null ? (IResult)Results.NotFound() : Results.Ok(execution);
        }).AddEndpointFilter<ScenarioErrorFilter>();
    }
}

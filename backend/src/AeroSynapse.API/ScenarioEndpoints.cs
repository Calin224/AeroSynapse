using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using MediatR;
namespace AeroSynapse.API;
internal static class ScenarioEndpoints
{
    public static void MapScenarioEndpoints(this WebApplication app)
    {
        var layouts = app.MapGroup("/scenes/{sceneId}/layout-versions").WithTags("Layouts").AddEndpointFilter<ScenarioErrorFilter>();
        layouts.MapPost("", async (Guid sceneId, ISender sender, CancellationToken token) =>
        {
            var value = await sender.Send(new PublishLayoutCommand(sceneId), token);
            return Results.Created($"/scenes/{sceneId}/layout-versions/{value.Version.Id}", value);
        }).Document<PublishedLayout>("Publish an immutable operational layout", 201);
        layouts.MapGet("", async (Guid sceneId, ISender sender, CancellationToken token) => Results.Ok(await sender.Send(new ListLayoutsQuery(sceneId), token)))
            .Document<IReadOnlyList<LayoutVersionSummary>>("List published layouts");
        layouts.MapGet("/{versionId}", async (Guid sceneId, Guid versionId, ISender sender, CancellationToken token) => Results.Ok(await sender.Send(new GetLayoutQuery(sceneId, versionId), token)))
            .Document<PublishedLayout>("Get the full published layout");
        var scenarios = app.MapGroup("/scenarios").WithTags("Scenarios").AddEndpointFilter<ScenarioErrorFilter>();
        scenarios.MapGet("", async (Guid? sceneId, ISender sender, CancellationToken token) => Results.Ok(await sender.Send(new ListScenariosQuery(sceneId), token)))
            .Document<IReadOnlyList<ScenarioSummary>>("List active scenarios");
        scenarios.MapGet("/{id}", async (Guid id, ISender sender, CancellationToken token) => Results.Ok(await sender.Send(new GetScenarioQuery(id), token)))
            .Document<ScenarioDetails>("Get complete scenario configuration");
        scenarios.MapPost("/validate", async (ScenarioWriteRequest request, ISender sender, CancellationToken token) =>
            Results.Ok(await sender.Send(new ValidateScenarioQuery(request), token)))
            .Document<ScenarioValidationResult>("Validate a complete scenario without creating or changing stored data");
        scenarios.MapPost("", async (ScenarioWriteRequest request, ISender sender, CancellationToken token) =>
        {
            var value = await sender.Send(new CreateScenarioCommand(request), token);
            return Results.Created($"/scenarios/{value.Scenario.Id}", value);
        }).Document<ScenarioDetails>("Validate and persist a complete scenario", 201);
        scenarios.MapPut("/{id}", async (Guid id, ScenarioWriteRequest request, ISender sender, CancellationToken token) => Results.Ok(await sender.Send(new UpdateScenarioCommand(id, request), token)))
            .Document<ScenarioDetails>("Replace configuration using the current scenario revision");
        scenarios.MapPost("/{id}/duplicate", async (Guid id, DuplicateScenarioRequest request, ISender sender, CancellationToken token) =>
        {
            var value = await sender.Send(new DuplicateScenarioCommand(id, request), token);
            return Results.Created($"/scenarios/{value.Scenario.Id}", value);
        }).Document<ScenarioDetails>("Duplicate a scenario independently", 201);
        scenarios.MapPost("/{id}/runs", async (Guid id, ISender sender, CancellationToken token) =>
        {
            var run = await sender.Send(new StartRunCommand(id), token);
            return Results.Accepted($"/runs/{run.Id}", run);
        }).Document<SimulationRun>("Capture immutable inputs and enqueue a simulation", 202);
        scenarios.MapPost("/{id}/what-if", async (Guid id, CreateWhatIfRequest request, IBatchService batches, CancellationToken token) =>
        {
            var pair = await batches.CreateWhatIfAsync(id, request, token);
            return Results.Accepted($"/simulation-batches/{pair.Batch.Id}", pair);
        }).Document<BatchDetails>("Atomically capture baseline and independent staffing what-if runs with the same initial population", 202);
        scenarios.MapGet("/{id}/runs", async (Guid id, int? limit, ISender sender, CancellationToken token) => Results.Ok(await sender.Send(new ListScenarioRunsQuery(id, limit ?? 50), token)))
            .Document<IReadOnlyList<SimulationRun>>("List scenario runs; limit 1–100");
        scenarios.MapGet("/{id}/runs/{runId}", async (Guid id, Guid runId, ISender sender, CancellationToken token) => Results.Ok(await sender.Send(new GetScenarioRunQuery(id, runId), token)))
            .Document<RunDetails>("Get a scenario run with results and readable error");
        scenarios.MapGet("/{id}/flights", async (Guid id, ISender sender, CancellationToken token) => Results.Ok(await sender.Send(new GetFlightsQuery(id), token)))
            .Document<ScenarioItems<ScenarioFlight>>("List flights and current scenario revision");
        scenarios.MapGet("/{id}/flight-schedule", async (Guid id, ISender sender, CancellationToken token) => Results.Ok(await sender.Send(new GetFlightScheduleQuery(id), token)))
            .Document<FlightSchedule>("Get demo flight schedule and gates from the published layout");
        scenarios.MapGet("/{id}/flights/export", async (Guid id, ISender sender, CancellationToken token) =>
            Results.File(System.Text.Encoding.UTF8.GetBytes(await sender.Send(new ExportFlightScheduleQuery(id), token)),
                "text/csv; charset=utf-8", $"demo-flights-{id}.csv"))
            .WithSummary("Export a synthetic flight schedule as CSV").Produces(200, contentType: "text/csv").ProducesProblem(404);
        scenarios.MapPost("/{id}/flights/import", async (Guid id, FlightCsvImportRequest request, ISender sender, CancellationToken token) =>
            Results.Ok(await sender.Send(new ImportFlightScheduleCommand(id, request), token)))
            .Document<ScenarioDetails>("Atomically replace the demo schedule from CSV using the current revision");
        scenarios.MapPost("/{id}/flights", async (Guid id, FlightWriteRequest request, ISender sender, CancellationToken token) => Results.Created($"/scenarios/{id}/flights", await sender.Send(new WriteFlightCommand(id, null, request), token)))
            .Document<ScenarioDetails>("Add a flight and validate the whole aggregate", 201);
        scenarios.MapPut("/{id}/flights/{flightId}", async (Guid id, Guid flightId, FlightWriteRequest request, ISender sender, CancellationToken token) => Results.Ok(await sender.Send(new WriteFlightCommand(id, flightId, request), token)))
            .Document<ScenarioDetails>("Update a flight using scenario revision");
        scenarios.MapDelete("/{id}/flights/{flightId}", async (Guid id, Guid flightId, int revision, ISender sender, CancellationToken token) => Results.Ok(await sender.Send(new DeleteFlightCommand(id, flightId, revision), token)))
            .Document<ScenarioDetails>("Delete an unreferenced flight using scenario revision");
        scenarios.MapGet("/{id}/events", async (Guid id, ISender sender, CancellationToken token) => Results.Ok(await sender.Send(new GetInputEventsQuery(id), token)))
            .Document<ScenarioItems<ScenarioInputEvent>>("List scheduled events and current revision");
        scenarios.MapPost("/{id}/events", async (Guid id, EventWriteRequest request, ISender sender, CancellationToken token) => Results.Created($"/scenarios/{id}/events", await sender.Send(new WriteInputEventCommand(id, null, request), token)))
            .Document<ScenarioDetails>("Add a scheduled event", 201);
        scenarios.MapPut("/{id}/events/{eventId}", async (Guid id, Guid eventId, EventWriteRequest request, ISender sender, CancellationToken token) => Results.Ok(await sender.Send(new WriteInputEventCommand(id, eventId, request), token)))
            .Document<ScenarioDetails>("Update a scheduled event using scenario revision");
        scenarios.MapDelete("/{id}/events/{eventId}", async (Guid id, Guid eventId, int revision, ISender sender, CancellationToken token) => Results.Ok(await sender.Send(new DeleteInputEventCommand(id, eventId, revision), token)))
            .Document<ScenarioDetails>("Delete a scheduled event using scenario revision");
    }
    public static RouteHandlerBuilder Document<T>(this RouteHandlerBuilder route, string summary, int status = 200) => route.WithSummary(summary)
        .Produces<T>(status).ProducesValidationProblem(400).Produces<ContractErrorResponse>(422).ProducesProblem(404).ProducesProblem(409);
}
internal sealed class ScenarioErrorFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var redactor = context.HttpContext.RequestServices.GetRequiredService<AeroSynapse.Infrastructure.Security.SecretRedactor>();
        string Safe(string message) => redactor.Redact(message);
        try { return await next(context); }
        catch (ScenarioValidationException exception)
        {
            return Results.ValidationProblem(exception.Errors.ToDictionary(x => redactor.Redact(x.Key), x => x.Value.Select(redactor.Redact).ToArray()), title: Safe(exception.Message),
                extensions: new Dictionary<string, object?> { ["code"] = "validation_failed" });
        }
        catch (ContractValidationException exception) { return Results.Json(VersionedContract.Response(exception.Errors), statusCode: 422); }
        catch (PassengerGeneratorUnavailableException exception)
        {
            return Results.Problem(statusCode: 502, title: Safe(exception.Message), extensions: new Dictionary<string, object?> { ["code"] = "passenger_generator_unavailable" });
        }
        catch (NavigationPlannerUnavailableException exception)
        {
            return Results.Problem(statusCode:502,title:Safe(exception.Message),extensions:new Dictionary<string,object?> { ["code"]="navigation_planner_unavailable" });
        }
        catch (ResourcePlannerUnavailableException exception)
        {
            return Results.Problem(statusCode: 502, title: Safe(exception.Message), extensions: new Dictionary<string, object?> { ["code"] = "resource_planner_unavailable" });
        }
        catch (QueueForecasterUnavailableException exception)
        {
            return Results.Problem(statusCode: 502, title: Safe(exception.Message), extensions: new Dictionary<string, object?> { ["code"] = "queue_forecaster_unavailable" });
        }
        catch (AnomalyDetectorUnavailableException exception)
        {
            return Results.Problem(statusCode: 502, title: Safe(exception.Message), extensions: new Dictionary<string, object?> { ["code"] = "anomaly_detector_unavailable" });
        }
        catch (GateAdvisorUnavailableException exception)
        {
            return Results.Problem(statusCode: 502, title: Safe(exception.Message), extensions: new Dictionary<string, object?> { ["code"] = "gate_advisor_unavailable" });
        }
        catch (StaffingPlannerUnavailableException exception)
        {
            return Results.Problem(statusCode: 502, title: Safe(exception.Message), extensions: new Dictionary<string, object?> { ["code"] = "staffing_planner_unavailable" });
        }
        catch (ScenarioNotFoundException exception)
        {
            return Results.Problem(statusCode: 404, title: Safe(exception.Message), extensions: new Dictionary<string, object?> { ["code"] = "resource_not_found" });
        }
        catch (ScenarioConflictException exception)
        {
            return Results.Problem(statusCode: 409, title: Safe(exception.Message), extensions: new Dictionary<string, object?> { ["code"] = "resource_conflict" });
        }
    }
}

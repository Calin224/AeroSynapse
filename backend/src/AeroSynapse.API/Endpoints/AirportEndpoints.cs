using AeroSynapse.Application.Airports;
using AeroSynapse.Domain.Airports;
using AeroSynapse.Domain.DigitalTwin;
using MediatR;

namespace AeroSynapse.API.Endpoints;

public static class AirportEndpoints
{
    public static void MapAirportEndpoints(this WebApplication app)
    {
        app.MapPost("/api/airports/validate", (AirportMap map) =>
        {
            var issues = AirportMapValidator.Validate(map);
            if (issues.Any(issue => issue.Severity == MapIssueSeverity.Error))
                return Results.UnprocessableEntity(new { message = "Invalid airport map.", issues });
            return Results.Ok(new AirportMapSnapshot(map, issues));
        }).WithTags("Airports").WithSummary("Validate an airport map without saving it")
            .Produces<AirportMapSnapshot>().Produces(422).Produces(400);

        app.MapGet("/api/airports", async (ISender sender, CancellationToken token) => Results.Ok(await sender.Send(new ListAirportsQuery(), token)))
            .WithTags("Airports").WithSummary("List airport maps").Produces<IReadOnlyList<Airport>>();
        app.MapPost("/api/airports", async (AirportMap map, ISender sender, CancellationToken cancellationToken) =>
        {
            try
            {
                var snapshot = await sender.Send(new CreateAirportMapCommand(map), cancellationToken);
                return Results.Created($"/api/airports/{snapshot.Map.Airport.Id}/snapshot", snapshot);
            }
            catch (AirportMapValidationException exception)
            {
                return Results.UnprocessableEntity(new { message = "Invalid airport map.", issues = exception.Issues });
            }
            catch (AirportMapConflictException exception)
            {
                return Results.Conflict(new { message = exception.Message });
            }
        }).WithTags("Airports").WithSummary("Validate and create a complete airport map")
            .Produces<AirportMapSnapshot>(201).Produces(422).Produces(409).Produces(400);

        app.MapPost("/api/airports/navigation/preview", (AirportMap map) => ExportNavigation(map))
            .WithTags("Airports").WithSummary("Preview navigation without saving an airport map")
            .Produces<NavigationConfig>().Produces(400).Produces(422);

        app.MapGet("/api/airports/{airportId:guid}/navigation", async (Guid airportId, ISender sender, CancellationToken cancellationToken) =>
        {
            var snapshot = await sender.Send(new GetAirportSnapshotQuery(airportId), cancellationToken);
            if (snapshot is null) return Results.NotFound();
            return ExportNavigation(snapshot.Map);
        }).WithTags("Airports").WithSummary("Export an airport map as a navigation graph")
            .Produces<NavigationConfig>().Produces(404).Produces(422);

        app.MapGet("/api/airports/{airportId:guid}/snapshot", async (Guid airportId, ISender sender, CancellationToken cancellationToken) =>
        {
            var snapshot = await sender.Send(new GetAirportSnapshotQuery(airportId), cancellationToken);
            return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
        }).WithTags("Airports").WithSummary("Get a versioned airport map snapshot")
            .Produces<AirportMapSnapshot>().Produces(404);
    }

    private static IResult ExportNavigation(AirportMap map)
    {
        try
        {
            return Results.Ok(NavigationGraphImporter.FromMap(map));
        }
        catch (AirportMapValidationException exception)
        {
            return Results.UnprocessableEntity(new { message = "Map cannot be exported for navigation.", issues = exception.Issues });
        }
    }
}

using AeroSynapse.Application.Airports;
using AeroSynapse.Domain.Airports;
using MediatR;

namespace AeroSynapse.API.Endpoints;

public static class AirportEndpoints
{
    public static void MapAirportEndpoints(this WebApplication app)
    {
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
            .Produces<AirportMapSnapshot>(201).Produces(422).Produces(409).ProducesProblem(400);

        app.MapGet("/api/airports/{airportId:guid}/snapshot", async (Guid airportId, ISender sender, CancellationToken cancellationToken) =>
        {
            var snapshot = await sender.Send(new GetAirportSnapshotQuery(airportId), cancellationToken);
            return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
        }).WithTags("Airports").WithSummary("Get a versioned airport map snapshot")
            .Produces<AirportMapSnapshot>().Produces(404).ProducesProblem(400);
    }
}

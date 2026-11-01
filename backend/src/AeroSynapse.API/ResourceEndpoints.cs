using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Storage;
using MediatR;

namespace AeroSynapse.API;

internal static class ResourceEndpoints
{
    public static void MapResourceEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/scenarios/{id}/resources").WithTags("Integrated resource planning").AddEndpointFilter<ScenarioErrorFilter>();
        group.MapGet("/settings", async (Guid id, ISender sender, CancellationToken token) =>
            Results.Ok(await sender.Send(new GetResourceSettingsQuery(id), token)))
            .Document<ResourceSettings>("Read opt-in integrated planning data and scenario compatibility");
        group.MapPut("/settings", async (Guid id, JsonElement payload, ISender sender, CancellationToken token) =>
            Results.Ok(await sender.Send(new WriteResourceConfigCommand(id,
                ContractJson.Read<ResourceConfigWriteRequest>(payload, "ResourceConfigWriteRequest")), token)))
            .Accepts<ResourceConfigWriteRequest>("application/json")
            .Document<ScenarioDetails>("Save or disable integrated planning using scenario revision; no operational allocations are applied");
        group.MapPost("/proposals", async (Guid id, JsonElement payload, ISender sender, CancellationToken token) =>
            Results.Ok(await sender.Send(new ProposeResourcesQuery(id,
                ContractJson.Read<ResourceProposalRequest>(payload, "ResourceProposalRequest")), token)))
            .AddEndpointFilter<AeroSynapse.API.Audit.AuditCalculationFilter>().Accepts<ResourceProposalRequest>("application/json")
            .Document<ResourceSolveResult>("Propose advisory gates and handling teams; explain concrete infeasible rules").ProducesProblem(502);
        group.MapPost("/apply", async (Guid id, FlightGateMoveRequest request, ISender sender, CancellationToken token) =>
            Results.Ok(await sender.Send(new MoveFlightGatesCommand(id, request), token)))
            .Document<ScenarioDetails>("Move flights to the proposed gates in one revision, rerouting their passenger flows");
    }
}

using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using MediatR;

namespace AeroSynapse.API;

internal static class NavigationEndpoints
{
    public static void MapNavigationEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/scenarios/{id}/navigation").WithTags("Operational navigation").AddEndpointFilter<ScenarioErrorFilter>();
        group.MapGet("/settings",async (Guid id,ISender sender,CancellationToken token) => Results.Ok(await sender.Send(new GetNavigationSettingsQuery(id),token)))
            .Document<NavigationSettings>("Read captured navigation settings and explicit resource-zone bindings");
        group.MapPut("/settings",async (Guid id,NavigationWriteRequest value,ISender sender,CancellationToken token) => Results.Ok(await sender.Send(new WriteNavigationCommand(id,value),token)))
            .Document<ScenarioDetails>("Atomically save graph, access profiles and corridor closures with revision control");
        group.MapGet("",async (Guid id,int revision,int? offset,int? limit,ISender sender,CancellationToken token) =>
            Results.Ok(await sender.Send(new PreviewNavigationQuery(id,revision,offset??0,limit??100),token)))
            .Document<NavigationResult>("Plan every synthetic passenger's assigned-gate route, with explicit unreachable errors").ProducesProblem(502);
        group.MapPost("/preview",async (Guid id,NavigationWriteRequest value,int? offset,int? limit,ISender sender,CancellationToken token) =>
            value.Config is null ? Results.BadRequest() : Results.Ok(await sender.Send(new PreviewNavigationQuery(id,value.Revision,offset??0,limit??100,value.Config),token)))
            .Document<NavigationResult>("Preview unsaved closures without changing the scenario or historical runs").ProducesProblem(502);
        app.MapGet("/runs/{id}/navigation",async (Guid id,int? offset,int? limit,ISender sender,CancellationToken token) =>
            Results.Ok(await sender.Send(new GetRunNavigationQuery(id,offset??0,limit??100),token)))
            .AddEndpointFilter<ScenarioErrorFilter>().Document<NavigationResult>("Read immutable routes, waypoints and boarding assessment from this completed run");
        app.MapGet("/navigation/airports/{id}",async (Guid id,ISender sender,CancellationToken token) =>
            Results.Ok(await sender.Send(new ImportNavigationMapQuery(id),token)))
            .AddEndpointFilter<ScenarioErrorFilter>().Document<NavigationConfig>("Convert zone/edge data; operational scene bindings remain explicit");
    }
}

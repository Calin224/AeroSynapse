using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Management;
using MediatR;

namespace AeroSynapse.API;

internal static class GateAdviceEndpoints
{
    public static void MapGateAdviceEndpoints(this WebApplication app)
    {
        app.MapPost("/scenarios/{id}/gate-advice", async (Guid id, int revision, ReportedDelay delay, ISender sender, CancellationToken token) =>
            Results.Ok(await sender.Send(new AdviseGatesQuery(id, revision, delay), token)))
            .WithTags("Gate advice").AddEndpointFilter<ScenarioErrorFilter>()
            .Document<GateAdvice>("Recommend the gate plan for a delay reported now, against keeping every gate; nothing is saved")
            .ProducesProblem(502);
    }
}

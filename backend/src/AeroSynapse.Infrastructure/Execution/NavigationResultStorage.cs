using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Domain.DigitalTwin;
using AeroSynapse.Infrastructure.Storage;

namespace AeroSynapse.Infrastructure.Execution;

internal static class NavigationResultStorage
{
    public static void Add(TwinDbContext database,SimulationRun run,SimulationRequest request,SimulationResponse response)
    {
        if (response.ProcessResult is not null || response.NavigationResult is not { } result) return;
        database.Snapshots.Add(new RunSnapshot
        {
            Id=Guid.NewGuid(),SceneId=run.SceneId,RunId=run.Id,IsSynthetic=run.IsSynthetic,ZoneType="scene",ZoneId=run.SceneId,
            RecordedAtUtc=request.StartsAtUtc.AddMinutes(response.SimulatedMinutes).UtcDateTime,
            PayloadJson=JsonSerializer.Serialize(new { kind="navigation/v1",navigationResult=result },ContractJson.Options)
        });
    }
}

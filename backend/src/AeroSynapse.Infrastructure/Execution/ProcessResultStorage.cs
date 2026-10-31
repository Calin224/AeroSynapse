using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Domain.DigitalTwin;
using AeroSynapse.Infrastructure.Storage;

namespace AeroSynapse.Infrastructure.Execution;

internal static class ProcessResultStorage
{
    public static void Add(TwinDbContext database, SimulationRun run, SimulationRequest request, SimulationResponse response)
    {
        if (response.ProcessResult is not { } report) return;
        var payload = new Dictionary<string, object> { ["kind"] = "processes/v1", ["processResult"] = report };
        if (response.NavigationResult is { } navigation) payload["navigationResult"] = navigation;
        if (response.KpiResult is { } kpis) payload["kpiResult"] = kpis;
        database.Snapshots.Add(new RunSnapshot
        {
            Id = Guid.NewGuid(), SceneId = run.SceneId, RunId = run.Id, IsSynthetic = run.IsSynthetic,
            ZoneType = "scene", ZoneId = run.SceneId,
            RecordedAtUtc = request.StartsAtUtc.AddMinutes(response.SimulatedMinutes).UtcDateTime,
            PayloadJson = JsonSerializer.Serialize(payload, ContractJson.Options)
        });
    }
}

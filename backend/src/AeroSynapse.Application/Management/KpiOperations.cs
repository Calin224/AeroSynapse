using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Storage;
using MediatR;

namespace AeroSynapse.Application.Management;

public sealed record KpiWriteRequest(int Revision, int SnapshotIntervalMinutes);
public sealed record WriteKpiSettingsCommand(Guid ScenarioId, KpiWriteRequest Value) : IRequest<ScenarioDetails>;
public sealed record GetRunKpisQuery(Guid RunId, Guid? ZoneId = null, int? FromMinute = null, int? ToMinute = null,
    int Offset = 0, int Limit = 100) : IRequest<KpiResult>;
public sealed class KpiOperations(IScenarioService scenarios, IRunStorageService runs) :
    IRequestHandler<WriteKpiSettingsCommand, ScenarioDetails>, IRequestHandler<GetRunKpisQuery, KpiResult>
{
    public async Task<ScenarioDetails> Handle(WriteKpiSettingsCommand command, CancellationToken token)
    {
        var value = await scenarios.GetAsync(command.ScenarioId, token) ?? throw new ScenarioNotFoundException("Scenario was not found.");
        return await scenarios.UpdateAsync(command.ScenarioId, new(value.Scenario.Code, value.Scenario.Name, value.Scenario.Description,
            value.Scenario.SceneId, value.Scenario.LayoutVersionId, value.Inputs with { SnapshotIntervalMinutes = command.Value.SnapshotIntervalMinutes }, command.Value.Revision), token);
    }
    public async Task<KpiResult> Handle(GetRunKpisQuery query, CancellationToken token)
    {
        if (query.Offset is < 0 or > KpiConventions.MaxSnapshots || query.Limit is < 1 or > KpiConventions.MaxSnapshots
            || query.FromMinute is < 0 or > 10080 || query.ToMinute is < 0 or > 10080
            || query.FromMinute > query.ToMinute) throw Invalid("pagination", "Use 0–50000 for offset, 1–50000 for limit and an ordered time range in 0–10080 minutes.");
        var run = await runs.GetAsync(query.RunId, token) ?? throw new ScenarioNotFoundException("Run was not found.");
        if (!run.ResultsValid) throw new ScenarioConflictException("KPI data is available after the run completes.");
        var snapshots = await runs.SnapshotsAsync(query.RunId, new(ZoneType: "scene", ZoneId: run.SceneId, Limit: 100), token);
        foreach (var snapshot in snapshots.Items)
        {
            using var document = JsonDocument.Parse(snapshot.PayloadJson);
            if (!document.RootElement.TryGetProperty("kpiResult", out var payload)) continue;
            var result = ContractJson.Read<KpiResult>(payload, "KpiResult");
            if (query.ZoneId is { } zone && !result.Snapshots.Any(row => row.ZoneId == zone)) throw Invalid("zoneId", "Use a checkpoint zone captured in this run.");
            var selected = result.Snapshots.Where(row => (query.ZoneId is null || row.ZoneId == query.ZoneId)
                && row.Minute >= (query.FromMinute ?? 0) && row.Minute <= (query.ToMinute ?? result.HorizonMinutes)).ToArray();
            var page = selected.Skip(query.Offset).Take(query.Limit).ToArray();
            return result with { Snapshots = page, MatchedSnapshotCount = selected.Length, Offset = query.Offset,
                ReturnedSnapshotCount = page.Length, HasMore = query.Offset + page.Length < selected.Length };
        }
        throw new ScenarioNotFoundException("This run has no captured KPI series: historical runs and runs with alternative passenger flows do not capture one.");
    }
    private static ScenarioValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });
}

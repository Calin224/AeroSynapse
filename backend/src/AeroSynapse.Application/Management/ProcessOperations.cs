using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Storage;
using MediatR;

namespace AeroSynapse.Application.Management;

public sealed record GetRunProcessesQuery(Guid RunId, int Offset = 0, int Limit = 100) : IRequest<ProcessResult>;
public sealed class ProcessOperations(IRunStorageService runs) : IRequestHandler<GetRunProcessesQuery, ProcessResult>
{
    public async Task<ProcessResult> Handle(GetRunProcessesQuery query, CancellationToken token)
    {
        NavigationOperations.Pagination(query.Offset, query.Limit);
        var run = await runs.GetAsync(query.RunId, token) ?? throw new ScenarioNotFoundException("Run was not found.");
        if (!run.ResultsValid) throw new ScenarioConflictException("The process report is available after a successful run.");
        var snapshots = await runs.SnapshotsAsync(query.RunId, new(ZoneType: "scene", ZoneId: run.SceneId, Limit: 100), token);
        foreach (var snapshot in snapshots.Items)
        {
            using var document = JsonDocument.Parse(snapshot.PayloadJson);
            if (!document.RootElement.TryGetProperty("kind", out var kind) || kind.GetString() != "processes/v1") continue;
            var result = ContractJson.Read<ProcessResult>(document.RootElement.GetProperty("processResult"), "ProcessResult");
            var passengers = result.Passengers.Skip(query.Offset).Take(query.Limit).ToArray();
            return result with { Passengers = passengers, Offset = query.Offset, ReturnedCount = passengers.Length, HasMore = query.Offset + passengers.Length < result.ArrivedCount };
        }
        throw new ScenarioNotFoundException("This run has no SimPy process report: historical runs and runs with alternative passenger flows do not capture one.");
    }
}

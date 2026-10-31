using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Storage;
using MediatR;

namespace AeroSynapse.Application.Management;

public sealed record GetRunJourneyQuery(Guid RunId, int Offset = 0, int Limit = 100) : IRequest<JourneyReport>;
public sealed class JourneyOperations(IRunStorageService runs) : IRequestHandler<GetRunJourneyQuery, JourneyReport>
{
    public async Task<JourneyReport> Handle(GetRunJourneyQuery query, CancellationToken token)
    {
        NavigationOperations.Pagination(query.Offset, query.Limit);
        var run = await runs.GetAsync(query.RunId, token) ?? throw new ScenarioNotFoundException("Run was not found.");
        if (!run.ResultsValid) throw new ScenarioConflictException("The journey report is available after a successful run.");
        var snapshots = await runs.SnapshotsAsync(query.RunId, new(ZoneType: "scene", ZoneId: run.SceneId, Limit: 100), token);
        foreach (var snapshot in snapshots.Items)
        {
            using var document = JsonDocument.Parse(snapshot.PayloadJson);
            if (!document.RootElement.TryGetProperty("kind", out var kind) || kind.GetString() != "journeys/v1") continue;
            var result = ContractJson.Read<JourneyResult>(document.RootElement.GetProperty("journeyResult"), "JourneyResult");
            var people = result.Passengers.Skip(query.Offset).Take(query.Limit).ToArray();
            return new(result.JourneyVersion, result.HorizonMinutes, result.PopulationCount, result.ConnectionPassengerCount,
                result.BoardedCount, result.MissedConnectionCount, result.Plans, result.Snapshots, people, query.Offset,
                people.Length, query.Offset + people.Length < result.PopulationCount);
        }
        throw new ScenarioNotFoundException("This run has no complete journey report. Enable it and start a new run.");
    }
}

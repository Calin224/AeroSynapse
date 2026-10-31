using System.Text.Json;
using System.Text.Json.Nodes;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Operations;
using AeroSynapse.Application.Storage;
using AeroSynapse.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace AeroSynapse.Infrastructure.Operations;

public sealed class RunProgressRecorder(TwinDbContext db) : IRunProgressRecorder
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task RecordAsync(Guid runId, EngineProgress progress, CancellationToken token)
    {
        if (!double.IsFinite(progress.SimulationMinute) || progress.SimulationMinute < 0 || progress.Zones.Count > 1000
            || progress.Zones.Select(x => x.ZoneId).Distinct().Count() != progress.Zones.Count
            || progress.Zones.Any(x => x.QueuedPassengers < 0 || x.ProcessedPassengers < 0 || x.NotArrivedPassengers < 0))
            throw new InvalidOperationException("Invalid engine progress.");
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var run = await db.Runs.FromSqlInterpolated($"SELECT * FROM twin.runs WHERE \"Id\" = {runId} FOR UPDATE").AsNoTracking().SingleOrDefaultAsync(token);
        if (run?.Status != "running" || run.InputSnapshotJson is null) return;
        var input = JsonSerializer.Deserialize<RunInputSnapshot>(run.InputSnapshotJson, Json)!;
        var zones = input.Layout.Layout.Checkpoints.Select(x => x.Id).ToHashSet();
        var passengers = input.Scenario.Inputs.Populations.Sum(x => (long)x.Count);
        // A surge increases the captured demand only from its scheduled minute onward.
        if (input.Engine == KpiConventions.Engine)
            passengers += (ScheduledEvents.Wire(input.Scenario.Inputs) ?? [])
                .Where(item => item.Type == "passenger_surge" && item.Minute <= progress.SimulationMinute)
                .Sum(item => (long)item.PassengerCount!.Value);
        if (progress.SimulationMinute > input.Scenario.Inputs.DurationMinutes || progress.Zones.Any(x => !zones.Contains(x.ZoneId)
            || x.QueuedPassengers > passengers || x.ProcessedPassengers > passengers
            || (long)(x.QueuedPassengers ?? 0) + (x.ProcessedPassengers ?? 0) + (x.NotArrivedPassengers ?? 0) > passengers))
            throw new InvalidOperationException("Engine progress does not match the captured run.");
        // Ignore late frames; an equal-minute identical frame is also a no-op.
        var current = await db.Progress.AsNoTracking().SingleOrDefaultAsync(x => x.RunId == runId, token);
        var payload = JsonSerializer.Serialize(progress.Zones.OrderBy(x => x.ZoneId), Json);
        if (current is not null && (current.SimulationMinute > progress.SimulationMinute
            || (current.SimulationMinute == progress.SimulationMinute && JsonNode.DeepEquals(JsonNode.Parse(current.ZonesJson), JsonNode.Parse(payload))))) return;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO twin.run_progress ("RunId", "SimulationMinute", "ZonesJson")
            VALUES ({runId}, {progress.SimulationMinute}, {payload}::jsonb)
            ON CONFLICT ("RunId") DO UPDATE SET "SimulationMinute" = EXCLUDED."SimulationMinute", "ZonesJson" = EXCLUDED."ZonesJson"
            """, token);
        await transaction.CommitAsync(token);
    }
}

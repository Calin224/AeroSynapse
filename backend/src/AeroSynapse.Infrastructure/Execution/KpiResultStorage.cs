using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Domain.DigitalTwin;
using AeroSynapse.Infrastructure.Storage;

namespace AeroSynapse.Infrastructure.Execution;

internal static class KpiResultStorage
{
    public static void Add(TwinDbContext database, SimulationRun run, SimulationRequest request, KpiResult result)
    {
        foreach (var row in result.Snapshots)
        {
            var time = request.StartsAtUtc.AddMinutes(row.Minute).UtcDateTime;
            database.Snapshots.Add(new RunSnapshot { Id = Guid.NewGuid(), SceneId = run.SceneId, RunId = run.Id,
                IsSynthetic = run.IsSynthetic, ZoneType = "checkpoint", ZoneId = row.ZoneId, RecordedAtUtc = time,
                PayloadJson = JsonSerializer.Serialize(row, ContractJson.Options) });
            if (row.Minute != result.HorizonMinutes) continue;
            foreach (var (name, value, unit) in new (string, double?, string)[]
            {
                ("meanWaitMinutes", row.MeanWaitMinutes, "minutes"), ("medianWaitMinutes", row.MedianWaitMinutes, "minutes"), ("p95WaitMinutes", row.P95WaitMinutes, "minutes"),
                ("throughputPerMinute", row.ThroughputPerMinute, "passengers/minute"), ("cumulativeThroughputPerMinute", row.CumulativeThroughputPerMinute, "passengers/minute"),
                ("maximumQueue", row.MaximumQueue, "passengers"), ("processedPassengers", row.ProcessedPassengers, "passengers"),
                ("waitSampleCount", row.WaitSampleCount, "passengers"), ("utilization", row.Utilization, "fraction"), ("cumulativeUtilization", row.CumulativeUtilization, "fraction"),
                ("boardingLateRisk", row.BoardingLateRisk, "fraction"), ("boardingAtRiskCount", row.BoardingAtRiskCount, "passengers")
            })
                if (value is { } number) database.Metrics.Add(new RunMetric { Id = Guid.NewGuid(), SceneId = run.SceneId, RunId = run.Id,
                    IsSynthetic = run.IsSynthetic, ZoneType = "checkpoint", ZoneId = row.ZoneId, RecordedAtUtc = time,
                    Name = name, Value = decimal.Round((decimal)number, 6, MidpointRounding.ToEven), Unit = unit });
        }
    }

    // Check-in desks are their own zone type, so every checkpoint reader keeps its rows unchanged.
    public static void AddCheckIn(TwinDbContext database, SimulationRun run, SimulationRequest request, CheckInResult result)
    {
        foreach (var row in result.Snapshots)
        {
            var time = request.StartsAtUtc.AddMinutes(row.Minute).UtcDateTime;
            database.Snapshots.Add(new RunSnapshot { Id = Guid.NewGuid(), SceneId = run.SceneId, RunId = run.Id,
                IsSynthetic = run.IsSynthetic, ZoneType = CheckInContractValidation.ZoneType, ZoneId = row.ZoneId, RecordedAtUtc = time,
                PayloadJson = JsonSerializer.Serialize(row, ContractJson.Options) });
            if (row.Minute != request.DurationMinutes) continue;
            foreach (var (name, value, unit) in new (string, double?, string)[]
            {
                ("meanWaitMinutes", row.MeanWaitMinutes, "minutes"), ("p95WaitMinutes", row.P95WaitMinutes, "minutes"),
                ("maximumQueue", row.MaximumQueue, "passengers"), ("processedPassengers", row.ProcessedPassengers, "passengers"),
                ("cumulativeUtilization", row.Utilization, "fraction")
            })
                if (value is { } number) database.Metrics.Add(new RunMetric { Id = Guid.NewGuid(), SceneId = run.SceneId, RunId = run.Id,
                    IsSynthetic = run.IsSynthetic, ZoneType = CheckInContractValidation.ZoneType, ZoneId = row.ZoneId, RecordedAtUtc = time,
                    Name = name, Value = decimal.Round((decimal)number, 6, MidpointRounding.ToEven), Unit = unit });
        }
    }
}

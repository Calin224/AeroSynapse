using System.Globalization;
using System.Text.Json;

namespace AeroSynapse.Application.Contracts;

public static class KpiContractValidation
{
    public static bool Near(double? actual, double? expected) => actual.HasValue == expected.HasValue
        && (!actual.HasValue || Math.Abs(actual.Value - expected!.Value) <= 1e-8 + 1e-12 * Math.Max(Math.Abs(actual.Value), Math.Abs(expected.Value)));
    private static bool Above(double actual, double maximum) => actual > maximum && !Near(actual, maximum);
    public static bool Timestamp(string value, out DateTimeOffset result) => DateTimeOffset.TryParseExact(value,
        ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFF'Z'"], CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out result);
    public static void Snapshot(KpiSnapshot row, string path, List<ContractFieldError> errors)
    {
        void Invalid() => errors.Add(VersionedContract.Error(path, "invalid_result"));
        if (!Timestamp(row.RecordedAtUtc, out _)) errors.Add(VersionedContract.Error(path + "/recordedAtUtc", "invalid_format"));
        var width = row.Minute - row.WindowStartMinute;
        if (row.ElapsedMinutes != row.Minute || width < 0
            || row.ArrivedPassengers + row.NotArrivedPassengers != row.PopulationCount
            || row.QueuedPassengers + row.ProcessingPassengers + row.WalkingPassengers + row.AtGatePassengers + row.UnreachablePassengers != row.ArrivedPassengers
            || row.ProcessedPassengers != row.WalkingPassengers + row.AtGatePassengers
            || row.WaitSampleCount != row.ProcessedPassengers || row.CompletedInWindow > row.ProcessedPassengers
            || row.ProcessingPassengers > row.Capacity || row.PeakBusy > row.Capacity || row.ProcessingPassengers > row.PeakBusy
            || row.MaximumQueue < row.QueuedPassengers || row.MaximumQueue > row.ArrivedPassengers
            || (row.FirstQueueMinute is null) != (row.MaximumQueue == 0) || row.FirstQueueMinute > row.Minute
            || Above(row.BusyMinutesInWindow, row.BusyMinutes) || Above(row.BusyMinutes, row.Capacity * (double)row.Minute)
            || Above(row.BusyMinutesInWindow, row.Capacity * (double)width)
            || row.BoardingAtRiskCount > row.BoardingRiskPopulationCount || row.BoardingRiskPopulationCount > row.PopulationCount
            || (row.EffectiveCapacity ?? 0) > row.Capacity
            || row.LateAtGateCount + row.MissedDeadlineUnfinishedCount != row.BoardingAtRiskCount) Invalid();
        foreach (var value in new[] { row.MeanWaitMinutes, row.MedianWaitMinutes, row.P95WaitMinutes })
            if ((value is null) != (row.WaitSampleCount == 0)) Invalid();
        if (row.P95WaitMinutes < row.MedianWaitMinutes) Invalid();
        if (!Near(row.ThroughputPerMinute, width > 0 ? (double)row.CompletedInWindow / width : null)
            || !Near(row.CumulativeThroughputPerMinute, row.Minute > 0 ? (double)row.ProcessedPassengers / row.Minute : null)
            || !Near(row.Utilization, row.Capacity > 0 && width > 0 ? row.BusyMinutesInWindow / (row.Capacity * (double)width) : null)
            || !Near(row.CumulativeUtilization, row.Capacity > 0 && row.Minute > 0 ? row.BusyMinutes / (row.Capacity * (double)row.Minute) : null)
            || !Near(row.BoardingLateRisk, row.BoardingRiskPopulationCount > 0 ? (double)row.BoardingAtRiskCount / row.BoardingRiskPopulationCount : null)) Invalid();
    }
    public static void Result(JsonElement value, string path, List<ContractFieldError> errors)
    {
        var result = value.Deserialize<KpiResult>(ContractJson.Options)!;
        void Invalid(string field = "") => errors.Add(VersionedContract.Error(path + field, "invalid_result"));
        if (result.ProcessedCount + result.UnprocessedCount != result.PopulationCount
            || result.ArrivedCount + result.NotArrivedCount != result.PopulationCount
            || result.ProcessedCount + result.UnreachableCount > result.ArrivedCount
            || result.ReturnedSnapshotCount != result.Snapshots.Count || result.MatchedSnapshotCount > result.SnapshotCount
            || result.ReturnedSnapshotCount > Math.Max(0, result.MatchedSnapshotCount - result.Offset)
            || result.HasMore != (result.Offset + result.ReturnedSnapshotCount < result.MatchedSnapshotCount)) Invalid();
        var seen = new HashSet<(Guid, int)>();
        foreach (var (row, index) in result.Snapshots.Select((row, index) => (row, index)))
        {
            Snapshot(row, path + $"/snapshots/{index}", errors);
            if (!seen.Add((row.ZoneId, row.Minute)) || row.RunId != result.RunId || row.Minute > result.HorizonMinutes) Invalid($"/snapshots/{index}");
        }
        if (result.Offset != 0 || result.ReturnedSnapshotCount != result.SnapshotCount || result.MatchedSnapshotCount != result.SnapshotCount) return;
        var final = new List<KpiSnapshot>(); var grid = KpiConventions.Minutes(result.HorizonMinutes, result.IntervalMinutes);
        foreach (var group in result.Snapshots.GroupBy(row => row.ZoneId))
        {
            var rows = group.OrderBy(row => row.Minute).ToArray();
            if (!rows.Select(row => row.Minute).SequenceEqual(grid)) { Invalid("/snapshots"); continue; }
            KpiSnapshot? previous = null;
            foreach (var row in rows)
            {
                if (row.WindowStartMinute != (previous?.Minute ?? 0) || row.CompletedInWindow != row.ProcessedPassengers - (previous?.ProcessedPassengers ?? 0)
                    || !Near(row.BusyMinutesInWindow, row.BusyMinutes - (previous?.BusyMinutes ?? 0))
                    || previous is not null && (row.ProcessedPassengers < previous.ProcessedPassengers || row.MaximumQueue < previous.MaximumQueue
                        || row.Capacity != previous.Capacity
                        // Without events demand is fixed; with them a zone population moves only at event minutes and arrivals.
                        || row.PopulationCount != previous.PopulationCount && result.EventCount is null)
                    || row.EffectiveCapacity.HasValue != result.EventCount.HasValue) Invalid("/snapshots");
                previous = row;
            }
            final.Add(rows[^1]);
        }
        if (final.Sum(row => row.PopulationCount) != result.PopulationCount) Invalid("/populationCount");
        if (final.Sum(row => row.ProcessedPassengers) != result.ProcessedCount) Invalid("/processedCount");
        if (final.Sum(row => row.ArrivedPassengers) != result.ArrivedCount) Invalid("/arrivedCount");
        if (final.Sum(row => row.NotArrivedPassengers) != result.NotArrivedCount) Invalid("/notArrivedCount");
        if (final.Sum(row => row.UnreachablePassengers) != result.UnreachableCount) Invalid("/unreachableCount");
    }
}

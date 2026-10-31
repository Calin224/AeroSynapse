namespace AeroSynapse.Application.Contracts;

public static class KpiCorrelation
{
    public static bool Valid(SimulationRequest request, SimulationResponse response)
    {
        // Alternative flows use the stage engine's v1 aggregates, like the process-report convention.
        if (response.KpiResult is not { } result)
            return request.Engine != KpiConventions.Engine || request.PassengerGeneration?.Flow is not null || response.Status != "completed";
        var timeline = new EventTimeline(request);
        var absent = CheckInCorrelation.Absent(response);
        if (request.Engine != result.Engine || result.RunId != request.RunId || result.IntervalMinutes != request.SnapshotIntervalMinutes
            || result.HorizonMinutes != request.DurationMinutes || result.PopulationCount != request.PassengerCount + timeline.SurgeTotal
            || result.EventCount != request.Events?.Count
            || result.Offset != 0 || result.HasMore || result.MatchedSnapshotCount != result.SnapshotCount
            || result.ReturnedSnapshotCount != result.SnapshotCount || response.ProcessResult is not { } processes
            || result.ProcessedCount != processes.Checkpoints.Sum(cp => cp.ProcessedCount)
            || result.ArrivedCount != processes.ArrivedCount || result.NotArrivedCount != processes.NotArrivedCount
            || result.UnreachableCount != processes.UnreachableCount || result.Definitions != KpiConventions.Definitions) return false;
        var zones = request.Checkpoints.OrderBy(cp => cp.CheckpointId.ToString(), StringComparer.Ordinal).ToArray();
        var groups = processes.Passengers.ToLookup(person => person.CheckpointId);
        var snapshots = result.Snapshots.ToLookup(row => row.ZoneId);
        var grid = KpiConventions.Minutes(request.DurationMinutes, result.IntervalMinutes);
        if (result.SnapshotCount != zones.Length * grid.Length || !snapshots.Select(group => group.Key).ToHashSet().SetEquals(zones.Select(cp => cp.CheckpointId))) return false;
        foreach (var (zone, index) in zones.Select((cp, index) => (cp, index)))
        {
            var people = groups[zone.CheckpointId].ToArray();
            // Still at check-in at the horizon: their control's demand from the moment they exist, and boarding risk.
            var stranded = absent.Where(person => person.CheckpointId == zone.CheckpointId)
                .Select(person => (Close: CheckInCorrelation.BoardingClose(timeline, person), From: timeline.Surge(person.Id)?.Event.Minute ?? 0)).ToArray();
            // A redirected arrival belongs to its assigned control until it arrives; surge demand exists from its event minute.
            ProcessPassenger[] Demand(int time) => processes.Passengers.Where(person => timeline.Visible(person, time)
                && (person.ArrivalMinute <= time ? person.CheckpointId : person.AssignedCheckpointId ?? person.CheckpointId) == zone.CheckpointId).ToArray();
            var rows = snapshots[zone.CheckpointId].OrderBy(row => row.Minute).ToArray();
            if (!rows.Select(row => row.Minute).SequenceEqual(grid)) return false;
            foreach (var row in rows)
            {
                var time = row.Minute; var start = row.WindowStartMinute;
                bool Arrived(ProcessPassenger person) => person.ArrivalMinute <= time;
                var waits = people.Where(person => person.SecurityClearedMinute <= time).Select(person => person.QueueWaitMinutes!.Value).Order().ToArray();
                var median = waits.Length == 0 ? (double?)null : (waits[waits.Length / 2] + waits[(waits.Length - 1) / 2]) / 2;
                var p95 = waits.Length == 0 ? (double?)null : waits[(int)Math.Ceiling(.95 * waits.Length) - 1];
                double Area(double begin) => people.Where(person => person.ProcessingStartedMinute is not null)
                    .Sum(person => Math.Max(0, Math.Min(time, person.SecurityClearedMinute ?? time) - Math.Max(begin, person.ProcessingStartedMinute!.Value)));
                var demand = Demand(time);
                var waiting = stranded.Where(person => person.From <= time).ToArray();
                var population = request.PassengerGeneration is null ? request.PassengerCount / zones.Length + (index < request.PassengerCount % zones.Length ? 1 : 0)
                    : demand.Length + waiting.Length;
                var riskPeople = demand.Where(person => person.BoardingCloseMinute is not null).ToArray();
                var strandedRisk = waiting.Where(person => person.Close is not null).ToArray();
                var risk = riskPeople.Count(person => person.BoardingCloseMinute <= time && (person.GateArrivalMinute is null || person.GateArrivalMinute > person.BoardingCloseMinute))
                    + strandedRisk.Count(person => person.Close <= time);
                var late = riskPeople.Count(person => person.GateArrivalMinute <= time && person.GateArrivalMinute > person.BoardingCloseMinute);
                if (row.Capacity != zone.StaffCount || row.PopulationCount != population
                    || row.EffectiveCapacity != (request.Events is null ? null : timeline.Capacity(zone.CheckpointId, time)) || row.WaitSampleCount != waits.Length
                    || !KpiContractValidation.Timestamp(row.RecordedAtUtc, out var recorded) || recorded != request.StartsAtUtc.AddMinutes(time)
                    || row.ArrivedPassengers != people.Count(Arrived)
                    || row.UnreachablePassengers != people.Count(person => Arrived(person) && person.ErrorCode is not null)
                    || row.QueuedPassengers != people.Count(person => Arrived(person) && person.ErrorCode is null && (person.ProcessingStartedMinute is null || person.ProcessingStartedMinute > time))
                    || row.ProcessingPassengers != people.Count(person => person.ProcessingStartedMinute <= time && (person.SecurityClearedMinute is null || person.SecurityClearedMinute > time))
                    || row.WalkingPassengers != people.Count(person => person.SecurityClearedMinute <= time && (person.GateArrivalMinute is null || person.GateArrivalMinute > time))
                    || row.AtGatePassengers != people.Count(person => person.GateArrivalMinute <= time)
                    || !KpiContractValidation.Near(row.MeanWaitMinutes, waits.Length == 0 ? null : waits.Average())
                    || !KpiContractValidation.Near(row.MedianWaitMinutes, median) || !KpiContractValidation.Near(row.P95WaitMinutes, p95)
                    || !KpiContractValidation.Near(row.BusyMinutes, Area(0)) || !KpiContractValidation.Near(row.BusyMinutesInWindow, Area(start))
                    || row.BoardingRiskPopulationCount != riskPeople.Length + strandedRisk.Length || row.BoardingAtRiskCount != risk || row.LateAtGateCount != late) return false;
            }
            var final = processes.Checkpoints.Single(cp => cp.CheckpointId == zone.CheckpointId);
            if (rows[^1].MaximumQueue != final.PeakQueue || rows[^1].PeakBusy != final.PeakBusy) return false;
        }
        return true;
    }
}

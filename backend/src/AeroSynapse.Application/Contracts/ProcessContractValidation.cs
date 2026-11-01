using System.Text.Json;

namespace AeroSynapse.Application.Contracts;

public static class ProcessContractValidation
{
    public static void Validate(JsonElement value, string path, List<ContractFieldError> errors)
    {
        var report = value.Deserialize<ProcessResult>(ContractJson.Options)!;
        void Invalid(string field = "") => errors.Add(VersionedContract.Error(path + field, "invalid_result"));
        if (report.ArrivedCount + report.NotArrivedCount != report.PopulationCount
            || report.QueuedCount + report.ProcessingCount + report.WalkingCount + report.AtGateCount + report.UnreachableCount != report.ArrivedCount
            || report.ReturnedCount != report.Passengers.Count || report.ReturnedCount > Math.Max(0, report.ArrivedCount - report.Offset)
            || report.HasMore != (report.Offset + report.ReturnedCount < report.ArrivedCount)
            || report.DeadlineMissedCount > report.ArrivedCount) Invalid();
        if (report.Checkpoints.Select(cp => cp.CheckpointId).Distinct().Count() != report.Checkpoints.Count) Invalid("/checkpoints");
        if (report.Passengers.Select(person => person.Id).Distinct().Count() != report.Passengers.Count) Invalid("/passengers");
        foreach (var (cp, index) in report.Checkpoints.Select((cp, index) => (cp, index)))
            if (cp.PeakBusy > cp.Capacity || cp.ProcessingCount > cp.PeakBusy || cp.PeakQueue < cp.QueuedCount
                || cp.QueuedCount + cp.ProcessingCount + cp.ProcessedCount != cp.ArrivedCount) Invalid($"/checkpoints/{index}");
        foreach (var (person, index) in report.Passengers.Select((person, index) => (person, index)))
        {
            var prefix = $"/passengers/{index}";
            var start = person.ProcessingStartedMinute; var clear = person.SecurityClearedMinute; var gate = person.GateArrivalMinute;
            bool Present(double? actual, bool wanted) => actual.HasValue == wanted;
            if (person.ArrivalMinute > report.HorizonMinutes || !Present(person.QueueEnteredMinute, person.Stage != "unreachable")
                || !Present(start, person.Stage is "processing" or "walking" or "at_gate")
                || !Present(clear, person.Stage is "walking" or "at_gate") || !Present(gate, person.Stage == "at_gate")
                || !Present(person.QueueWaitMinutes, start.HasValue) || (person.ErrorCode is not null) != (person.Stage == "unreachable"))
            { Invalid(prefix); continue; }
            if (person.QueueEnteredMinute is { } entered && entered != person.ArrivalMinute) Invalid(prefix);
            if (start is { } started && (started < person.ArrivalMinute || started > report.HorizonMinutes
                || Math.Abs(started - person.ArrivalMinute - person.QueueWaitMinutes!.Value) > 1e-8)) Invalid(prefix);
            if (clear is { } cleared && (cleared > report.HorizonMinutes || Math.Abs(cleared - start!.Value - person.ServiceMinutes) > 1e-8)) Invalid(prefix);
            // A corridor closure can hold a passenger at the control exit before walking starts.
            var hold = person.RouteHoldMinutes ?? 0;
            if (gate is { } arrived && (arrived > report.HorizonMinutes || Math.Abs(arrived - clear!.Value - hold - person.WalkingMinutes) > 1e-8)) Invalid(prefix);
            if (person.Stage == "processing" && start!.Value + person.ServiceMinutes <= report.HorizonMinutes) Invalid(prefix);
            if (person.AwaitingRoute == true)
            {
                if (person.Stage != "walking" || person.WalkingMinutes != 0 || Math.Abs(clear!.Value + hold - report.HorizonMinutes) > 1e-8) Invalid(prefix);
            }
            else if (person.Stage == "walking" && clear!.Value + hold + person.WalkingMinutes <= report.HorizonMinutes) Invalid(prefix);
            if (person.RouteHoldMinutes.HasValue && clear is null) Invalid(prefix);
            // Only a chain of moves can bring a passenger back to the control they were assigned.
            var hops = person.Transfers;
            if (person.AssignedCheckpointId == person.CheckpointId && hops is null) Invalid(prefix);
            if (person.RedistributedAtMinute.HasValue != person.TransferMinutes.HasValue || person.RedistributedAtMinute.HasValue != (hops is not null)) Invalid(prefix);
            if (person.RedistributedAtMinute is { } moved && (person.AssignedCheckpointId is null
                || moved < person.ArrivalMinute || moved > report.HorizonMinutes || start < moved + person.TransferMinutes
                // Moves chain from control to control; each starts once the previous walk has ended.
                || hops is null || hops[0].Minute < person.ArrivalMinute || hops[^1].ToCheckpointId != person.CheckpointId
                || hops[^1].Minute != moved || hops[^1].WalkingMinutes != person.TransferMinutes
                || hops.Any(hop => hop.FromCheckpointId == hop.ToCheckpointId)
                || hops.Zip(hops.Skip(1)).Any(pair => pair.Second.FromCheckpointId != pair.First.ToCheckpointId
                    || pair.Second.Minute < pair.First.Minute + pair.First.WalkingMinutes))) Invalid(prefix);
            var missed = person.BoardingCloseMinute is { } close && (gate.HasValue ? gate.Value > close : report.HorizonMinutes >= close);
            var risk = person.Stage == "unreachable" ? "unreachable" : person.BoardingCloseMinute is null ? "not_applicable"
                : gate.HasValue ? missed ? "late" : "safe" : missed ? "missed" : "pending";
            if (person.DeadlineMissed != missed || person.BoardingRisk != risk) Invalid(prefix);
        }
        if (report.Offset != 0 || report.ReturnedCount != report.ArrivedCount) return;
        var stages = report.Passengers.GroupBy(person => person.Stage).ToDictionary(group => group.Key, group => group.Count());
        foreach (var (field, stage, expected) in new[] { ("queuedCount", "queued", report.QueuedCount), ("processingCount", "processing", report.ProcessingCount),
            ("walkingCount", "walking", report.WalkingCount), ("atGateCount", "at_gate", report.AtGateCount), ("unreachableCount", "unreachable", report.UnreachableCount) })
            if (stages.GetValueOrDefault(stage) != expected) Invalid("/" + field);
        if (report.DeadlineMissedCount != report.Passengers.Count(person => person.DeadlineMissed)) Invalid("/deadlineMissedCount");
        var known = report.Checkpoints.Select(cp => cp.CheckpointId).ToHashSet();
        if (report.Passengers.Any(person => !known.Contains(person.CheckpointId))) Invalid("/passengers");
        var groups = report.Passengers.Where(person => person.Stage != "unreachable").ToLookup(person => person.CheckpointId);
        foreach (var (cp, index) in report.Checkpoints.Select((cp, index) => (cp, index)))
        {
            var members = groups[cp.CheckpointId].ToArray();
            if (cp.ArrivedCount != members.Length || cp.QueuedCount != members.Count(person => person.Stage == "queued")
                || cp.ProcessingCount != members.Count(person => person.Stage == "processing")
                || cp.ProcessedCount != members.Count(person => person.Stage is "walking" or "at_gate")) Invalid($"/checkpoints/{index}");
        }
    }
}

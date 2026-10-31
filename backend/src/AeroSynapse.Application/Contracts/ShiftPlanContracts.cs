namespace AeroSynapse.Application.Contracts;

// Shift plans in 15-minute slots: request and CP-SAT result of POST /optimization/staffing/v1/shifts.
public sealed record ShiftRules(int MinShiftSlots, int MaxShiftSlots, int BreakAfterSlots, int TransferMinutes,
    int MaxMovesPerHour, int QueueWeight, int StaffSlotCost, int MoveCost, int TargetUtilizationPercent);
public sealed record ShiftStaffMember(Guid StaffMemberId, Guid TerminalId);
public sealed record ShiftCheckpoint(Guid CheckpointId, Guid TerminalId, int MinimumStaff, int MaximumStaff, int PhysicalLimit,
    bool RequiredOpen, bool Closed, long CapacityMilliPerMinute, IReadOnlyList<int> ArrivalsPerSlot);
public sealed record ShiftPlanRequest(string ContractVersion, Guid ScenarioId, int ScenarioRevision, int SlotMinutes, int SlotCount,
    double TimeLimitSeconds, ShiftRules Rules, IReadOnlyList<ShiftStaffMember> Staff, IReadOnlyList<ShiftCheckpoint> Checkpoints);
public sealed record ShiftSegment(Guid CheckpointId, int StartSlot, int EndSlot);
public sealed record StaffShift(Guid StaffMemberId, int StartSlot, int EndSlot, IReadOnlyList<int> Breaks, IReadOnlyList<ShiftSegment> Segments);
public sealed record ShiftCheckpointPlan(Guid CheckpointId, IReadOnlyList<int> Staff, IReadOnlyList<int> ArrivingMovers, IReadOnlyList<long> QueueMilli);
public sealed record ShiftPlanResult(string Status, string SolverStatus, bool TimedOut, long? Score, long? BestBound, int? DutySlots,
    int? Moves, IReadOnlyList<StaffShift> Shifts, IReadOnlyList<ShiftCheckpointPlan> Checkpoints, ShiftPlanRequest Request,
    string Objective, double WallTimeSeconds);

/// <summary>Mirrors intelligence/app/optimization/shifts.py: the plan is recomputed from the shifts alone.</summary>
public static class ShiftPlanValidation
{
    public const int Slot = 15, Hour = 4;
    private static readonly Dictionary<string, string> Status = new()
    {
        ["OPTIMAL"] = "optimal", ["FEASIBLE"] = "feasible", ["INFEASIBLE"] = "infeasible", ["UNKNOWN"] = "timeout", ["MODEL_INVALID"] = "invalid_model"
    };

    public static void Request(ShiftPlanRequest request, string path, List<ContractFieldError> errors)
    {
        void Duplicates<T>(IReadOnlyList<T> items, Func<T, Guid> key, string name, string field)
        {
            var seen = new HashSet<Guid>();
            for (var index = 0; index < items.Count; index++)
                if (!seen.Add(key(items[index]))) errors.Add(VersionedContract.Error($"{path}/{name}/{index}/{field}", "duplicate_id"));
        }
        Duplicates(request.Staff, item => item.StaffMemberId, "staff", "staffMemberId");
        Duplicates(request.Checkpoints, item => item.CheckpointId, "checkpoints", "checkpointId");
        if (request.Rules.MaxShiftSlots < request.Rules.MinShiftSlots) errors.Add(VersionedContract.Error(path + "/rules/maxShiftSlots", "out_of_range"));
        for (var index = 0; index < request.Checkpoints.Count; index++)
        {
            var checkpoint = request.Checkpoints[index]; var field = $"{path}/checkpoints/{index}";
            if (checkpoint.ArrivalsPerSlot.Count != request.SlotCount) errors.Add(VersionedContract.Error(field + "/arrivalsPerSlot", "comparison_mismatch"));
            if (checkpoint.MaximumStaff < checkpoint.MinimumStaff) errors.Add(VersionedContract.Error(field + "/maximumStaff", "out_of_range"));
            if (checkpoint.RequiredOpen && checkpoint.Closed) errors.Add(VersionedContract.Error(field + "/requiredOpen", "invalid_value"));
        }
        var terminals = request.Checkpoints.GroupBy(item => item.TerminalId).ToDictionary(group => group.Key, group => group.Count());
        if ((long)request.Staff.Sum(person => terminals.GetValueOrDefault(person.TerminalId)) * request.SlotCount > 200_000)
            errors.Add(VersionedContract.Error(path + "/staff", "out_of_range"));
    }

    /// <summary>Per-slot staffing, movers and queues implied by the shifts, and the score the objective gives them.</summary>
    public static (IReadOnlyList<ShiftCheckpointPlan> Plans, int Duty, int Moves, long Score) Evaluate(ShiftPlanRequest request, IReadOnlyList<StaffShift> shifts)
    {
        var slots = request.SlotCount; var rules = request.Rules;
        var checkpoints = request.Checkpoints.OrderBy(item => item.CheckpointId.ToString("D"), StringComparer.Ordinal).ToArray();
        var staff = checkpoints.ToDictionary(item => item.CheckpointId, _ => new int[slots]);
        var movers = checkpoints.ToDictionary(item => item.CheckpointId, _ => new int[slots]);
        int moves = 0, duty = 0;
        foreach (var shift in shifts)
        {
            duty += shift.EndSlot - shift.StartSlot;
            for (var index = 0; index < shift.Segments.Count; index++)
            {
                var segment = shift.Segments[index];
                for (var t = segment.StartSlot; t < segment.EndSlot; t++) staff[segment.CheckpointId][t]++;
                var previous = index > 0 ? shift.Segments[index - 1] : null;
                // Consecutive worked slots at different checkpoints are a move; a break in between is not.
                if (previous is not null && previous.EndSlot == segment.StartSlot && previous.CheckpointId != segment.CheckpointId)
                {
                    movers[segment.CheckpointId][segment.StartSlot]++;
                    moves++;
                }
            }
        }
        var plans = new List<ShiftCheckpointPlan>(); long queued = 0;
        foreach (var item in checkpoints)
        {
            long queue = 0; var row = new long[slots];
            for (var t = 0; t < slots; t++)
            {
                // Plans keep headroom: random arrivals queue well before an operator is fully busy.
                var planned = item.CapacityMilliPerMinute * rules.TargetUtilizationPercent / 100;
                var capacity = planned * (Slot * staff[item.CheckpointId][t] - rules.TransferMinutes * movers[item.CheckpointId][t]);
                queue = Math.Max(0, queue + 1000L * item.ArrivalsPerSlot[t] - capacity);
                row[t] = queue;
            }
            queued += row.Sum();
            plans.Add(new(item.CheckpointId, staff[item.CheckpointId], movers[item.CheckpointId], row));
        }
        return (plans, duty, moves, rules.QueueWeight * queued + (long)rules.StaffSlotCost * duty + (long)rules.MoveCost * moves);
    }

    public static bool Valid(ShiftPlanResult result)
    {
        var request = result.Request;
        if (!Status.TryGetValue(result.SolverStatus, out var state) || result.Status != state || result.TimedOut != state is "feasible" or "timeout") return false;
        if (state is not ("optimal" or "feasible"))
            return result.Shifts.Count == 0 && result.Checkpoints.Count == 0
                && result is { Score: null, BestBound: null, DutySlots: null, Moves: null };
        if (result.Score is not { } score || result.BestBound is not { } bound || bound > score || state == "optimal" && score != bound) return false;
        var slots = request.SlotCount; var rules = request.Rules;
        var people = request.Staff.ToDictionary(person => person.StaffMemberId);
        var checkpoints = request.Checkpoints.ToDictionary(item => item.CheckpointId);
        var seen = new HashSet<Guid>();
        foreach (var shift in result.Shifts)
        {
            var segments = shift.Segments;
            var worked = segments.SelectMany(segment => Enumerable.Range(segment.StartSlot, Math.Max(0, segment.EndSlot - segment.StartSlot))).ToHashSet();
            if (!people.TryGetValue(shift.StaffMemberId, out var person) || !seen.Add(shift.StaffMemberId)
                || !(shift.StartSlot < shift.EndSlot && shift.EndSlot <= slots) || segments.Count == 0
                || shift.EndSlot - shift.StartSlot < rules.MinShiftSlots || shift.EndSlot - shift.StartSlot > rules.MaxShiftSlots
                || segments.Any(segment => segment.StartSlot >= segment.EndSlot)
                || segments.Zip(segments.Skip(1)).Any(pair => pair.Second.StartSlot < pair.First.EndSlot
                    // Adjacent segments at one checkpoint would be one segment.
                    || pair.First.EndSlot == pair.Second.StartSlot && pair.First.CheckpointId == pair.Second.CheckpointId)
                || segments[0].StartSlot != shift.StartSlot || segments[^1].EndSlot != shift.EndSlot
                || !shift.Breaks.SequenceEqual(Enumerable.Range(shift.StartSlot, shift.EndSlot - shift.StartSlot).Where(t => !worked.Contains(t)))
                || segments.Any(segment => !checkpoints.TryGetValue(segment.CheckpointId, out var checkpoint)
                    || checkpoint.TerminalId != person.TerminalId || checkpoint.Closed)
                || Enumerable.Range(0, Math.Max(0, slots - rules.BreakAfterSlots))
                    .Any(k => Enumerable.Range(k, rules.BreakAfterSlots + 1).All(worked.Contains))) return false;
        }
        var (plans, duty, moves, actual) = Evaluate(request, result.Shifts);
        if (result.DutySlots != duty || result.Moves != moves || score != actual || result.Checkpoints.Count != plans.Count
            || result.Checkpoints.Zip(plans).Any(pair => pair.First.CheckpointId != pair.Second.CheckpointId
                || !pair.First.Staff.SequenceEqual(pair.Second.Staff) || !pair.First.ArrivingMovers.SequenceEqual(pair.Second.ArrivingMovers)
                || !pair.First.QueueMilli.SequenceEqual(pair.Second.QueueMilli))) return false;
        foreach (var plan in plans)
        {
            var item = checkpoints[plan.CheckpointId];
            // Required open: staffed in every slot with arrivals, not at an empty checkpoint all day.
            if (plan.Staff.Where((count, t) => count > Math.Min(item.MaximumStaff, item.PhysicalLimit) || count > 0 && count < item.MinimumStaff
                || item.RequiredOpen && item.ArrivalsPerSlot[t] > 0 && count < Math.Max(1, item.MinimumStaff)).Any()) return false;
        }
        return Enumerable.Range(0, slots).All(t => plans.Sum(plan => Enumerable.Range(t, Math.Min(slots, t + Hour) - t).Sum(k => plan.ArrivingMovers[k])) <= rules.MaxMovesPerHour);
    }
}

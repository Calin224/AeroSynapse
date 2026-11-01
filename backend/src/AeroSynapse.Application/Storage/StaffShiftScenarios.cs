using AeroSynapse.Application.Contracts;
using AeroSynapse.Domain.DigitalTwin;

namespace AeroSynapse.Application.Storage;

/// <summary>Scenario staff shifts: who works where and when, and the staffing schedule they give the simulator.</summary>
public static class StaffShiftScenarios
{
    public const int SlotMinutes = 15;
    /// <summary>A shift plan needs a day long enough for real shifts.</summary>
    public const int MinimumDurationMinutes = 120;

    public static void Validate(ScenarioInputs inputs, SceneDetails layout)
    {
        if (inputs.StaffShifts is not { } plan) return;
        var errors = new Dictionary<string, string[]>();
        void Error(string field, string message) => errors.TryAdd(field, [message]);
        if (inputs.DurationMinutes < MinimumDurationMinutes)
            Error("inputs.staffShifts", $"Shift plans need a scenario of at least {MinimumDurationMinutes} minutes.");
        if (plan.SlotMinutes != SlotMinutes) Error("inputs.staffShifts.slotMinutes", $"Shift plans use {SlotMinutes}-minute slots.");
        if (plan.TransferMinutes is < 0 or >= SlotMinutes) Error("inputs.staffShifts.transferMinutes", $"Between 0 and {SlotMinutes - 1} minutes.");
        if (inputs.PassengerGeneration?.Flow is not null) Error("inputs.staffShifts", "Shift plans are not supported with alternative passenger flows.");
        var staff = layout.Staff.ToDictionary(member => member.Id);
        var checkpoints = layout.Checkpoints.ToDictionary(checkpoint => checkpoint.Id);
        var people = new HashSet<Guid>();
        foreach (var (shift, index) in (plan.Shifts ?? []).Select((value, index) => (value, index)))
        {
            var field = $"inputs.staffShifts.shifts[{index}]";
            if (shift?.Segments is not { Count: > 0 } segments) { Error(field, "A shift needs at least one segment."); continue; }
            if (!people.Add(shift.StaffMemberId)) Error(field + ".staffMemberId", "A staff member works one shift per scenario.");
            if (!staff.TryGetValue(shift.StaffMemberId, out var member)) Error(field + ".staffMemberId", "Staff member is not available in the selected layout version.");
            var previousEnd = 0;
            foreach (var (segment, position) in segments.Select((value, position) => (value, position)))
            {
                var part = $"{field}.segments[{position}]";
                if (segment is null) { Error(part, "A segment is required."); continue; }
                if (segment.StartMinute < previousEnd || segment.EndMinute <= segment.StartMinute || segment.EndMinute > inputs.DurationMinutes)
                    Error(part, "Segments are ordered, do not overlap and stay inside the scenario.");
                previousEnd = segment.EndMinute;
                if (!checkpoints.TryGetValue(segment.CheckpointId, out var checkpoint)) Error(part + ".checkpointId", "Checkpoint is not present in the selected layout version.");
                else if (member is not null && member.TerminalId != checkpoint.TerminalId)
                    Error(part + ".checkpointId", "Staff member and checkpoint must belong to the same terminal.");
            }
        }
        if (plan.Shifts is null) Error("inputs.staffShifts.shifts", "An array is required.");
        if (errors.Count != 0) throw new ScenarioValidationException(errors);
    }

    /// <summary>People on duty at each checkpoint, minute by minute; a move straight from another checkpoint starts after the transfer.</summary>
    public static IReadOnlyDictionary<Guid, int[]> Minutes(ScenarioStaffShifts plan, int durationMinutes, IEnumerable<Guid> checkpoints)
    {
        var counts = checkpoints.ToDictionary(id => id, _ => new int[durationMinutes]);
        foreach (var shift in plan.Shifts)
            for (var index = 0; index < shift.Segments.Count; index++)
            {
                var segment = shift.Segments[index];
                var previous = index > 0 ? shift.Segments[index - 1] : null;
                var moved = previous is not null && previous.EndMinute == segment.StartMinute && previous.CheckpointId != segment.CheckpointId;
                if (!counts.TryGetValue(segment.CheckpointId, out var row)) continue;
                for (var minute = segment.StartMinute + (moved ? plan.TransferMinutes : 0); minute < Math.Min(segment.EndMinute, durationMinutes); minute++)
                    row[minute]++;
            }
        return counts;
    }

    /// <summary>People assigned at a checkpoint at the start of each slot; a fixed allocation repeats all day.</summary>
    public static int[] Assigned(ScenarioInputs inputs, Guid checkpointId)
    {
        var slots = (inputs.DurationMinutes + SlotMinutes - 1) / SlotMinutes;
        if (inputs.StaffShifts is not { } plan)
            return Enumerable.Repeat(inputs.StaffAllocations.Count(allocation => allocation.CheckpointId == checkpointId), slots).ToArray();
        return Enumerable.Range(0, slots).Select(slot => plan.Shifts.Count(shift => shift.Segments.Any(segment => segment.CheckpointId == checkpointId
            && segment.StartMinute <= slot * SlotMinutes && slot * SlotMinutes < segment.EndMinute))).ToArray();
    }

    /// <summary>The staff count the simulator sees: the most people a shift plan puts on duty at once, else the fixed allocation.</summary>
    public static int StaffCount(ScenarioInputs inputs, Guid checkpointId) => inputs.StaffShifts is { } plan
        ? Minutes(plan, inputs.DurationMinutes, [checkpointId])[checkpointId].DefaultIfEmpty(0).Max()
        : inputs.StaffAllocations.Count(allocation => allocation.CheckpointId == checkpointId);

    /// <summary>A fixed allocation with these counts per checkpoint. Current people stay first; integrated handling teams keep theirs.</summary>
    public static IReadOnlyList<StaffAllocation> Allocate(ScenarioInputs inputs, SceneDetails layout, IReadOnlyDictionary<Guid, int> counts, string field)
    {
        var assigned = inputs.StaffAllocations.GroupBy(item => item.CheckpointId)
            .SelectMany(group => group.OrderBy(item => item.StaffMemberId).Take(counts.GetValueOrDefault(group.Key))).ToList();
        var taken = assigned.Select(item => item.StaffMemberId)
            .Concat(inputs.IntegratedResources?.Teams.SelectMany(team => team.MemberIds) ?? []).ToHashSet();
        var pools = layout.Staff.Where(item => !taken.Contains(item.Id)).GroupBy(item => item.TerminalId)
            .ToDictionary(group => group.Key, group => new Queue<Guid>(group.OrderBy(item => item.Id).Select(item => item.Id)));
        foreach (var checkpoint in layout.Checkpoints.OrderBy(item => item.Id))
            for (var needed = counts.GetValueOrDefault(checkpoint.Id) - assigned.Count(item => item.CheckpointId == checkpoint.Id); needed > 0; needed--)
            {
                if (!pools.TryGetValue(checkpoint.TerminalId, out var pool) || !pool.TryDequeue(out var id))
                    throw new ScenarioValidationException(new Dictionary<string, string[]> { [field] = ["The staffing exceeds the captured terminal staff pool."] });
                assigned.Add(new(id, checkpoint.Id));
            }
        return assigned.OrderBy(item => item.StaffMemberId).ToArray();
    }

    /// <summary>Step functions for the KPI engine, one per checkpoint; null without shifts.</summary>
    public static IReadOnlyList<StaffSchedule>? Schedules(ScenarioInputs inputs, SceneDetails layout, string? engine)
    {
        if (inputs.StaffShifts is not { } plan) return null;
        if (engine != KpiConventions.Engine)
            throw new ScenarioValidationException(new Dictionary<string, string[]> { ["inputs.staffShifts"] = ["Shift plans need the KPI engine. Launch the scenario again."] });
        return Minutes(plan, inputs.DurationMinutes, layout.Checkpoints.Select(checkpoint => checkpoint.Id)).OrderBy(pair => pair.Key)
            .Select(pair => new StaffSchedule(pair.Key, Steps(pair.Value))).ToArray();
    }

    private static IReadOnlyList<StaffStep> Steps(int[] minutes)
    {
        var steps = new List<StaffStep> { new(0, minutes.Length == 0 ? 0 : minutes[0]) };
        for (var minute = 1; minute < minutes.Length; minute++)
            if (minutes[minute] != minutes[minute - 1]) steps.Add(new(minute, minutes[minute]));
        return steps;
    }
}

using System.Text.Json;

namespace AeroSynapse.Application.Contracts;

/// <summary>From StartMinute on, StaffCount people serve at the checkpoint, until the next step.</summary>
public sealed record StaffStep(int StartMinute, int StaffCount);
/// <summary>People on duty over time at one checkpoint; its StaffCount is then the most at once.</summary>
public sealed record StaffSchedule(Guid CheckpointId, IReadOnlyList<StaffStep> Steps);

/// <summary>Mirrors validate_staff_schedules in intelligence/app/contracts/kpi_validation.py.</summary>
public static class StaffScheduleContractValidation
{
    /// <summary>People scheduled on duty at a time: the last step starting at or before it.</summary>
    public static int PlannedAt(IReadOnlyList<StaffStep> steps, double minute) => steps.Last(step => step.StartMinute <= minute).StaffCount;

    public static void Request(JsonElement value, string path, List<ContractFieldError> errors)
    {
        if (!value.TryGetProperty("staffSchedules", out var schedules)) return;
        // Events must be present, possibly empty, so the KPI report carries the staffing in effect.
        if (!value.TryGetProperty("events", out _)) errors.Add(VersionedContract.Error(path + "/events", "required"));
        var staff = value.GetProperty("checkpoints").EnumerateArray()
            .ToDictionary(cp => cp.GetProperty("checkpointId").GetGuid(), cp => cp.GetProperty("staffCount").GetInt32());
        var horizon = value.GetProperty("durationMinutes").GetInt32();
        var seen = new HashSet<Guid>();
        var index = 0;
        foreach (var schedule in schedules.EnumerateArray())
        {
            var field = $"{path}/staffSchedules/{index++}";
            var key = schedule.GetProperty("checkpointId").GetGuid();
            if (!seen.Add(key)) errors.Add(VersionedContract.Error(field + "/checkpointId", "duplicate_id"));
            if (!staff.TryGetValue(key, out var nominal))
            {
                errors.Add(VersionedContract.Error(field + "/checkpointId", "invalid_value"));
                continue;
            }
            var steps = schedule.GetProperty("steps").EnumerateArray()
                .Select(step => (Minute: step.GetProperty("startMinute").GetInt32(), Count: step.GetProperty("staffCount").GetInt32())).ToArray();
            if (steps[0].Minute != 0 || steps.Zip(steps.Skip(1)).Any(pair => pair.Second.Minute <= pair.First.Minute) || steps[^1].Minute >= horizon)
                errors.Add(VersionedContract.Error(field + "/steps", "invalid_value"));
            if (nominal != steps.Max(step => step.Count)) errors.Add(VersionedContract.Error(field + "/steps", "comparison_mismatch"));
        }
    }
}

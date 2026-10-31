using System.Text.Json;
using System.Text.Json.Serialization;

namespace AeroSynapse.Application.Contracts;

// Check-in desks before security (KPI engine). Times after the horizon are null.
// DeskSteps: desks open and close over time (agent plan), a step function from minute 0 whose highest level is StaffCount.
public sealed record CheckInInput(Guid ZoneId, int StaffCount, double ServiceRatePerMinute, double HoldBaggageShare, double WalkingMinutes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<StaffStep>? DeskSteps = null)
{
    /// <summary>The desks open from each step minute on; without steps every desk is open from minute 0.</summary>
    public (int Start, int Count)[] Levels() => DeskSteps is { Count: > 0 } steps
        ? steps.Select(step => (step.StartMinute, step.StaffCount)).ToArray() : [(0, StaffCount)];
    public int OpenAt(double minute) => Levels().LastOrDefault(step => step.Start <= minute).Count;
    /// <summary>Desks open just before the minute; a desk opening at that minute is not counted.</summary>
    public int OpenBefore(double minute) => Levels().LastOrDefault(step => step.Start < minute).Count;
    /// <summary>Desk-minutes open up to the minute.</summary>
    public double DeskMinutes(double minute)
    {
        var levels = Levels(); var total = 0.0;
        for (var index = 0; index < levels.Length; index++)
        {
            var end = index + 1 < levels.Length ? Math.Min(minute, levels[index + 1].Start) : minute;
            total += levels[index].Count * Math.Max(0, end - levels[index].Start);
        }
        return total;
    }
}
public sealed record CheckInPassenger(string Id, Guid CheckpointId, double TerminalArrivalMinute, double ServiceMinutes,
    double? StartedMinute, double? FinishedMinute, double? SecurityArrivalMinute);
public sealed record CheckInSnapshot(Guid ZoneId, string ZoneType, int Minute, int ArrivedPassengers, int QueuedPassengers,
    int ProcessingPassengers, int ProcessedPassengers, int WalkingPassengers, int MaximumQueue,
    double? MeanWaitMinutes, double? MedianWaitMinutes, double? P95WaitMinutes, int Capacity, double BusyMinutes, double? Utilization);
public sealed record CheckInResult(Guid ZoneId, int Capacity, int PopulationCount, int ProcessedCount, int UnfinishedCount,
    IReadOnlyList<CheckInSnapshot> Snapshots, IReadOnlyList<CheckInPassenger> Passengers);

/// <summary>Check-in invariants mirrored by intelligence/app/contracts/kpi_validation.py.</summary>
public static class CheckInContractValidation
{
    public const string ZoneType = "checkIn";

    // Check-in reshapes generated arrivals, so it needs flight profiles and the single-control route.
    public static void Request(JsonElement value, string path, List<ContractFieldError> errors)
    {
        if (!value.TryGetProperty("checkIn", out var checkIn)) return;
        if (!value.TryGetProperty("passengerGeneration", out var generation)) errors.Add(VersionedContract.Error(path + "/passengerGeneration", "required"));
        else if (generation.TryGetProperty("flow", out var flow) && flow.ValueKind != JsonValueKind.Null) errors.Add(VersionedContract.Error(path + "/checkIn", "invalid_value"));
        var zone = checkIn.GetProperty("zoneId").GetString();
        if (value.GetProperty("checkpoints").EnumerateArray().Any(cp => cp.GetProperty("checkpointId").GetString() == zone))
            errors.Add(VersionedContract.Error(path + "/checkIn/zoneId", "duplicate_id"));
        if (checkIn.TryGetProperty("deskSteps", out var raw))
        {
            // Desks may open and close: a step function from minute 0 whose highest level is staffCount.
            var steps = raw.EnumerateArray().Select(step => (Minute: step.GetProperty("startMinute").GetInt32(), Count: step.GetProperty("staffCount").GetInt32())).ToArray();
            if (steps[0].Minute != 0 || steps.Zip(steps.Skip(1)).Any(pair => pair.Second.Minute <= pair.First.Minute)
                || steps[^1].Minute >= value.GetProperty("durationMinutes").GetInt32() || steps.Max(step => step.Count) != checkIn.GetProperty("staffCount").GetInt32())
                errors.Add(VersionedContract.Error(path + "/checkIn/deskSteps", "invalid_value"));
        }
    }

    public static void Result(JsonElement value, string path, List<ContractFieldError> errors)
    {
        var result = value.Deserialize<CheckInResult>(ContractJson.Options)!;
        void Invalid(string field = "") => errors.Add(VersionedContract.Error(path + field, "invalid_result"));
        var people = result.Passengers;
        if (result.PopulationCount != people.Count || people.Select(person => person.Id).Distinct(StringComparer.Ordinal).Count() != people.Count
            || result.ProcessedCount != people.Count(person => person.FinishedMinute is not null)
            || result.UnfinishedCount != people.Count(person => person.SecurityArrivalMinute is null)) Invalid();
        foreach (var (person, index) in people.Select((person, index) => (person, index)))
            if (person.StartedMinute is null && person.FinishedMinute is not null || person.FinishedMinute is null && person.SecurityArrivalMinute is not null
                || person.StartedMinute < person.TerminalArrivalMinute
                || person.FinishedMinute is { } finish && !KpiContractValidation.Near(finish, person.StartedMinute + person.ServiceMinutes)
                || person.SecurityArrivalMinute < person.FinishedMinute) Invalid($"/passengers/{index}");
        CheckInSnapshot? previous = null;
        foreach (var (row, index) in result.Snapshots.Select((row, index) => (row, index)))
        {
            if (row.ZoneId != result.ZoneId || row.Capacity > result.Capacity
                || row.QueuedPassengers + row.ProcessingPassengers + row.ProcessedPassengers != row.ArrivedPassengers
                || row.WalkingPassengers > row.ProcessedPassengers || row.ProcessingPassengers > result.Capacity
                || row.MaximumQueue < row.QueuedPassengers || row.MaximumQueue > row.ArrivedPassengers
                || (row.Utilization is null) != (row.Capacity == 0 || row.Minute == 0)
                || row.BusyMinutes > result.Capacity * (double)row.Minute && !KpiContractValidation.Near(row.BusyMinutes, result.Capacity * (double)row.Minute)
                || new[] { row.MeanWaitMinutes, row.MedianWaitMinutes, row.P95WaitMinutes }.Any(wait => (wait is null) != (row.ProcessedPassengers == 0))
                || previous is not null && (row.Minute <= previous.Minute || row.ProcessedPassengers < previous.ProcessedPassengers
                    || row.MaximumQueue < previous.MaximumQueue)) Invalid($"/snapshots/{index}");
            previous = row;
        }
    }
}

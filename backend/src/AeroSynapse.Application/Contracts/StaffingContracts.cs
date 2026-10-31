using System.Text.Json;

namespace AeroSynapse.Application.Contracts;

public sealed record StaffingConstraint(Guid CheckpointId, int MinimumStaff, int MaximumStaff, int PhysicalLimit,
    bool RequiredOpen, bool Closed, int ChangeCost);
public sealed record StaffingPool(Guid TerminalId, int AvailableStaff);
public sealed record StaffingCheckpoint(Guid CheckpointId, int MinimumStaff, int MaximumStaff, int PhysicalLimit,
    bool RequiredOpen, bool Closed, int ChangeCost, Guid TerminalId, int CurrentStaff, int DemandMilli, long CapacityMilli);
public sealed record StaffingSolveRequest(string ContractVersion, Guid ScenarioId, int ScenarioRevision,
    int AvailableStaff, double TimeLimitSeconds, IReadOnlyList<StaffingPool> StaffPools, IReadOnlyList<StaffingCheckpoint> Checkpoints);
public sealed record StaffingAssignment(Guid CheckpointId, int StaffCount, long EstimatedCapacityMilli, int UnmetDemandMilli, long ChangePenalty);
public sealed record StaffingSolveResult(string Status, string SolverStatus, bool TimedOut, long? Score, long? BestBound,
    int? UsedStaff, int? UnassignedStaff, IReadOnlyList<StaffingAssignment> Assignments, StaffingSolveRequest Constraints,
    string Objective, int CapacityScale, double WallTimeSeconds);
public sealed record StaffingCandidateSearchRequest(StaffingSolveRequest Constraints, int CandidateCount);
public sealed record StaffingCandidate(IReadOnlyList<StaffingAssignment> Assignments, long Score);
public sealed record StaffingCandidateSearchResult(StaffingCandidateSearchRequest Request, string Status,
    IReadOnlyList<StaffingCandidate> Candidates, double WallTimeSeconds);

public interface IStaffingPlanner
{
    Task<ParetoSearchResult> GenerateParetoAsync(ParetoSearchRequest request, CancellationToken token);
    Task<StaffingSolveResult> SolveAsync(StaffingSolveRequest request, CancellationToken token);
    Task<StaffingCandidateSearchResult> GenerateCandidatesAsync(StaffingCandidateSearchRequest request, CancellationToken token);
    Task<ShiftPlanResult> PlanShiftsAsync(ShiftPlanRequest request, CancellationToken token);
}
public sealed class StaffingPlannerUnavailableException() : Exception("The staffing solver is unavailable or returned an invalid proposal. Check Intelligence and retry.");

public static class StaffingValidation
{
    public const string Objective = "min sum(unmetDemandMilli + changeCost * abs(staffCount - currentStaff))";
    public static bool ValidCandidates(StaffingCandidateSearchRequest request, StaffingCandidateSearchResult result)
    {
        if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(request, ContractJson.Options),
                JsonSerializer.SerializeToElement(result.Request, ContractJson.Options))) return false;
        var errors = new List<ContractFieldError>();
        CandidateResult(result, "", errors);
        return errors.Count == 0;
    }

    public static void CandidateResult(StaffingCandidateSearchResult result, string path, List<ContractFieldError> errors)
    {
        var request = result.Request;
        Request(request.Constraints, path + "/request/constraints", errors);
        if (errors.Count > 0) return;
        if (result.Candidates.Count > request.CandidateCount || result.Status is not ("complete" or "exhausted" or "timeout" or "invalid_model")
            || (result.Status == "complete") != (result.Candidates.Count == request.CandidateCount))
        { errors.Add(VersionedContract.Error(path, "invalid_result")); return; }
        string Key(IEnumerable<(Guid Id, int Count)> counts) => string.Join(";", counts.OrderBy(item => item.Id).Select(item => $"{item.Id}:{item.Count}"));
        var seen = new HashSet<string> { Key(request.Constraints.Checkpoints.Select(item => (item.CheckpointId, item.CurrentStaff))) };
        foreach (var (candidate, index) in result.Candidates.Select((item, index) => (item, index)))
        {
            if (!seen.Add(Key(candidate.Assignments.Select(item => (item.CheckpointId, item.StaffCount)))))
            { errors.Add(VersionedContract.Error(path + $"/candidates/{index}", "invalid_result")); continue; }
            var used = candidate.Assignments.Sum(item => item.StaffCount);
            if (!Valid(request.Constraints, new("feasible", "FEASIBLE", true, candidate.Score, 0, used,
                request.Constraints.AvailableStaff - used, candidate.Assignments, request.Constraints, Objective, 1000, 0)))
                errors.Add(VersionedContract.Error(path + $"/candidates/{index}", "invalid_result"));
        }
    }
    public static void Request(StaffingSolveRequest request, string path, List<ContractFieldError> errors)
    {
        var pools = new HashSet<Guid>();
        foreach (var (pool, index) in request.StaffPools.Select((item, index) => (item, index)))
            if (!pools.Add(pool.TerminalId)) errors.Add(VersionedContract.Error($"{path}/staffPools/{index}/terminalId", "duplicate_id"));
        var ids = new HashSet<Guid>();
        foreach (var (checkpoint, index) in request.Checkpoints.Select((item, index) => (item, index)))
        {
            if (!ids.Add(checkpoint.CheckpointId)) errors.Add(VersionedContract.Error($"{path}/checkpoints/{index}/checkpointId", "duplicate_id"));
            if (!pools.Contains(checkpoint.TerminalId)) errors.Add(VersionedContract.Error($"{path}/checkpoints/{index}/terminalId", "invalid_value"));
        }
        if (request.AvailableStaff > request.StaffPools.Sum(pool => (long)pool.AvailableStaff))
            errors.Add(VersionedContract.Error(path + "/availableStaff", "out_of_range"));
    }

    // Check the external solver's proposal independently before it reaches the operator.
    public static bool Valid(StaffingSolveRequest request, StaffingSolveResult result)
    {
        if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(request, ContractJson.Options),
            JsonSerializer.SerializeToElement(result.Constraints, ContractJson.Options))
            || result.Objective != Objective || result.CapacityScale != 1000) return false;
        var state = result.SolverStatus switch
        {
            "OPTIMAL" => "optimal", "FEASIBLE" => "feasible", "INFEASIBLE" => "infeasible",
            "UNKNOWN" => "timeout", "MODEL_INVALID" => "invalid_model", _ => ""
        };
        if (result.Status != state || result.TimedOut != (state is "feasible" or "timeout")) return false;
        if (state is not ("optimal" or "feasible"))
            return result.Score is null && result.BestBound is null && result.UsedStaff is null
                && result.UnassignedStaff is null && result.Assignments.Count == 0;
        if (result.Score is not { } score || result.BestBound is not { } bound || bound < 0 || bound > score
            || state == "optimal" && bound != score || result.Assignments.Count != request.Checkpoints.Count) return false;
        var constraints = request.Checkpoints.ToDictionary(item => item.CheckpointId);
        var ids = new HashSet<Guid>();
        var pools = request.StaffPools.ToDictionary(pool => pool.TerminalId, _ => 0);
        var used = 0; var actualScore = 0L;
        foreach (var assignment in result.Assignments)
        {
            if (!ids.Add(assignment.CheckpointId) || !constraints.TryGetValue(assignment.CheckpointId, out var checkpoint)) return false;
            var count = assignment.StaffCount;
            if (count < 0 || count > checkpoint.PhysicalLimit || count > checkpoint.MaximumStaff
                || checkpoint.Closed && count != 0 || checkpoint.RequiredOpen && count < Math.Max(1, checkpoint.MinimumStaff)
                || count > 0 && count < checkpoint.MinimumStaff) return false;
            var capacity = (long)count * checkpoint.CapacityMilli;
            var shortage = Math.Max(0, checkpoint.DemandMilli - capacity);
            var change = (long)Math.Abs(count - checkpoint.CurrentStaff) * checkpoint.ChangeCost;
            if (assignment.EstimatedCapacityMilli != capacity || assignment.UnmetDemandMilli != shortage || assignment.ChangePenalty != change) return false;
            used += count; pools[checkpoint.TerminalId] += count; actualScore += shortage + change;
        }
        return actualScore == score && used <= request.AvailableStaff && used == result.UsedStaff
            && result.UnassignedStaff == request.AvailableStaff - used
            && request.StaffPools.All(pool => pools[pool.TerminalId] <= pool.AvailableStaff);
    }
}

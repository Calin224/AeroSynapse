using System.Text.Json;

namespace AeroSynapse.Application.Contracts;

public sealed record ObjectiveWeights(int Time, int Cost, int Risk);
public sealed record ObjectiveNormalization(double TimeMinutes, double CostPoints, double RiskFraction);
public sealed record ParetoOptions(ObjectiveWeights Weights, ObjectiveNormalization Normalization,
    long OperationalBudget, int StaffMinuteCost, double MaxMeanWaitMinutes, double MaxP95WaitMinutes, double MaxBoardingRisk);
public sealed record ParetoSearchRequest(StaffingSolveRequest Constraints, int CandidateCount, int DurationMinutes, ParetoOptions Options);
public sealed record ParetoSolve(string Profile, ObjectiveWeights Weights, string SolverStatus, bool Attempted,
    long? ScaledObjective, long? BestBound, double? ProxyTimeMinutes, double? ProxyRisk, long? OperationalCost,
    IReadOnlyList<StaffingAssignment> Assignments, double WallTimeSeconds);
public sealed record ParetoSearchResult(ParetoSearchRequest Request, string Status, string Formula,
    IReadOnlyList<StaffingCandidate> Candidates, IReadOnlyList<ParetoSolve> Solves, double WallTimeSeconds);

public static class ParetoModel
{
    public const long Scale = 1000000;
    public const string Formula = "sum(round(1000000 * (wT * pressureMinutes/Tref + wC * operatingPoints/Cref + wR * shortageFraction/Rref) / (wT+wC+wR)))";
    public static bool Valid(ParetoOptions? options) => options is { Weights: not null, Normalization: not null }
        && ValidWeights(options.Weights)
        && Finite(options.Normalization.TimeMinutes, .01, 10080) && Finite(options.Normalization.CostPoints, 1, 1000000000000)
        && Finite(options.Normalization.RiskFraction, .001, 1) && options.OperationalBudget is >= 0 and <= 100000000000000
        && options.OperationalBudget / options.Normalization.CostPoints <= 1000000
        && options.StaffMinuteCost is >= 1 and <= 1000000
        && Finite(options.MaxMeanWaitMinutes, 0, 10080) && Finite(options.MaxP95WaitMinutes, 0, 10080)
        && Finite(options.MaxBoardingRisk, 0, 1);
    public static bool ValidWeights(ObjectiveWeights w) => w.Time is >= 0 and <= 1000 && w.Cost is >= 0 and <= 1000
        && w.Risk is >= 0 and <= 1000 && w.Time + w.Cost + w.Risk > 0;
    private static bool Finite(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;
    public static long Cost(StaffingCheckpoint cp, int count, int duration, ParetoOptions options) =>
        (long)count * duration * options.StaffMinuteCost + (long)Math.Abs(count - cp.CurrentStaff) * cp.ChangeCost;
    public static (double Time, double Risk, long Cost, long Score) Values(ParetoSearchRequest request,
        IReadOnlyList<StaffingAssignment> assignments, ObjectiveWeights weights)
    {
        var byId = assignments.ToDictionary(item => item.CheckpointId);
        var demand = Math.Max(1L, request.Constraints.Checkpoints.Sum(item => (long)item.DemandMilli));
        double time = 0, risk = 0; long cost = 0, score = 0;
        foreach (var cp in request.Constraints.Checkpoints.OrderBy(item => item.CheckpointId))
        {
            var count = byId[cp.CheckpointId].StaffCount; var capacity = (long)count * cp.CapacityMilli;
            var t = cp.DemandMilli == 0 ? 0 : (double)request.DurationMinutes * cp.DemandMilli / (cp.DemandMilli + (double)capacity) * cp.DemandMilli / demand;
            var r = Math.Max(0, cp.DemandMilli - capacity) / (double)demand;
            var c = Cost(cp, count, request.DurationMinutes, request.Options);
            var n = request.Options.Normalization;
            var value = (weights.Time * (t / n.TimeMinutes) + weights.Cost * (c / n.CostPoints) + weights.Risk * (r / n.RiskFraction))
                / (weights.Time + weights.Cost + weights.Risk);
            score += checked((long)Math.Floor(Scale * value + .5)); time += t; risk += r; cost += c;
        }
        return (time, risk, cost, score);
    }
    public static bool ValidResult(ParetoSearchRequest request, ParetoSearchResult result)
    {
        if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(request, ContractJson.Options),
                JsonSerializer.SerializeToElement(result.Request, ContractJson.Options)) || result.Formula != Formula
            || !Valid(request.Options) || result.Solves.Count is < 4 or > 8) return false;
        var legacy = new StaffingCandidateSearchRequest(request.Constraints, request.CandidateCount);
        if (!StaffingValidation.ValidCandidates(legacy, new(legacy, result.Status, result.Candidates, result.WallTimeSeconds))) return false;
        string Key(IReadOnlyList<StaffingAssignment> values) => string.Join(";", values.OrderBy(item => item.CheckpointId).Select(item => $"{item.CheckpointId}:{item.StaffCount}"));
        var solved = new HashSet<string>();
        foreach (var solve in result.Solves)
        {
            if (!ValidWeights(solve.Weights) || !double.IsFinite(solve.WallTimeSeconds) || solve.WallTimeSeconds < 0) return false;
            if (!solve.Attempted && solve.SolverStatus != "UNKNOWN") return false;
            if (solve.SolverStatus is not ("OPTIMAL" or "FEASIBLE"))
            {
                if (solve.SolverStatus is not ("INFEASIBLE" or "UNKNOWN" or "MODEL_INVALID") || solve.Assignments.Count != 0
                    || solve.ScaledObjective is not null || solve.BestBound is not null || solve.ProxyTimeMinutes is not null
                    || solve.ProxyRisk is not null || solve.OperationalCost is not null) return false;
                continue;
            }
            var oldScore = solve.Assignments.Sum(item => (long)item.UnmetDemandMilli + item.ChangePenalty);
            var used = solve.Assignments.Sum(item => item.StaffCount);
            if (!solve.Attempted || !StaffingValidation.Valid(request.Constraints, new("feasible", "FEASIBLE", true, oldScore, 0, used,
                    request.Constraints.AvailableStaff - used, solve.Assignments, request.Constraints, StaffingValidation.Objective, 1000, solve.WallTimeSeconds))) return false;
            var actual = Values(request, solve.Assignments, solve.Weights);
            if (solve.Assignments.All(item => item.StaffCount == request.Constraints.Checkpoints.Single(cp => cp.CheckpointId == item.CheckpointId).CurrentStaff)) return false;
            if (actual.Cost > request.Options.OperationalBudget || solve.OperationalCost != actual.Cost || solve.ScaledObjective != actual.Score
                || solve.ProxyTimeMinutes is not { } t || Math.Abs(t - actual.Time) > 1e-7
                || solve.ProxyRisk is not { } r || Math.Abs(r - actual.Risk) > 1e-9
                || solve.BestBound is not { } bound || bound < 0 || bound > actual.Score
                || solve.SolverStatus == "OPTIMAL" && bound != actual.Score) return false;
            solved.Add(Key(solve.Assignments));
        }
        var expected = new[] { request.Options.Weights, new ObjectiveWeights(0, 1, 0), new ObjectiveWeights(1, 0, 0), new ObjectiveWeights(0, 0, 1) };
        if (!result.Solves.Take(4).Select(item => item.Weights).SequenceEqual(expected)) return false;
        if (!result.Solves.Take(4).Select(item => item.Profile).SequenceEqual(new[] { "selected", "cost", "time", "risk" })) return false;
        if (result.Solves.Skip(4).Any(item => item.Weights != request.Options.Weights)) return false;
        return result.Candidates.All(item => solved.Contains(Key(item.Assignments)));
    }
}

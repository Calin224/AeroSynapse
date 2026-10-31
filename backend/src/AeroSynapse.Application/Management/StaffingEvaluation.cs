using AeroSynapse.Application.Contracts;

namespace AeroSynapse.Application.Management;

// Moving people between controls slows the ones they leave. With CheckpointTradeoffs a worse control is a tradeoff,
// not a rejection; the passenger-wide limits above still decide.
public sealed record StaffingAcceptancePolicy(double MaxMeanWaitIncreaseMinutes = 0, double MaxP95IncreaseMinutes = 0,
    double MaxBoardingRiskIncrease = 0, int MaxUnprocessedIncrease = 0, long MaxReallocationCost = 100000000000000,
    bool CheckpointTradeoffs = false);
public sealed record CreateStaffingEvaluationRequest(Guid CommandId, string Name, StaffingProposalRequest Proposal,
    int CandidateCount, StaffingAcceptancePolicy Policy, int TimeLimitSeconds = 120,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] ParetoOptions? MultiObjective = null,
    // With shift rules the evaluation compares today, the best fixed plan and shift plans per 15-minute slot.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] ShiftRules? Shifts = null);
public sealed record StaffingConfiguration(Guid CheckpointId, string Name, int StaffCount);
public sealed record StaffingCheckpointObservation(Guid CheckpointId, int PopulationCount, int ProcessedCount,
    int WaitSampleCount, double? MeanWaitMinutes, double? P95WaitMinutes, int BoardingRiskPopulationCount, int BoardingAtRiskCount);
public sealed record StaffingSimulatedMetrics(int PopulationCount, int ProcessedCount, int UnprocessedCount,
    int WaitSampleCount, double? MeanWaitMinutes, double? P95WaitMinutes, double? BoardingRisk,
    int BoardingAtRiskCount, int BoardingRiskPopulationCount, IReadOnlyList<StaffingCheckpointObservation> Checkpoints);
public sealed record StaffingCandidateAssessment(string Status, IReadOnlyList<string> Reasons);
public sealed record StaffingEvaluatedVariant(Guid VariantId, Guid ScenarioId, Guid RunId, int Position, string RunStatus,
    IReadOnlyList<StaffingConfiguration> Configuration, int ReallocatedPeople, long ReallocationCost,
    StaffingSimulatedMetrics? Metrics, StaffingCandidateAssessment Assessment,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] StaffingSchedule? Schedule = null);
// People assigned at each checkpoint at the start of every slot, and the shifts behind them when the variant has any.
// Arrivals are the sampled passengers per slot the shift planner used, the same for every variant.
public sealed record StaffingScheduleRow(Guid CheckpointId, string Name, IReadOnlyList<int> Staff,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<int>? Arrivals = null);
public sealed record StaffingSchedule(int SlotMinutes, IReadOnlyList<StaffingScheduleRow> Checkpoints,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<AeroSynapse.Domain.DigitalTwin.ScenarioStaffShift>? Shifts = null);
public sealed record StaffingEvaluationDetails(BatchDetails Batch, StaffingCandidateSearchResult Planning,
    StaffingAcceptancePolicy Policy, string Ranking, Guid? RecommendedRunId, IReadOnlyList<StaffingEvaluatedVariant> Variants,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] ParetoAnalysis? Pareto = null);

public static class StaffingEvaluationPolicy
{
    public const string Ranking = "min lexicographic(boardingAtRiskCount, unprocessedCount, meanWaitMinutes, p95WaitMinutes, reallocationCost)";
    private const double Epsilon = 1e-9;

    public static bool Valid(StaffingAcceptancePolicy? value) => value is not null
        && double.IsFinite(value.MaxMeanWaitIncreaseMinutes) && value.MaxMeanWaitIncreaseMinutes is >= 0 and <= 10080
        && double.IsFinite(value.MaxP95IncreaseMinutes) && value.MaxP95IncreaseMinutes is >= 0 and <= 10080
        && double.IsFinite(value.MaxBoardingRiskIncrease) && value.MaxBoardingRiskIncrease is >= 0 and <= 1
        && value.MaxUnprocessedIncrease is >= 0 and <= 100000
        && value.MaxReallocationCost is >= 0 and <= 100000000000000;

    public static StaffingCandidateAssessment Assess(StaffingSimulatedMetrics baseline, StaffingSimulatedMetrics candidate,
        long cost, StaffingAcceptancePolicy policy)
    {
        if (baseline.PopulationCount != candidate.PopulationCount)
            return new("rejected", ["population_mismatch"]);
        if (baseline.BoardingRiskPopulationCount != candidate.BoardingRiskPopulationCount)
            return new("rejected", ["boarding_population_mismatch"]);
        if (baseline.MeanWaitMinutes is not { } mean || candidate.MeanWaitMinutes is not { } otherMean
            || baseline.P95WaitMinutes is not { } p95 || candidate.P95WaitMinutes is not { } otherP95
            || baseline.BoardingRisk is not { } risk || candidate.BoardingRisk is not { } otherRisk)
            return new("unassessable", ["insufficient_observed_metrics"]);
        var reasons = new List<string>();
        if (otherMean - mean > policy.MaxMeanWaitIncreaseMinutes + Epsilon) reasons.Add("mean_wait_degradation");
        if (otherP95 - p95 > policy.MaxP95IncreaseMinutes + Epsilon) reasons.Add("p95_degradation");
        if (otherRisk - risk > policy.MaxBoardingRiskIncrease + Epsilon) reasons.Add("boarding_risk_degradation");
        if (candidate.UnprocessedCount - baseline.UnprocessedCount > policy.MaxUnprocessedIncrease) reasons.Add("unprocessed_degradation");
        if (cost > policy.MaxReallocationCost) reasons.Add("reallocation_cost_limit");
        var deltas = new List<double> { otherMean - mean, otherP95 - p95, otherRisk - risk,
            (double)candidate.UnprocessedCount - baseline.UnprocessedCount };
        var controls = candidate.Checkpoints.ToDictionary(item => item.CheckpointId);
        if (!controls.Keys.ToHashSet().SetEquals(baseline.Checkpoints.Select(item => item.CheckpointId)))
            return new("unassessable", ["checkpoint_metrics_missing"]);
        foreach (var before in baseline.Checkpoints)
        {
            var after = controls[before.CheckpointId];
            void Check(double delta, double limit, string code)
            {
                deltas.Add(delta);
                if (!policy.CheckpointTradeoffs && delta > limit + Epsilon) reasons.Add(code + ":" + before.CheckpointId);
            }
            Check((after.PopulationCount - after.ProcessedCount) - (before.PopulationCount - before.ProcessedCount),
                policy.MaxUnprocessedIncrease, "unprocessed_degradation");
            if (before.PopulationCount == 0 || after.PopulationCount == 0) continue;
            if (before.MeanWaitMinutes is not { } beforeMean || after.MeanWaitMinutes is not { } afterMean
                || before.P95WaitMinutes is not { } beforeP95 || after.P95WaitMinutes is not { } afterP95)
                return new("unassessable", ["insufficient_checkpoint_wait_metrics:" + before.CheckpointId]);
            Check(afterMean - beforeMean, policy.MaxMeanWaitIncreaseMinutes, "mean_wait_degradation");
            Check(afterP95 - beforeP95, policy.MaxP95IncreaseMinutes, "p95_degradation");
            if (before.BoardingRiskPopulationCount > 0 && after.BoardingRiskPopulationCount > 0)
                Check((double)after.BoardingAtRiskCount / after.BoardingRiskPopulationCount - (double)before.BoardingAtRiskCount / before.BoardingRiskPopulationCount,
                    policy.MaxBoardingRiskIncrease, "boarding_risk_degradation");
        }
        if (reasons.Count > 0) return new("rejected", reasons);
        if (deltas.Any(delta => delta > Epsilon)) return new("tradeoff", ["accepted_degradation"]);
        return deltas.Any(delta => delta < -Epsilon) ? new("improved", []) : new("unchanged", ["no_simulated_improvement"]);
    }

    public static Guid? Recommend(IReadOnlyList<StaffingEvaluatedVariant> variants)
    {
        var baseline = variants.Single(item => item.Position == 0);
        if (baseline.Metrics is null || baseline.Assessment.Status != "baseline") return null;
        (int Risk, int Unprocessed, double Mean, double P95, long Cost) Key(StaffingEvaluatedVariant item) =>
            (item.Metrics!.BoardingAtRiskCount, item.Metrics.UnprocessedCount,
                item.Metrics.MeanWaitMinutes ?? double.PositiveInfinity,
                item.Metrics.P95WaitMinutes ?? double.PositiveInfinity, item.ReallocationCost);
        var best = variants.Where(item => item.Assessment.Status is "improved" or "tradeoff")
            .OrderBy(Key).ThenBy(item => item.Position).FirstOrDefault();
        return best is not null && Key(best).CompareTo(Key(baseline)) < 0 ? best.RunId : null;
    }
}

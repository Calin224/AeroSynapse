using AeroSynapse.Application.Management;
using Xunit;

namespace AeroSynapse.Management.Tests;

public sealed class StaffingEvaluationPolicyTests
{
    private static StaffingSimulatedMetrics Metrics(double mean = 2, double p95 = 4, int risk = 10, int unprocessed = 10) =>
        new(100, 100 - unprocessed, unprocessed, 100 - unprocessed, mean, p95, risk / 100d, risk, 100, []);
    private static StaffingEvaluatedVariant Variant(int position, StaffingSimulatedMetrics metrics, long cost, StaffingCandidateAssessment assessment) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), position, "completed", [], position, cost, metrics, assessment);

    [Theory]
    [InlineData("mean_wait_degradation")]
    [InlineData("p95_degradation")]
    [InlineData("boarding_risk_degradation")]
    [InlineData("unprocessed_degradation")]
    [InlineData("reallocation_cost_limit")]
    public void Better_mean_wait_cannot_hide_unaccepted_risk_starvation_percentiles_or_change_cost(string reason)
    {
        var baseline = Metrics(); var candidate = Metrics(mean: 1);
        candidate = reason switch { "mean_wait_degradation" => candidate with { MeanWaitMinutes = 3 },
            "p95_degradation" => candidate with { P95WaitMinutes = 5 },
            "boarding_risk_degradation" => candidate with { BoardingRisk = .11, BoardingAtRiskCount = 11 },
            "unprocessed_degradation" => candidate with { UnprocessedCount = 11, ProcessedCount = 89, WaitSampleCount = 89 }, _ => candidate };
        var result = StaffingEvaluationPolicy.Assess(baseline, candidate, 10, new(MaxReallocationCost: reason == "reallocation_cost_limit" ? 0 : 100));
        Assert.Equal("rejected", result.Status); Assert.Contains(reason, result.Reasons);
    }

    [Fact]
    public void Tolerated_degradation_is_a_tradeoff_and_is_never_labelled_an_improvement()
    {
        var baseline = Metrics(); var candidate = Metrics(mean: 3, risk: 5);
        var assessment = StaffingEvaluationPolicy.Assess(baseline, candidate, 10, new(MaxMeanWaitIncreaseMinutes: 1));
        Assert.Equal("tradeoff", assessment.Status);
        var a = Variant(0, baseline, 0, new("baseline", [])); var b = Variant(1, candidate, 10, assessment);
        Assert.Equal(b.RunId, StaffingEvaluationPolicy.Recommend([a, b])); // Explicit risk-first ranking.
        candidate = Metrics(mean: 1, risk: 11);
        b = Variant(1, candidate, 10, StaffingEvaluationPolicy.Assess(baseline, candidate, 10, new(MaxBoardingRiskIncrease: .01)));
        Assert.Equal("tradeoff", b.Assessment.Status); Assert.Null(StaffingEvaluationPolicy.Recommend([a, b]));
    }

    [Fact]
    public void Identical_or_missing_observations_do_not_produce_a_fake_improvement()
    {
        var baseline = Metrics();
        Assert.Equal("unchanged", StaffingEvaluationPolicy.Assess(baseline, baseline, 10, new()).Status);
        Assert.Equal("unassessable", StaffingEvaluationPolicy.Assess(baseline, baseline with { MeanWaitMinutes = null }, 0, new()).Status);
        Assert.Equal("rejected", StaffingEvaluationPolicy.Assess(baseline, baseline with { PopulationCount = 99 }, 0, new()).Status);
        Assert.Equal("rejected", StaffingEvaluationPolicy.Assess(baseline, baseline with { BoardingRiskPopulationCount = 99 }, 0, new()).Status);
    }

    [Fact]
    public void Recommendation_uses_simulated_performance_and_change_cost_as_the_final_tie_breaker()
    {
        var baseline = Metrics(); var candidate = Metrics(mean: 1, p95: 2);
        var assessment = StaffingEvaluationPolicy.Assess(baseline, candidate, 10, new());
        Assert.Equal("improved", assessment.Status);
        var a = Variant(0, baseline, 0, new("baseline", []));
        var expensive = Variant(1, candidate, 20, assessment); var cheaper = Variant(2, candidate, 10, assessment);
        Assert.Equal(cheaper.RunId, StaffingEvaluationPolicy.Recommend([a, expensive, cheaper]));
        Assert.Null(StaffingEvaluationPolicy.Recommend([a, cheaper with { Assessment = new("failed", []) }]));
    }

    [Fact]
    public void Aggregate_improvement_does_not_hide_a_worse_individual_checkpoint()
    {
        var first = new StaffingCheckpointObservation(Guid.NewGuid(), 50, 45, 45, 2, 4, 50, 5);
        var second = first with { CheckpointId = Guid.NewGuid() };
        var baseline = Metrics() with { Checkpoints = [first, second] };
        var candidate = Metrics(mean: 1.75) with { Checkpoints = [first with { MeanWaitMinutes = 3 }, second with { MeanWaitMinutes = .5 }] };
        var assessment = StaffingEvaluationPolicy.Assess(baseline, candidate, 10, new());
        Assert.Equal("rejected", assessment.Status); Assert.Contains("mean_wait_degradation:" + first.CheckpointId, assessment.Reasons);
        assessment = StaffingEvaluationPolicy.Assess(baseline, candidate, 10, new(MaxMeanWaitIncreaseMinutes: 1));
        Assert.Equal("tradeoff", assessment.Status);
    }

    [Fact]
    public void Checkpoint_tradeoffs_accept_a_slower_control_but_not_a_worse_passenger_wide_result()
    {
        var first = new StaffingCheckpointObservation(Guid.NewGuid(), 50, 45, 45, 2, 4, 50, 5);
        var second = first with { CheckpointId = Guid.NewGuid() };
        var baseline = Metrics() with { Checkpoints = [first, second] };
        // Staff moved to the second control: the first one gets much slower, passengers overall wait less.
        var candidate = Metrics(mean: 1.75, p95: 4.1) with { Checkpoints = [first with { MeanWaitMinutes = 8, P95WaitMinutes = 20 },
            second with { MeanWaitMinutes = .5, P95WaitMinutes = 1 }] };
        var policy = new StaffingAcceptancePolicy(MaxP95IncreaseMinutes: 1, CheckpointTradeoffs: true);
        var assessment = StaffingEvaluationPolicy.Assess(baseline, candidate, 10, policy);
        Assert.Equal("tradeoff", assessment.Status);
        Assert.Equal("rejected", StaffingEvaluationPolicy.Assess(baseline, candidate, 10, policy with { CheckpointTradeoffs = false }).Status);
        var worse = StaffingEvaluationPolicy.Assess(baseline, candidate with { P95WaitMinutes = 5.5 }, 10, policy);
        Assert.Equal("rejected", worse.Status); Assert.Equal(["p95_degradation"], worse.Reasons);
    }
}

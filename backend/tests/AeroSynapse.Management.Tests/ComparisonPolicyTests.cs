using AeroSynapse.Application.Management;
using AeroSynapse.Domain.DigitalTwin;
using Xunit;

namespace AeroSynapse.Management.Tests;

public sealed class ComparisonPolicyTests
{
    private static RunMetric Metric(decimal value) => new() { Value = value, RecordedAtUtc = new DateTime(2026, 10, 16, 10, 0, 0, DateTimeKind.Utc) };
    private static MetricDifference Compare(RunMetric? a, RunMetric? b, bool comparable = true) => RunComparisonPolicy.Difference(Guid.NewGuid(), "meanWaitMinutes", "minutes", a, b, comparable);
    [Theory]
    [InlineData(10, 5, -5, -50)][InlineData(5, 10, 5, 100)][InlineData(10, 10, 0, 0)]
    public void Relative_changes_use_the_valid_baseline_and_keep_the_sign(int a, int b, int delta, int percent)
    { var row = Compare(Metric(a), Metric(b)); Assert.Equal(delta, row.Delta); Assert.Equal(percent, row.PercentChange); Assert.Null(row.UnavailableReason); }
    [Theory][InlineData(0, 0)][InlineData(0, 5)][InlineData(-1, 5)]
    public void Zero_and_negative_denominators_do_not_produce_a_percentage(int a, int b)
    { var row = Compare(Metric(a), Metric(b)); Assert.Equal(b - a, row.Delta); Assert.Null(row.PercentChange); Assert.Equal("baseline_not_positive", row.UnavailableReason); }
    [Fact]
    public void Missing_misaligned_incompatible_and_out_of_range_observations_are_explicit()
    {
        Assert.Equal("metric_unavailable", Compare(Metric(1), null).UnavailableReason);
        Assert.Null(Compare(Metric(1), null).Delta);
        var later = Metric(2); later.RecordedAtUtc = later.RecordedAtUtc.AddMinutes(1);
        var misaligned = Compare(Metric(1), later);
        Assert.Null(misaligned.Delta); Assert.Equal("observation_mismatch", misaligned.UnavailableReason);
        var blocked = Compare(Metric(1), Metric(2), false); Assert.Null(blocked.Delta); Assert.Null(blocked.PercentChange);
        Assert.Equal("percentage_out_of_range", Compare(Metric(.000001m), Metric(decimal.MaxValue)).UnavailableReason);
        Assert.Equal("difference_out_of_range", Compare(Metric(decimal.MinValue), Metric(decimal.MaxValue)).UnavailableReason);
    }
}

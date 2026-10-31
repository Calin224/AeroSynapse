using AeroSynapse.Application.Management;
using Xunit;

namespace AeroSynapse.Contract.Tests;

public sealed class MonteCarloStatisticsTests
{
    [Fact]
    public void Known_sample_has_sample_variance_and_empirical_quantiles()
    {
        var result = MonteCarloStatistics.Describe([3, 1, 2]);
        Assert.Equal(3, result.Count); Assert.Equal(2, result.Mean); Assert.Equal(1, result.StandardDeviation);
        Assert.Equal(1, result.Minimum); Assert.Equal(3, result.Maximum); Assert.Equal(2, result.Median);
        Assert.Equal(1, result.P05); Assert.Equal(3, result.P95);
        Assert.Equal(2.5, MonteCarloStatistics.Describe([1, 2, 3, 4]).Median);
    }
    [Fact]
    public void Missing_samples_are_unknown_and_single_replication_does_not_claim_a_spread()
    {
        var empty = MonteCarloStatistics.Describe([]); Assert.Equal(0, empty.Count); Assert.Null(empty.Mean); Assert.Null(empty.StandardDeviation);
        var single = MonteCarloStatistics.Describe([7]); Assert.Equal(7, single.Mean); Assert.Null(single.StandardDeviation);
        Assert.Throws<ArgumentException>(() => MonteCarloStatistics.Describe([double.NaN]));
        Assert.Equal(0, MonteCarloStatistics.Describe([2, 2, 2]).StandardDeviation);
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using AeroSynapse.Application.Contracts;
using Xunit;

namespace AeroSynapse.Contract.Tests;

public sealed class QueueForecastTests
{
    private static JsonElement Example(string name) => JsonSerializer.SerializeToElement(JsonNode.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "contracts", "forecast", "v1", "examples", name + ".json")))!);

    [Fact]
    public void Python_examples_obey_the_recurrence_units_period_and_strict_alert_threshold()
    {
        var request = ContractJson.Read<QueueForecastRequest>(Example("forecast-request"), "QueueForecastRequest");
        var result = ContractJson.Read<QueueForecastResult>(Example("forecast-response"), "QueueForecastResult");
        Assert.True(QueueForecastValidation.Correlates(request, result));
        Assert.Equal("simulated_estimate", result.Kind); Assert.Equal(15, result.ToMinute - result.FromMinute);
        Assert.Equal(1, result.Checkpoints[0].Points[4].QueuePassengers);
        Assert.Equal(5, result.Checkpoints[0].Alert!.Minute);
        Assert.Equal(new QueueForecastMitigation(1, 0), result.Checkpoints[0].Mitigation);
        var equal = result with { QueueThresholdPassengers = 1, Checkpoints = result.Checkpoints.Select(row => row with { Alert = null, Mitigation = new(0, 1) }).ToArray() };
        Assert.True(QueueForecastValidation.ValidResult(equal));
        Assert.True(QueueForecastValidation.ValidResult(equal with { QueueThresholdPassengers = null,
            Checkpoints = equal.Checkpoints.Select(row => row with { Mitigation = null }).ToArray() }));
        Assert.False(QueueForecastValidation.Correlates(request, result with { RunId = Guid.NewGuid() }));
    }

    [Theory]
    [InlineData("queue")]
    [InlineData("alert")]
    [InlineData("mitigation")]
    [InlineData("period")]
    [InlineData("duplicate")]
    public void Forged_results_are_rejected_by_the_shared_contract(string kind)
    {
        var result = Example("forecast-response").Deserialize<QueueForecastResult>(ContractJson.Options)!;
        var row = result.Checkpoints[0]; var points = row.Points.ToArray();
        if (kind == "queue") { points[4] = points[4] with { QueuePassengers = -1 }; result = result with { Checkpoints = [row with { Points = points }] }; }
        if (kind == "alert") result = result with { Checkpoints = [row with { Alert = null }] };
        if (kind == "mitigation") result = result with { Checkpoints = [row with { Mitigation = new(0, 1) }] };
        if (kind == "period") result = result with { ToMinute = result.ToMinute + 1 };
        if (kind == "duplicate") result = result with { Checkpoints = [row, row] };
        Assert.NotEmpty(VersionedContract.Validate("QueueForecastResult", JsonSerializer.SerializeToElement(result, ContractJson.Options)));
    }

    [Theory]
    [InlineData("origin")]
    [InlineData("duplicate")]
    [InlineData("population")]
    public void Invalid_captured_request_relationships_fail_before_forecasting(string kind)
    {
        var request = Example("forecast-request").Deserialize<QueueForecastRequest>(ContractJson.Options)!;
        if (kind == "origin") request = request with { Options = request.Options with { FromMinute = 31 } };
        if (kind == "duplicate") request = request with { Backlogs = [request.Backlogs[0], request.Backlogs[0]] };
        if (kind == "population") request = request with { Backlogs = [request.Backlogs[0] with { QueuedPassengers = 100 }] };
        Assert.NotEmpty(VersionedContract.Validate("QueueForecastRequest", JsonSerializer.SerializeToElement(request, ContractJson.Options)));
    }
}

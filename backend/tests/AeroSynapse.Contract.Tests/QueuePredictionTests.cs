using System.Text.Json;
using System.Text.Json.Nodes;
using AeroSynapse.Application.Contracts;
using Xunit;

namespace AeroSynapse.Contract.Tests;

public sealed class QueuePredictionTests
{
    private static JsonNode Example(string name) => JsonNode.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "contracts", "forecast", "v1", "examples", name + ".json")))!;

    private static JsonNode Deterministic(JsonNode value)
    {
        value["predictor"] = new JsonObject
        {
            ["kind"] = "deterministic", ["modelVersion"] = QueuePredictionValidation.DeterministicVersion, ["estimator"] = null,
            ["datasetVersion"] = null, ["datasetHash"] = null, ["trainingDataSource"] = null,
            ["formula"] = "Q[t+1]=max(0,Q[t]+arrivals-capacity)", ["fallbackReason"] = "predictor_disabled"
        };
        foreach (var row in value["checkpoints"]!.AsArray())
        {
            row!["predictedQueuePassengers"] = row["deterministicQueuePassengers"]!.DeepClone();
            row["predictionInterval"] = null; row["exceedanceProbability"] = null;
        }
        return value;
    }

    [Fact]
    public void Python_prediction_correlates_with_the_independently_validated_forecast()
    {
        var request = ContractJson.Read<QueueForecastRequest>(JsonSerializer.SerializeToElement(Example("forecast-request")), "QueueForecastRequest");
        var forecast = ContractJson.Read<QueueForecastResult>(JsonSerializer.SerializeToElement(Example("forecast-response")), "QueueForecastResult");
        var prediction = ContractJson.Read<QueuePredictionResult>(JsonSerializer.SerializeToElement(Example("prediction-response")), "QueuePredictionResult");
        Assert.Equal("ml", prediction.Predictor.Kind);
        Assert.Equal("synthetic_simpy_simulation", prediction.Predictor.TrainingDataSource);
        Assert.True(QueuePredictionValidation.Correlates(request, forecast, prediction));
        var fallback = ContractJson.Read<QueuePredictionResult>(JsonSerializer.SerializeToElement(Deterministic(Example("prediction-response"))), "QueuePredictionResult");
        Assert.True(QueuePredictionValidation.Correlates(request, forecast, fallback));
        var forged = prediction with { Checkpoints = [prediction.Checkpoints[0] with { DeterministicQueuePassengers = 1 }] };
        Assert.False(QueuePredictionValidation.Correlates(request, forecast, forged));
        Assert.False(QueuePredictionValidation.Correlates(request with { ObservationSource = "live_simulation" }, forecast, prediction));
        Assert.False(QueuePredictionValidation.Correlates(request, forecast, prediction with { QueueThresholdPassengers = null,
            Checkpoints = [prediction.Checkpoints[0] with { ExceedanceProbability = null }] }));
    }

    [Theory]
    [InlineData("ml")]
    [InlineData("deterministic")]
    [InlineData("interval-on-fallback")]
    [InlineData("disabled-model-version")]
    [InlineData("missing-provenance")]
    [InlineData("probability-without-threshold")]
    [InlineData("missing-probability")]
    [InlineData("inverted-interval")]
    [InlineData("formula-on-ml")]
    [InlineData("horizon")]
    [InlineData("duplicate")]
    [InlineData("real-source")]
    [InlineData("claim")]
    public void Inconsistent_answers_are_rejected_exactly_as_in_python(string kind)
    {
        var value = kind is "deterministic" or "interval-on-fallback" or "disabled-model-version" ? Deterministic(Example("prediction-response")) : Example("prediction-response");
        var row = value["checkpoints"]![0]!;
        if (kind == "interval-on-fallback") row["predictionInterval"] = Example("prediction-response")["checkpoints"]![0]!["predictionInterval"]!.DeepClone();
        if (kind == "disabled-model-version") value["predictor"]!["modelVersion"] = Example("prediction-response")["predictor"]!["modelVersion"]!.DeepClone();
        if (kind == "missing-provenance") value["predictor"]!["datasetHash"] = null;
        if (kind == "probability-without-threshold") value["queueThresholdPassengers"] = null;
        if (kind == "missing-probability") row["exceedanceProbability"] = null;
        if (kind == "inverted-interval") { row["predictionInterval"]!["lowerPassengers"] = 5; row["predictionInterval"]!["upperPassengers"] = 1; }
        if (kind == "formula-on-ml") value["predictor"]!["formula"] = "Q[t+1]=max(0,Q[t]+arrivals-capacity)";
        if (kind == "horizon") value["toMinute"] = value["fromMinute"]!.GetValue<int>() + 31;
        if (kind == "duplicate") value["checkpoints"]!.AsArray().Add(row.DeepClone());
        if (kind == "real-source") value["predictor"]!["trainingDataSource"] = "airport_operations";
        if (kind == "claim") value["validity"] = "validated";
        var errors = VersionedContract.Validate("QueuePredictionResult", JsonSerializer.SerializeToElement(value));
        Assert.Equal(kind is not ("ml" or "deterministic"), errors.Count > 0);
    }
}

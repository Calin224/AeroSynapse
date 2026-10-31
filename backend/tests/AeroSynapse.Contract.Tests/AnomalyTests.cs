using System.Text.Json;
using System.Text.Json.Nodes;
using AeroSynapse.Application.Contracts;
using Xunit;

namespace AeroSynapse.Contract.Tests;

public sealed class AnomalyTests
{
    private static JsonNode Example(string name) => JsonNode.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "contracts", "anomaly", "v1", "examples", name + ".json")))!;
    private static T Read<T>(JsonNode value, string definition) => ContractJson.Read<T>(JsonSerializer.SerializeToElement(value), definition);

    [Fact]
    public void Dotnet_recomputes_the_python_envelope_p_values_episodes_and_calibration_exactly()
    {
        var request = Read<AnomalyDetectionRequest>(Example("detection-request"), "AnomalyDetectionRequest");
        var result = Read<AnomalyDetectionResult>(Example("detection-response"), "AnomalyDetectionResult");
        Assert.True(AnomalyValidation.Correlates(request, result));
        Assert.Equal(1d / (request.ReplicationRuns.Count + 1), result.MinimumPValue);
        var episode = Assert.Single(result.Checkpoints.Single().Episodes);
        Assert.True(episode.Explanation!.Contributions.Demand > 10 * Math.Abs(episode.Explanation.Contributions.Capacity));
        Assert.False(AnomalyValidation.Correlates(request with { Settings = request.Settings with { MinExcessPassengers = 50 } }, result));
    }

    [Theory]
    [InlineData("exceeds")]
    [InlineData("median")]
    [InlineData("episode")]
    [InlineData("peak")]
    [InlineData("calibration")]
    [InlineData("p-value")]
    public void Forged_statistics_do_not_correlate_with_the_replications(string kind)
    {
        var request = Read<AnomalyDetectionRequest>(Example("detection-request"), "AnomalyDetectionRequest");
        var value = Example("detection-response");
        var row = value["checkpoints"]![0]!;
        if (kind == "exceeds") row["exceeds"]![0] = !row["exceeds"]![0]!.GetValue<bool>();
        if (kind == "median") row["median"]![3] = row["median"]![3]!.GetValue<double>() + 0.5;
        if (kind == "episode") row["episodes"] = new JsonArray();
        if (kind == "peak") row["episodes"]![0]!["peakMinute"] = row["episodes"]![0]!["onsetMinute"]!.GetValue<int>();
        if (kind == "calibration") value["nullCalibration"]!["alerts"] = value["nullCalibration"]!["alerts"]!.GetValue<int>() + 1;
        if (kind == "p-value") row["pValue"]![2] = 0.5;
        var result = JsonSerializer.SerializeToElement(value).Deserialize<AnomalyDetectionResult>(ContractJson.Options)!;
        Assert.False(AnomalyValidation.Correlates(request, result));
    }

    [Theory]
    [InlineData("self")]
    [InlineData("seeds")]
    [InlineData("grid")]
    [InlineData("window")]
    [InlineData("arrivals")]
    [InlineData("count")]
    [InlineData("person")]
    [InlineData("scope")]
    public void Inconsistent_or_person_level_requests_are_rejected_as_in_python(string kind)
    {
        var value = Example("detection-request");
        var row = value["checkpoints"]![0]!;
        if (kind == "self") value["replicationRuns"]![0]!["runId"] = value["runId"]!.DeepClone();
        if (kind == "seeds") value["replicationRuns"]![1]!["seed"] = value["replicationRuns"]![0]!["seed"]!.DeepClone();
        if (kind == "grid") value["minutes"]![1] = 2;
        if (kind == "window") { value["settings"]!["windowMinutes"] = 3; value["settings"]!["minExceedances"] = 4; }
        if (kind == "arrivals") { var arrived = row["observed"]!["arrived"]!.AsArray(); arrived[arrived.Count - 1] = 0; }
        if (kind == "count") row["replications"]!.AsArray().RemoveAt(0);
        if (kind == "person") row["observed"]!["passengers"] = new JsonArray(new JsonObject { ["id"] = "p-1" });
        if (kind == "scope") value["dataScope"] = "airport_operations";
        Assert.NotEmpty(VersionedContract.Validate("AnomalyDetectionRequest", JsonSerializer.SerializeToElement(value)));
    }

    [Fact]
    public void Settings_cannot_require_more_exceedances_than_the_window_holds()
    {
        var body = JsonSerializer.SerializeToElement(new { settings = new { upperQuantile = 0.95, windowMinutes = 2, minExceedances = 3, minExcessPassengers = 10 } });
        Assert.NotEmpty(VersionedContract.Validate("AnomalyEvaluationRequest", body));
        var ok = JsonSerializer.SerializeToElement(new { settings = new { upperQuantile = 0.95, windowMinutes = 10, minExceedances = 6, minExcessPassengers = 10 } });
        Assert.Empty(VersionedContract.Validate("AnomalyEvaluationRequest", ok));
    }

    [Fact]
    public void Synthetic_test_set_reports_must_add_up()
    {
        static JsonObject Case(string kind, string outcome) => new()
        {
            ["caseKey"] = "test-08-00", ["scenario"] = 8, ["label"] = new JsonObject { ["kind"] = kind }, ["outcome"] = outcome,
            ["delayMinutes"] = null, ["preOnsetAlerts"] = 0, ["episodes"] = new JsonArray()
        };
        JsonObject Report(JsonObject first, int negatives) => new()
        {
            ["status"] = "ready", ["contractVersion"] = "1.0", ["dataScope"] = "aggregate_synthetic", ["testSetVersion"] = "us039-synthetic-1.0",
            ["testSetHash"] = new string('a', 64), ["settings"] = new JsonObject { ["upperQuantile"] = 0.95, ["windowMinutes"] = 10, ["minExceedances"] = 6, ["minExcessPassengers"] = 10 },
            ["windowSamples"] = 10, ["replicationsPerScenario"] = 15, ["scenarios"] = 1,
            ["confusion"] = new JsonObject { ["true_positive"] = 0, ["false_negative"] = 0, ["false_positive"] = 0, ["true_negative"] = negatives },
            ["recall"] = null, ["falsePositiveRate"] = 0, ["precision"] = null, ["medianDelayMinutes"] = null, ["preOnsetAlerts"] = 0,
            ["byKind"] = new JsonObject(), ["byImpact"] = new JsonObject(), ["nullCheck"] = new JsonObject { ["leaveOneOutAlarmRate"] = 0, ["heldOutFalsePositiveRate"] = 0 },
            ["sweep"] = new JsonArray(), ["cases"] = new JsonArray(first)
        };
        Assert.Empty(VersionedContract.Validate("AnomalyEvaluationResult", JsonSerializer.SerializeToElement(Report(Case("normal", "true_negative"), 1))));
        Assert.NotEmpty(VersionedContract.Validate("AnomalyEvaluationResult", JsonSerializer.SerializeToElement(Report(Case("normal", "true_negative"), 2))));
        Assert.NotEmpty(VersionedContract.Validate("AnomalyEvaluationResult", JsonSerializer.SerializeToElement(Report(Case("normal", "true_positive"), 1))));
        Assert.Empty(VersionedContract.Validate("AnomalyEvaluationResult", JsonSerializer.SerializeToElement(new JsonObject
            { ["status"] = "warming", ["contractVersion"] = "1.0", ["dataScope"] = "aggregate_synthetic" })));
    }
}

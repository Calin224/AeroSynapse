using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AeroSynapse.Application.Contracts;
using Xunit;

namespace AeroSynapse.Contract.Tests;

public sealed class ContractTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "contracts", "v1");
    private static JsonElement Example(string name) => JsonSerializer.SerializeToElement(
        JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "examples", name + ".json")))!);

    public static IEnumerable<object[]> Cases() => new[] { "compatibility-cases.json", "regression-cases.json", "us009-cases.json" }
        .SelectMany(file => JsonNode.Parse(File.ReadAllText(Path.Combine(Root, file)))!.AsArray())
        .Select(item => new object[] { item!["name"]!.GetValue<string>(), item.ToJsonString() });

    [Theory]
    [MemberData(nameof(Cases))]
    public void Shared_compatibility_case(string name, string caseJson)
    {
        var test = JsonNode.Parse(caseJson)!;
        Assert.Equal(name, test["name"]!.GetValue<string>());
        var payload = SharedCorpusTests.Apply(JsonNode.Parse(Example(test["example"]!.GetValue<string>()).GetRawText())!, test["mutations"]!.AsArray());
        var errors = VersionedContract.Validate(test["definition"]!.GetValue<string>(), JsonSerializer.SerializeToElement(payload));
        if (test["status"]!.GetValue<int>() == 200) Assert.Empty(errors);
        else
        {
            Assert.NotEmpty(errors);
            foreach (var expected in test["errors"]!.AsArray())
                Assert.Contains(errors, item => item.Field == expected!["field"]!.GetValue<string>() && item.Code == expected["code"]!.GetValue<string>());
            Assert.Empty(VersionedContract.Validate("ValidationError", JsonSerializer.SerializeToElement(VersionedContract.Response(errors), ContractJson.Options)));
        }
    }

    [Fact]
    public void Published_v1_schema_is_frozen()
    {
        var text = File.ReadAllText(Path.Combine(Root, "contract.schema.json")).Replace("\r\n", "\n");
        Assert.Equal("b5a89bc975afec4756d362f4bd511ec38a58c97ac115dfd7934df9b3481f2099",
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
    }

    [Fact]
    public void Existing_v1_definitions_are_unchanged_by_additive_US009_contracts()
    {
        var baseline = File.ReadAllText(Path.Combine(Root, "baseline.schema.json")).Replace("\r\n", "\n");
        Assert.Equal("8ec8e50de70bf688a7b0af76319d249b507a0e88c68074a10e417d32d82f1f0c",
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(baseline))));
        var previous = JsonNode.Parse(baseline)!;
        var current = VersionedContract.Schema("SimulationRequest");
        foreach (var definition in previous["$defs"]!.AsObject())
            Assert.True(JsonNode.DeepEquals(definition.Value, current["$defs"]![definition.Key]), definition.Key);
    }

    [Fact]
    public void Frozen_scenario_builds_the_shared_request_without_live_data()
    {
        var snapshot = ContractJson.Read<ScenarioSnapshot>(Example("scenario-snapshot"), "ScenarioSnapshot");
        var expected = Example("simulation-request");
        var request = snapshot.ToSimulationRequest(expected.GetProperty("runId").GetGuid());
        Assert.True(JsonElement.DeepEquals(expected, JsonSerializer.SerializeToElement(request, ContractJson.Options)));
        Assert.Throws<ContractValidationException>(() => snapshot.ToSimulationRequest(Guid.Empty));
    }

    [Fact]
    public void Typed_DTOs_read_all_shared_examples_and_emit_valid_contracts()
    {
        RoundTrip<SimulationRequest>("simulation-request", "SimulationRequest");
        RoundTrip<OptimizationRequest>("optimization-request", "OptimizationRequest");
        RoundTrip<SimulationResult>("simulation-result", "SimulationResult");
        RoundTrip<SimulationResult>("simulation-failed", "SimulationResult");
        RoundTrip<OptimizationResult>("optimization-result", "OptimizationResult");
        RoundTrip<OptimizationResult>("optimization-failed", "OptimizationResult");
        RoundTrip<ScenarioSnapshot>("scenario-snapshot", "ScenarioSnapshot");
        RoundTrip<ZoneSnapshot>("zone-snapshot", "ZoneSnapshot");
        RoundTrip<SimulationResponse>("simulation-response", "SimulationResponse");
        RoundTrip<SimulationResponse>("simulation-response-failed", "SimulationResponse");
        RoundTrip<OptimizationResponse>("optimization-response", "OptimizationResponse");
        RoundTrip<OptimizationResponse>("optimization-failed", "OptimizationResponse");
        RoundTrip<ContractErrorResponse>("validation-error", "ValidationError");
        static void RoundTrip<T>(string example, string definition)
        {
            var value = ContractJson.Read<T>(Example(example), definition);
            Assert.Empty(VersionedContract.Validate(definition, JsonSerializer.SerializeToElement(value, ContractJson.Options)));
        }
    }

    [Fact]
    public void Integers_follow_JSON_Schema_numeric_semantics()
    {
        var text = Example("simulation-request").GetRawText().Replace("\"durationMinutes\":60", "\"durationMinutes\":6e1").Replace("\"seed\":42", "\"seed\":42.0");
        using var document = JsonDocument.Parse(text);
        var request = ContractJson.Read<SimulationRequest>(document.RootElement, "SimulationRequest");
        Assert.Equal(60, request.DurationMinutes);
        Assert.Equal(42, request.Seed);
    }

    [Fact]
    public void Response_correlation_rejects_missing_candidates_and_wrong_zones_or_time()
    {
        var request = ContractJson.Read<SimulationRequest>(Example("simulation-request"), "SimulationRequest");
        var result = ContractJson.Read<SimulationResult>(Example("simulation-result"), "SimulationResult");
        Assert.Empty(ContractCorrelation.Validate(request, result));
        var invalid = result with { RunId = Guid.NewGuid(), Metrics = [result.Metrics[0] with { ZoneId = Guid.NewGuid(), RecordedAtUtc = request.StartsAtUtc.AddDays(1) }] };
        Assert.Equal(new[] { "/runId", "/metrics/0/zoneId", "/metrics/0/recordedAtUtc" }, ContractCorrelation.Validate(request, invalid).Select(x => x.Field));
        var optimization = ContractJson.Read<OptimizationRequest>(Example("optimization-request"), "OptimizationRequest");
        var comparison = ContractJson.Read<OptimizationResult>(Example("optimization-result"), "OptimizationResult");
        Assert.Empty(ContractCorrelation.Validate(optimization, comparison));
        Assert.Contains(ContractCorrelation.Validate(optimization, comparison with { Candidates = [] }), x => x.Field == "/candidates");
        var response = ContractJson.Read<SimulationResponse>(Example("simulation-response"), "SimulationResponse");
        Assert.Empty(ContractCorrelation.Validate(request, response));
        Assert.Contains(ContractCorrelation.Validate(request, response with { SimulatedMinutes = 61 }), x => x.Field == "/simulatedMinutes");
        Assert.Contains(ContractCorrelation.Validate(request, response with { SimulatedMinutes = 20 }), x => x.Field == "/metrics/0/recordedAtUtc");
        Assert.Contains(ContractCorrelation.Validate(request, response with { SimulatedMinutes = 20 }), x => x.Field == "/snapshots/0/elapsedMinutes");
        var farFutureRequest = request with { StartsAtUtc = new DateTimeOffset(9999, 12, 31, 22, 0, 0, TimeSpan.Zero) };
        Assert.Contains(ContractCorrelation.Validate(farFutureRequest, response with { SimulatedMinutes = 10080 }), x => x.Field == "/simulatedMinutes");
        Assert.Empty(ContractCorrelation.Validate(optimization, ContractJson.Read<OptimizationResponse>(Example("optimization-response"), "OptimizationResponse")));
    }

    [Fact]
    public void Duplicate_JSON_fields_are_explicit_errors()
    {
        using var json = JsonDocument.Parse("""{"checkpoints":[{"staffCount":1,"staffCount":2}]}""");
        Assert.Equal(VersionedContract.Error("/checkpoints/0/staffCount", "duplicate_field"), Assert.Single(VersionedContract.DuplicateFields(json.RootElement)));
    }

    [Theory]
    [InlineData("""{"text":"\ud800"}""")]
    [InlineData("""{"\udc00":1}""")]
    public void Unpaired_surrogates_return_a_controlled_wire_error(string text)
    {
        using var document = JsonDocument.Parse(text);
        Assert.Equal(VersionedContract.Error("", "malformed_json"), Assert.Single(VersionedContract.Validate("SimulationRequest", document.RootElement)));
    }

    [Theory]
    [InlineData("1e1000")]
    [InlineData("1e-1000")]
    [InlineData("-1e-1000")]
    public void Unrepresentable_numbers_do_not_reach_typed_DTOs(string number)
    {
        var text = Example("simulation-request").GetRawText().Replace("\"arrivalRatePerMinute\":2", "\"arrivalRatePerMinute\":" + number);
        using var document = JsonDocument.Parse(text);
        Assert.Equal(VersionedContract.Error("/arrivalRatePerMinute", "out_of_range"), Assert.Single(VersionedContract.Validate("SimulationRequest", document.RootElement)));
        Assert.Throws<ContractValidationException>(() => ContractJson.Read<SimulationRequest>(document.RootElement, "SimulationRequest"));
    }

    [Fact]
    public void Typed_DTO_reads_reject_duplicate_fields_and_report_each_path_once()
    {
        var text = Example("simulation-request").GetRawText().Replace("\"seed\":42", "\"seed\":1,\"seed\":2,\"seed\":3");
        using var document = JsonDocument.Parse(text);
        var exception = Assert.Throws<ContractValidationException>(() => ContractJson.Read<SimulationRequest>(document.RootElement, "SimulationRequest"));
        Assert.Equal(VersionedContract.Error("/seed", "duplicate_field"), Assert.Single(exception.Errors));
    }

    [Fact]
    public void Fractional_integer_tokens_cannot_be_rounded_into_valid_inputs()
    {
        var text = Example("simulation-request").GetRawText().Replace("\"seed\":42", "\"seed\":42.00000000000000000000000000000001");
        using var document = JsonDocument.Parse(text);
        Assert.Contains(VersionedContract.Validate("SimulationRequest", document.RootElement), item => item.Field == "/seed" && item.Code == "invalid_type");
        Assert.Throws<ContractValidationException>(() => ContractJson.Read<SimulationRequest>(document.RootElement, "SimulationRequest"));
    }

    [Fact]
    public void Invalid_UUID_format_does_not_emit_negated_const_diagnostics()
    {
        var payload = JsonNode.Parse(Example("simulation-request").GetRawText())!;
        payload["runId"] = "bad-id";
        Assert.Equal(VersionedContract.Error("/runId", "invalid_format"),
            Assert.Single(VersionedContract.Validate("SimulationRequest", JsonSerializer.SerializeToElement(payload))));
    }

    [Fact]
    public void Mathematical_zero_with_large_exponent_survives_DTO_read()
    {
        var text = Example("simulation-request").GetRawText().Replace("\"seed\":42", "\"seed\":0e1000");
        using var document = JsonDocument.Parse(text);
        Assert.Equal(0, ContractJson.Read<SimulationRequest>(document.RootElement, "SimulationRequest").Seed);
    }
}

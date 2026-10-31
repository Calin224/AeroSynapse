using System.Text.Json;
using System.Text.Json.Nodes;
using AeroSynapse.Application.Contracts;
using Xunit;

namespace AeroSynapse.Contract.Tests;

public sealed class ProcessTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "contracts", "processes", "v1");
    private static JsonElement Example(string name) => JsonSerializer.SerializeToElement(JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "examples", name + ".json")))!);
    public static IEnumerable<object[]> Cases() => JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "validation-cases.json")))!.AsArray()
        .Select(item => new object[] { item!["name"]!.GetValue<string>(), item.ToJsonString() });
    [Theory]
    [MemberData(nameof(Cases))]
    public void Shared_process_case_matches_Python(string name, string caseJson)
    {
        var test = JsonNode.Parse(caseJson)!; Assert.Equal(name, test["name"]!.GetValue<string>());
        var payload = JsonNode.Parse(Example(test["example"]!.GetValue<string>()).GetRawText())!;
        foreach (var mutation in test["mutations"]!.AsArray())
        {
            var parts = mutation!["path"]!.GetValue<string>().Split('/').Skip(1).ToArray(); var parent = payload;
            foreach (var part in parts.SkipLast(1)) parent = parent is JsonArray ? parent[int.Parse(part)]! : parent[part]!;
            if (parent is JsonArray array) array[int.Parse(parts.Last())] = mutation["value"]?.DeepClone(); else parent[parts.Last()] = mutation["value"]?.DeepClone();
        }
        var errors = VersionedContract.Validate(test["definition"]!.GetValue<string>(), JsonSerializer.SerializeToElement(payload));
        Assert.Equal(test["errors"]!.AsArray().Select(item => (item!["field"]!.GetValue<string>(), item["code"]!.GetValue<string>())), errors.Select(item => (item.Field, item.Code)));
    }
    [Theory]
    [InlineData("simpy-request", "simpy-response")]
    [InlineData("empty-request", "empty-response")]
    public void Engine_and_actual_process_results_roundtrip_and_correlate(string input, string output)
    {
        var request = ContractJson.Read<SimulationRequest>(Example(input), "SimulationRequest");
        var response = ContractJson.Read<SimulationResponse>(Example(output), "SimulationResponse");
        Assert.True(JsonElement.DeepEquals(Example(input), JsonSerializer.SerializeToElement(request, ContractJson.Options)));
        Assert.True(JsonElement.DeepEquals(Example(output), JsonSerializer.SerializeToElement(response, ContractJson.Options)));
        Assert.Empty(ContractCorrelation.Validate(request, response));
        Assert.Contains(ContractCorrelation.Validate(request, response with { ProcessResult = null }), item => item.Field == "/processResult");
    }
    [Fact]
    public void Only_runs_with_alternative_flows_may_omit_the_process_report()
    {
        var request = ContractJson.Read<SimulationRequest>(Example("simpy-request"), "SimulationRequest");
        var response = ContractJson.Read<SimulationResponse>(Example("simpy-response"), "SimulationResponse") with { ProcessResult = null, NavigationResult = null };
        Assert.False(ProcessCorrelation.Valid(request, response));
        var flow = new AeroSynapse.Domain.DigitalTwin.PassengerFlowConfig([new("standard", 1, false, "security", 1, 1)], [], []);
        Assert.True(ProcessCorrelation.Valid(request with { PassengerGeneration = request.PassengerGeneration! with { Flow = flow } }, response));
    }
    [Fact]
    public void Replaying_service_intervals_detects_overlapping_jobs_above_resource_capacity()
    {
        var request = ContractJson.Read<SimulationRequest>(Example("simpy-request"), "SimulationRequest");
        var response = ContractJson.Read<SimulationResponse>(Example("simpy-response"), "SimulationResponse");
        var report = response.ProcessResult!;
        var second = report.Passengers[1] with { ProcessingStartedMinute = 0, QueueWaitMinutes = 0, SecurityClearedMinute = 1, GateArrivalMinute = 3, DeadlineMissed = false, BoardingRisk = "safe" };
        var forged = report with { Passengers = [report.Passengers[0], second], DeadlineMissedCount = 0,
            Checkpoints = [report.Checkpoints[0] with { MeanWaitMinutes = 0 }] };
        Assert.False(ProcessCorrelation.Valid(request, response with { ProcessResult = forged,
            Metrics = [response.Metrics[0] with { MeanWaitMinutes = 0 }] }));
    }
}

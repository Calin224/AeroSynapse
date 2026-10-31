using System.Text.Json;
using System.Text.Json.Nodes;
using AeroSynapse.Application.Contracts;
using Xunit;

namespace AeroSynapse.Contract.Tests;

// Agent decisions are inputs: desks opened during the run and how each delay is absorbed must correlate like any capture.
public sealed class AgentDecisionContractTests
{
    private static JsonElement Example(string contract, string name) => JsonSerializer.SerializeToElement(JsonNode.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "contracts", contract, "v1", "examples", name + ".json")))!);

    [Theory]
    [InlineData("kpis", "check-in-desks")]
    [InlineData("journeys", "gate-choice")]
    public void Python_decision_runs_roundtrip_and_correlate(string contract, string name)
    {
        var request = ContractJson.Read<SimulationRequest>(Example(contract, name + "-request"), "SimulationRequest");
        var response = ContractJson.Read<SimulationResponse>(Example(contract, name + "-response"), "SimulationResponse");
        Assert.True(JsonElement.DeepEquals(Example(contract, name + "-request"), JsonSerializer.SerializeToElement(request, ContractJson.Options)));
        Assert.True(JsonElement.DeepEquals(Example(contract, name + "-response"), JsonSerializer.SerializeToElement(response, ContractJson.Options)));
        Assert.Empty(ContractCorrelation.Validate(request, response));
    }

    [Fact]
    public void Opened_desks_follow_their_steps_and_cannot_be_moved_earlier()
    {
        var request = ContractJson.Read<SimulationRequest>(Example("kpis", "check-in-desks-request"), "SimulationRequest");
        var response = ContractJson.Read<SimulationResponse>(Example("kpis", "check-in-desks-response"), "SimulationResponse");
        var input = request.CheckIn!;
        Assert.All(response.CheckInResult!.Snapshots, row => Assert.Equal(input.OpenAt(row.Minute), row.Capacity));
        // Claiming the extra desk from minute 0 breaks the reported capacity.
        var early = input with { DeskSteps = [new(0, input.StaffCount)] };
        Assert.NotEmpty(ContractCorrelation.Validate(request with { CheckIn = early }, response));
    }

    [Fact]
    public void Desk_steps_allow_closures_and_gate_choices_name_the_runs_delays()
    {
        var desks = JsonNode.Parse(Example("kpis", "check-in-desks-request").GetRawText())!;
        desks["checkIn"]!["deskSteps"] = new JsonArray(new JsonObject { ["startMinute"] = 0, ["staffCount"] = 2 }, new JsonObject { ["startMinute"] = 10, ["staffCount"] = 1 });
        Assert.Empty(VersionedContract.Validate("KpiSimulationRequest", JsonSerializer.SerializeToElement(desks)));
        desks["checkIn"]!["deskSteps"]![0]!["startMinute"] = 1;
        Assert.Contains(VersionedContract.Validate("KpiSimulationRequest", JsonSerializer.SerializeToElement(desks)), item => item.Field.EndsWith("/checkIn/deskSteps"));
        var gates = JsonNode.Parse(Example("journeys", "gate-choice-request").GetRawText())!;
        gates["journey"]!["gateChoices"]![0]!["eventId"] = "71000000-0000-0000-0000-000000000099";
        Assert.Contains(VersionedContract.Validate("KpiSimulationRequest", JsonSerializer.SerializeToElement(gates)), item => item.Field.EndsWith("/journey/gateChoices"));
    }
}

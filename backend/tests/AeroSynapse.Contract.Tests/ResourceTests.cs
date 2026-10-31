using System.Text.Json;
using System.Text.Json.Nodes;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Storage;
using AeroSynapse.Application.Management;
using AeroSynapse.Domain.DigitalTwin;
using Xunit;

namespace AeroSynapse.Contract.Tests;

public sealed class ResourceTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "contracts", "resources", "v1", "examples");
    private static JsonElement Example(string name) => JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(Root, name + ".json")));
    private static ResourceSolveRequest Request => ContractJson.Read<ResourceSolveRequest>(Example("resource-request"), "ResourceSolveRequest");

    [Fact]
    public void Python_solution_roundtrips_and_obeys_independently_checked_constraints()
    {
        var request = Request;
        var result = ContractJson.Read<ResourceSolveResult>(Example("resource-response"), "ResourceSolveResult");
        Assert.True(ResourceValidation.Valid(request, result));
        Assert.Equal(1, result.Score); Assert.True(result.AdvisoryOnly);
        Assert.NotEqual(request.Flights[0].CurrentGateId, result.Assignments[0].GateId);
    }

    [Fact]
    public void Nullable_configuration_can_be_enabled_disabled_and_validated_at_nested_fields()
    {
        var enabled = new ResourceConfigWriteRequest(1, Request.Resources);
        var read = ContractJson.Read<ResourceConfigWriteRequest>(JsonSerializer.SerializeToElement(enabled, ContractJson.Options), "ResourceConfigWriteRequest");
        Assert.Equal(1, read.Resources!.Requirements[0].MinimumTeamSize);
        var disabled = ContractJson.Read<ResourceConfigWriteRequest>(JsonSerializer.SerializeToElement(new ResourceConfigWriteRequest(1, null), ContractJson.Options), "ResourceConfigWriteRequest");
        Assert.Null(disabled.Resources);
        var invalid = JsonNode.Parse(JsonSerializer.Serialize(enabled, ContractJson.Options))!;
        invalid["resources"]!["requirements"]![0]!["minimumTeamSize"] = 0;
        Assert.Contains(VersionedContract.Validate("ResourceConfigWriteRequest", JsonSerializer.SerializeToElement(invalid)),
            item => item.Field == "/resources/requirements/0/minimumTeamSize" && item.Code == "out_of_range");
        invalid["resources"]!["requirements"]![0]!["minimumTeamSize"] = 1;
        invalid["resources"]!["workers"]![0]!["unknown"] = true;
        Assert.Contains(VersionedContract.Validate("ResourceConfigWriteRequest", JsonSerializer.SerializeToElement(invalid)),
            item => item.Field == "/resources/workers/0/unknown" && item.Code == "unknown_field");
    }

    [Fact]
    public void Flight_intervals_that_collapse_at_binary64_precision_are_rejected()
    {
        var text = Example("resource-request").GetRawText().Replace("\"startMinute\": 10", "\"startMinute\": 10.0000000000000000000001", StringComparison.Ordinal)
            .Replace("\"endMinute\": 40", "\"endMinute\": 10.0000000000000000000002", StringComparison.Ordinal);
        using var document = JsonDocument.Parse(text);
        Assert.Contains(VersionedContract.Validate("ResourceSolveRequest", document.RootElement), item => item.Code == "out_of_range");
    }

    [Theory]
    [InlineData("gate")]
    [InlineData("team")]
    [InlineData("score")]
    [InlineData("duplicate")]
    [InlineData("timeout")]
    [InlineData("advisory")]
    [InlineData("shift")]
    [InlineData("skills")]
    public void Forged_solver_results_are_rejected(string mutation)
    {
        var value = JsonNode.Parse(Example("resource-response").GetRawText())!;
        switch (mutation)
        {
            case "gate": value["assignments"]![0]!["gateId"] = Request.Resources.Gates[1].GateId.ToString(); break;
            case "team": value["assignments"]![0]!["teamId"] = Guid.NewGuid().ToString(); break;
            case "score": value["score"] = 0; break;
            case "duplicate": value["assignments"]![1] = value["assignments"]![0]!.DeepClone(); break;
            case "timeout": value["status"] = "timeout"; value["solverStatus"] = "UNKNOWN"; value["timedOut"] = true; break;
            case "advisory": value["advisoryOnly"] = false; break;
            case "shift": foreach (var worker in value["constraints"]!["resources"]!["workers"]!.AsArray()) worker!["shifts"] = new JsonArray(); break;
            case "skills": foreach (var worker in value["constraints"]!["resources"]!["workers"]!.AsArray()) worker!["skills"] = new JsonArray(); break;
        }
        Assert.NotEmpty(VersionedContract.Validate("ResourceSolveResult", JsonSerializer.SerializeToElement(value)));
    }

    [Fact]
    public void Independent_result_validation_rejects_double_booking_across_teams_sharing_the_same_person()
    {
        var request = Request;
        request = request with { Flights = request.Flights.Select(item => item with { StartMinute = 10, EndMinute = 40 }).ToArray(),
            Resources = request.Resources with
            {
                Gates = request.Resources.Gates.Select(item => item with { Availability = [new(0,90)] }).ToArray(),
                Teams = request.Resources.Teams.Select(item => item with { MemberIds = request.Resources.Teams[0].MemberIds }).ToArray()
            } };
        var assignments = request.Flights.Select((flight, index) => new ResourceAssignment(flight.FlightId,
            request.Resources.Gates[index].GateId, request.Resources.Teams[index].TeamId)).ToArray();
        Assert.All(assignments, item => Assert.True(ResourceValidation.Eligible(request, item)));
        var result = new ResourceSolveResult("optimal", "OPTIMAL", false, 1, 1, assignments, [], request, true, .01);
        Assert.False(ResourceValidation.Valid(request, result));
    }

    [Fact]
    public void A_gap_in_availability_is_unavailable_but_adjacent_windows_cover_the_interval()
    {
        Assert.True(ResourceValidation.Covers([new(0,10), new(10,40)], 5, 40));
        Assert.False(ResourceValidation.Covers([new(0,10), new(10.0001,40)], 5, 40));
    }

    private static (ScenarioWriteRequest Scenario, SceneDetails Layout) Scenario()
    {
        var request = Request;
        var start = new DateTime(2026,10,17,8,0,0,DateTimeKind.Utc); var terminal = Guid.NewGuid();
        var layout = new SceneDetails(new(Guid.NewGuid(), "DEMO", "Demo", "", true), [], [],
            request.Resources.Gates.Select(item => new Gate { Id = item.GateId, TerminalId = terminal, Code = item.GateId.ToString(), Name = "Gate", Capacity = 100 }).ToArray(),
            request.Resources.Workers.Select(item => new StaffMember { Id = item.StaffMemberId, TerminalId = terminal, DisplayName = "Worker" }).ToArray(), []);
        var inputs = new ScenarioInputs { StartsAtUtc = start, DurationMinutes = request.DurationMinutes, IntegratedResources = request.Resources,
            Flights = request.Flights.Select((item,index) => new ScenarioFlight(item.FlightId, $"DEMO {index}", item.CurrentGateId, start.AddMinutes(item.EndMinute), 100,
                start.AddMinutes(item.StartMinute), start.AddMinutes(item.EndMinute-1))).ToArray() };
        return (new("DEMO", "Demo", "", layout.Scene.Id, Guid.NewGuid(), inputs), layout);
    }

    [Fact]
    public void Optional_model_preserves_security_scenarios_and_delayed_schedule_uses_the_existing_occupancy_window()
    {
        var (scenario, layout) = Scenario();
        ScenarioValidator.Validate(scenario, layout);
        var flights = scenario.Inputs.Flights.Select(item => item with { DelayMinutes = 5 }).ToArray();
        var details = new ScenarioDetails(new(Guid.NewGuid(), "DEMO", "Demo", "", scenario.SceneId, scenario.LayoutVersionId, 1, DateTime.UtcNow, DateTime.UtcNow, true),
            scenario.Inputs with { Flights = flights });
        var request = IntegratedResourceScenarios.Build(details, layout, 5);
        Assert.Equal(15, request.Flights[0].StartMinute); Assert.Equal(45, request.Flights[0].EndMinute);
        var legacy = scenario with { Inputs = scenario.Inputs with { IntegratedResources = null } };
        ScenarioValidator.Validate(legacy, layout);
        Assert.DoesNotContain("integratedResources", JsonSerializer.Serialize(legacy.Inputs, ContractJson.Options));
    }

    [Theory]
    [InlineData("security")]
    [InlineData("terminal")]
    [InlineData("unknown-gate")]
    [InlineData("unknown-worker")]
    [InlineData("missing-flight")]
    [InlineData("flight-change")]
    [InlineData("outside-horizon")]
    public void Model_activation_rejects_incompatible_scenarios_without_relaxing_existing_security_rules(string mutation)
    {
        var (scenario, layout) = Scenario(); var inputs = scenario.Inputs; var config = inputs.IntegratedResources!;
        switch (mutation)
        {
            case "security":
                var cp = new Checkpoint { Id = Guid.NewGuid(), TerminalId = layout.Staff[0].TerminalId, CapacityPerMinute = 10 };
                layout = layout with { Checkpoints = [cp] };
                inputs = inputs with { StaffAllocations = [new(layout.Staff[0].Id, cp.Id)] }; break;
            case "terminal": layout.Staff[0].TerminalId = Guid.NewGuid(); break;
            case "unknown-gate": config = config with { Gates = config.Gates.Select(item => item with { GateId = Guid.NewGuid() }).ToArray() }; break;
            case "unknown-worker": config = config with { Workers = config.Workers.Select(item => item with { StaffMemberId = Guid.NewGuid() }).ToArray() }; break;
            case "missing-flight": config = config with { Requirements = [config.Requirements[0]] }; break;
            case "outside-horizon": inputs = inputs with { Flights = inputs.Flights.Select((item,index) => index == 0
                ? item with { BoardingOpensAtUtc = inputs.StartsAtUtc.AddMinutes(-1) } : item).ToArray() }; break;
            case "flight-change": inputs = inputs with { Events = [new(Guid.NewGuid(), "flight_change", inputs.StartsAtUtc.AddMinutes(1), null, null, inputs.Flights[0].Id, "{\"delayMinutes\":5}")] }; break;
        }
        Assert.Throws<ScenarioValidationException>(() => IntegratedResourceScenarios.Validate(inputs with { IntegratedResources = config }, layout));
        IntegratedResourceScenarios.Validate(inputs with { IntegratedResources = null }, layout);
        if (mutation == "outside-horizon") ScenarioValidator.Validate(scenario with { Inputs = inputs with { IntegratedResources = null } }, layout);
    }
}

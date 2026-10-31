using System.Text.Json;
using System.Text.Json.Nodes;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using Xunit;

namespace AeroSynapse.Contract.Tests;

// Scheduled events: the Python engine's output must correlate here, field for field.
public sealed class EventTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "contracts", "events", "v1");
    private static readonly Guid ControlA = Guid.Parse("60000000-0000-0000-0000-000000000101");
    private static JsonElement Example(string name) => JsonSerializer.SerializeToElement(JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "examples", name + ".json")))!);
    private static SimulationRequest Request() => ContractJson.Read<SimulationRequest>(Example("event-request"), "SimulationRequest");
    private static SimulationResponse Response() => ContractJson.Read<SimulationResponse>(Example("event-response"), "SimulationResponse");

    public static IEnumerable<object[]> Cases() => JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "validation-cases.json")))!.AsArray()
        .Select(item => new object[] { item!["name"]!.GetValue<string>(), item.ToJsonString() });

    [Theory]
    [MemberData(nameof(Cases))]
    public void Shared_event_case_matches_Python(string name, string caseJson)
    {
        var test = JsonNode.Parse(caseJson)!;
        Assert.Equal(name, test["name"]!.GetValue<string>());
        var payload = JsonNode.Parse(Example(test["example"]!.GetValue<string>()).GetRawText())!;
        foreach (var mutation in test["mutations"]!.AsArray())
            payload[mutation!["path"]!.GetValue<string>().TrimStart('/')] = mutation["value"]?.DeepClone();
        var errors = VersionedContract.Validate(test["definition"]!.GetValue<string>(), JsonSerializer.SerializeToElement(payload));
        Assert.Equal(test["errors"]!.AsArray().Select(item => (item!["field"]!.GetValue<string>(), item["code"]!.GetValue<string>())),
            errors.Select(item => (item.Field, item.Code)));
    }

    [Fact]
    public void Python_event_run_roundtrips_and_correlates_with_its_request()
    {
        var request = Request(); var response = Response();
        Assert.True(JsonElement.DeepEquals(Example("event-request"), JsonSerializer.SerializeToElement(request, ContractJson.Options)));
        Assert.True(JsonElement.DeepEquals(Example("event-response"), JsonSerializer.SerializeToElement(response, ContractJson.Options)));
        Assert.Empty(VersionedContract.Validate("KpiSimulationResponse", Example("event-response")));
        Assert.Empty(ContractCorrelation.Validate(request, response));
        var people = response.ProcessResult!.Passengers;
        Assert.Contains(people, person => person.AssignedCheckpointId is not null);
        Assert.Contains(people, person => person.RouteHoldMinutes is not null);
        Assert.Contains(people, person => person.Id.StartsWith("demo-surge-", StringComparison.Ordinal));
    }

    [Fact]
    public void Applied_events_must_follow_the_deterministic_order()
    {
        var response = Response();
        var applied = response.ProcessResult!.Events!.ToArray();
        (applied[1], applied[2]) = (applied[2] with { Sequence = 2 }, applied[1] with { Sequence = 3 });
        Assert.False(ProcessCorrelation.Valid(Request(), response with { ProcessResult = response.ProcessResult with { Events = applied } }));
    }

    [Fact]
    public void A_redirect_must_come_from_an_unavailable_control()
    {
        var response = Response();
        var people = response.ProcessResult!.Passengers.Select(person => person.AssignedCheckpointId is not null ? person with { AssignedCheckpointId = null } : person).ToArray();
        Assert.False(ProcessCorrelation.Valid(Request(), response with { ProcessResult = response.ProcessResult with { Passengers = people } }));
    }

    [Fact]
    public void Grants_respect_the_staffing_in_effect()
    {
        // Keep control A closed for longer in both the request and the event log: its later grants become illegal.
        var request = Request();
        var resume = request.Events!.Single(item => item.Type == "resume" && item.CheckpointId == ControlA);
        request = request with { Events = request.Events!.Select(item => item == resume ? item with { Minute = 89 } : item).ToArray() };
        var response = Response();
        var applied = EventContractValidation.Ordered(request.Events!).Select((pair, index) => new AppliedEvent(pair.Event.EventId, pair.Event.Type, pair.Event.Minute, index + 1)).ToArray();
        Assert.False(ProcessCorrelation.Valid(request, response with { ProcessResult = response.ProcessResult! with { Events = applied } }));
    }

    [Fact]
    public void Kpi_rows_report_the_effective_capacity_beside_the_nominal_one()
    {
        var response = Response();
        var rows = response.KpiResult!.Snapshots;
        var closed = rows.First(row => row.ZoneId == ControlA && row.Minute == 15);
        Assert.Equal((1, 0), (closed.Capacity, closed.EffectiveCapacity!.Value));
        var changed = rows.Select(row => row == closed ? row with { EffectiveCapacity = 1 } : row).ToArray();
        Assert.False(KpiCorrelation.Valid(Request(), response with { KpiResult = response.KpiResult with { Snapshots = changed } }));
    }

    [Fact]
    public void Scenario_events_map_to_virtual_minutes_and_reject_contradictions_with_field_errors()
    {
        var start = new DateTime(2026, 10, 15, 6, 0, 0, DateTimeKind.Utc);
        var layout = new SceneDetails(new SceneSummary(Guid.NewGuid(), "DEMO", "Demo", "", true), [],
            [new Checkpoint { Id = ControlA, Name = "A", CapacityPerMinute = 1 }], [], [], []);
        var inputs = new ScenarioInputs
        {
            StartsAtUtc = start, DurationMinutes = 60, StaffAllocations = [new StaffAllocation(Guid.NewGuid(), ControlA)],
            Events =
            [
                new ScenarioInputEvent(Guid.NewGuid(), "operator-note", start.AddSeconds(30), null, null, null, "{\"note\":\"free text\"}"),
                new ScenarioInputEvent(Guid.NewGuid(), "checkpoint_unavailable", start.AddMinutes(10), ControlA, null, null),
                new ScenarioInputEvent(Guid.NewGuid(), "resume", start.AddMinutes(25), ControlA, null, null),
            ]
        };
        ScheduledEvents.Validate(inputs, layout);
        var wire = ScheduledEvents.Wire(inputs)!;
        Assert.Equal([("checkpoint_unavailable", 10), ("resume", 25)], wire.Select(item => (item.Type, item.Minute)));
        Assert.Null(ScheduledEvents.Wire(inputs with { Events = [inputs.Events[0]] }));

        var orphan = inputs with { Events = [inputs.Events[2]] };
        var conflict = Assert.Throws<ScenarioValidationException>(() => ScheduledEvents.Validate(orphan, layout));
        Assert.Equal("The event contradicts the scheduled timeline.", Assert.Single(conflict.Errors["inputs.events[0].checkpointId"]));

        var malformed = inputs with { Events = [new ScenarioInputEvent(Guid.NewGuid(), "staff_reduction", start.AddMinutes(5).AddSeconds(30), ControlA, null, null, "{\"lanes\":2}")] };
        var errors = Assert.Throws<ScenarioValidationException>(() => ScheduledEvents.Validate(malformed, layout)).Errors;
        Assert.Contains("inputs.events[0].occurredAtUtc", errors.Keys);
        Assert.Contains("inputs.events[0].payloadJson", errors.Keys);
    }

    [Theory]
    [InlineData("staff_reduction", "{\"staffCount\":-1}")]
    [InlineData("staff_reduction", "{\"staffCount\":10001}")]
    [InlineData("passenger_surge", "{\"passengerCount\":0,\"windowMinutes\":1}")]
    [InlineData("passenger_surge", "{\"passengerCount\":10001,\"windowMinutes\":1}")]
    [InlineData("passenger_surge", "{\"passengerCount\":1,\"windowMinutes\":0}")]
    [InlineData("passenger_surge", "{\"passengerCount\":1,\"windowMinutes\":2147483647}")]
    [InlineData("flight_change", "{\"delayMinutes\":1441}")]
    [InlineData("staff_reduction", "{\"staffCount\":0,\"staffCount\":1}")]
    public void Scenario_parameters_obey_the_wire_ranges_before_they_can_be_saved(string type, string payload)
    {
        var start = new DateTime(2026, 10, 15, 6, 0, 0, DateTimeKind.Utc);
        var inputs = new ScenarioInputs { StartsAtUtc = start,
            Events = [new ScenarioInputEvent(Guid.NewGuid(), type, start.AddMinutes(1), ControlA, null, null, payload)] };
        var error = Assert.Throws<ScenarioValidationException>(() => ScheduledEvents.Wire(inputs));
        Assert.Contains("inputs.events[0].payloadJson", error.Errors.Keys);
    }
}

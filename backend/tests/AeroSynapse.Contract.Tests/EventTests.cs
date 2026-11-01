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

    [Theory][InlineData("closure")][InlineData("closure-staggered")]
    public void Closing_controls_send_their_whole_queue_on_to_the_open_controls(string name)
    {
        // Three controls: A closes at 5 with 37 waiting, B at 20; "closure" also reopens A at 10 (test_queue_redistribution.py).
        var request = ContractJson.Read<SimulationRequest>(Example(name + "-request"), "SimulationRequest");
        var response = ContractJson.Read<SimulationResponse>(Example(name + "-response"), "SimulationResponse");
        Assert.Empty(VersionedContract.Validate("KpiSimulationResponse", Example(name + "-response")));
        Assert.Empty(ContractCorrelation.Validate(request, response));
        var people = response.ProcessResult!.Passengers;
        Assert.Equal(37, people.Count(person => person.Transfers is [{ Minute: 5 } first, ..] && first.FromCheckpointId == ControlA));
        Assert.Contains(people, person => person.Transfers is { Count: 2 });
    }

    [Fact]
    public void Queue_moves_leave_an_unavailable_control_and_chain_from_where_the_passenger_arrived()
    {
        var request = ContractJson.Read<SimulationRequest>(Example("closure-request"), "SimulationRequest");
        var response = ContractJson.Read<SimulationResponse>(Example("closure-response"), "SimulationResponse");
        SimulationResponse Forge(Func<ProcessPassenger, ProcessPassenger> change) => response with { ProcessResult = response.ProcessResult! with {
            Passengers = response.ProcessResult.Passengers.Select(person => person.Transfers is { Count: 2 } ? change(person) : person).ToArray() } };
        // A reopened at 10: leaving its queue at 12 would need a queue_redistribution decision.
        Assert.False(ProcessCorrelation.Valid(request, Forge(person => person with { Transfers = [person.Transfers![0] with { Minute = 12 }, person.Transfers[1]] })));
        // Dropping the first move would mean a redirect to B on arrival, but A was open then.
        Assert.False(ProcessCorrelation.Valid(request, Forge(person => person with { Transfers = [person.Transfers![1]] })));
    }

    [Fact]
    public void With_routes_an_arrival_may_be_rerouted_beforehand_to_another_lane_of_its_flight()
    {
        var request = ContractJson.Read<SimulationRequest>(Example("closure-request"), "SimulationRequest");
        var response = ContractJson.Read<SimulationResponse>(Example("closure-response"), "SimulationResponse");
        var timeline = new EventTimeline(request);
        // A generated passenger arriving at 12.5, between A reopening and B closing, sent there from a lane that never closed.
        var person = response.ProcessResult!.Passengers.First(p => p.FlightId is not null && p.AssignedCheckpointId is null
            && p.Transfers is null && timeline.Surge(p.Id) is null) with { ArrivalMinute = 12.5 };
        var other = request.Checkpoints.Select(c => c.CheckpointId).First(id => id != person.CheckpointId && timeline.Available(id, request.DurationMinutes));
        var rerouted = person with { AssignedCheckpointId = other };
        var flight = request.PassengerGeneration!.Flights.Single(f => f.FlightId == person.FlightId);
        var spread = request with { PassengerGeneration = request.PassengerGeneration with { Flights = [flight with { CheckpointRoutes =
            [new(person.CheckpointId, flight.RouteId, flight.RouteTravelMinutes), new(other, flight.RouteId, flight.RouteTravelMinutes)] }] } };
        // Decided at the start of the five-minute window the arrival falls in; from then it counts at its new lane.
        var decided = ProcessCorrelation.ReroutedAt(spread, new EventTimeline(spread), rerouted);
        Assert.Equal(Math.Floor(person.ArrivalMinute / 5) * 5, decided);
        Assert.Equal(other, rerouted.CheckpointAt(decided!.Value - 0.5, decided));
        Assert.Equal(person.CheckpointId, rerouted.CheckpointAt(decided.Value, decided));
        // A fixed flight has one lane, and without routes nothing is rerouted: such a move stays a forged redirect.
        Assert.Null(ProcessCorrelation.ReroutedAt(request, timeline, rerouted));
        Assert.Null(ProcessCorrelation.ReroutedAt(spread with { Navigation = null }, new EventTimeline(spread), rerouted));
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

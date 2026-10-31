using System.Text.Json;
using System.Text.Json.Nodes;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using Xunit;

namespace AeroSynapse.Contract.Tests;

public sealed class PassengerTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "contracts", "passengers", "v1");

    [Theory]
    [InlineData("generation-request", "PassengerGenerationRequest")]
    [InlineData("generation-overload", "PassengerGenerationRequest")]
    [InlineData("generation-flows", "PassengerGenerationRequest")]
    [InlineData("generation-spread", "PassengerGenerationRequest")]
    [InlineData("profiled-simulation-request", "ProfiledSimulationRequest")]
    public void Passenger_DTOs_roundtrip_shared_examples(string example, string definition)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "examples", example + ".json")));
        var payload = document.RootElement;
        var wire = definition == "PassengerGenerationRequest"
            ? JsonSerializer.SerializeToElement(ContractJson.Read<PassengerGenerationRequest>(payload, definition), ContractJson.Options)
            : JsonSerializer.SerializeToElement(ContractJson.Read<SimulationRequest>(payload, definition), ContractJson.Options);
        Assert.True(JsonElement.DeepEquals(payload, wire));
    }

    private static readonly DateTime Start = new(2026, 10, 14, 10, 0, 0, DateTimeKind.Utc);
    private static readonly Guid SceneId = Guid.NewGuid(), CheckpointId = Guid.NewGuid(), GateId = Guid.NewGuid(), RouteId = Guid.NewGuid();
    private static SceneDetails Layout => new(new(SceneId, "demo", "Demo", "", true), [],
        [new Checkpoint { Id = CheckpointId, CapacityPerMinute = 4 }],
        [new Gate { Id = GateId }], [],
        [new PassengerRoute { Id = RouteId, Code = "R-1", FromCheckpointId = CheckpointId, ToGateId = GateId, TravelSeconds = 120 }]);
    private static ScenarioInputs Inputs => new() { StartsAtUtc = Start, DurationMinutes = 90, Seed = 42,
        Flights = [new(Guid.NewGuid(), "DEMO-1", GateId, Start.AddMinutes(60), 120, Start.AddMinutes(20), Start.AddMinutes(55))] };

    [Fact]
    public void Defaults_and_subsets_synchronize_exact_populations_and_flows_and_snapshot_captures_profiles()
    {
        var inputs = Inputs;
        inputs = PassengerScenarios.Synchronize(inputs with { PassengerGeneration = PassengerScenarios.Defaults(inputs, Layout) }, Layout);
        Assert.Equal(120, Assert.Single(inputs.Populations).Count);
        Assert.Equal(120, Assert.Single(inputs.Flows).PassengerCount);
        Assert.Equal(RouteId, inputs.Flows[0].RouteId);
        var full = inputs;
        var config = inputs.PassengerGeneration!;
        inputs = PassengerScenarios.Synchronize(inputs with { PassengerGeneration = config with {
            Batches = [config.Batches[0] with { CountMode = "subset", ModeledCount = 23 }] } }, Layout);
        PassengerScenarios.Validate(inputs, Layout);
        Assert.Equal(23, inputs.Populations[0].Count);
        Assert.Equal(full.Populations[0].Id, inputs.Populations[0].Id);
        Assert.Equal(full.Flows[0].Id, inputs.Flows[0].Id);
        var id = Guid.NewGuid(); var version = Guid.NewGuid();
        var captured = new RunInputSnapshot(1, Start,
            new(new(id, "demo", "Demo", "", SceneId, version, 7, Start, Start, true), inputs),
            new(new(version, SceneId, 1, Start), Layout));
        var request = CapturedSimulation.Request(Guid.NewGuid(), captured);
        Assert.Equal(23, request.PassengerCount);
        Assert.Equal(23, request.PassengerGeneration!.Batches[0].ModeledCount);
        var changed = PassengerScenarios.Synchronize(inputs with { Seed = 43 }, Layout);
        Assert.Equal(42, CapturedSimulation.Request(request.RunId, captured).Seed);
        Assert.Equal(43, changed.Seed);
    }

    [Fact]
    public void Spread_defaults_offer_every_checkpoint_routed_to_the_gate_while_fixed_and_compiled_defaults_stay_single()
    {
        var other = Guid.NewGuid(); var otherRoute = Guid.NewGuid(); var elsewhere = Guid.NewGuid();
        var layout = Layout with
        {
            Checkpoints = [.. Layout.Checkpoints, new Checkpoint { Id = other, CapacityPerMinute = 6 }],
            Gates = [.. Layout.Gates, new Gate { Id = elsewhere }],
            Routes = [.. Layout.Routes,
                new PassengerRoute { Id = otherRoute, Code = "R-2", FromCheckpointId = other, ToGateId = GateId, TravelSeconds = 300 },
                new PassengerRoute { Id = Guid.NewGuid(), Code = "R-3", FromCheckpointId = other, ToGateId = elsewhere, TravelSeconds = 60 }]
        };
        var inputs = Inputs;
        Assert.Null(Assert.Single(PassengerScenarios.Defaults(inputs, layout).Batches).CheckpointMode);
        Assert.Null(Assert.Single(PassengerScenarios.Request(inputs with { PassengerGeneration = PassengerScenarios.Defaults(inputs, layout) }, layout).Flights).CheckpointRoutes);

        inputs = PassengerScenarios.Synchronize(inputs with { PassengerGeneration = PassengerScenarios.SpreadDefaults(inputs, layout) }, layout);
        PassengerScenarios.Validate(inputs, layout);
        var batch = Assert.Single(inputs.PassengerGeneration!.Batches);
        Assert.Equal(PassengerBatchConfig.Spread, batch.CheckpointMode);
        // The flight keeps its own checkpoint and route; flows stay on it and incidents still target it.
        Assert.Equal(RouteId, Assert.Single(inputs.Flows).RouteId);
        var flight = Assert.Single(PassengerScenarios.Request(inputs, layout).Flights);
        Assert.Equal((CheckpointId, RouteId), (flight.CheckpointId, flight.RouteId));
        Assert.Equal(new[] { (CheckpointId, RouteId, 2d), (other, otherRoute, 5d) }.OrderBy(item => item.Item1),
            flight.CheckpointRoutes!.Select(route => (route.CheckpointId, route.RouteId, route.RouteTravelMinutes)));
        // Response correlation accepts passengers on any offered lane, and only there.
        Assert.True(flight.Allows(other, otherRoute) && flight.Allows(CheckpointId, RouteId) && flight.Allows(other));
        Assert.False(flight.Allows(other, RouteId) || flight.Allows(Guid.NewGuid()));
        Assert.False((flight with { CheckpointRoutes = null }).Allows(other));
        var wire = JsonSerializer.SerializeToElement(PassengerScenarios.Request(inputs, layout), ContractJson.Options);
        Assert.Empty(VersionedContract.Validate("PassengerGenerationRequest", wire));
        Assert.Equal("spread", wire.GetProperty("batches")[0].GetProperty("checkpointMode").GetString());

        var added = inputs.Flights[0] with { Id = Guid.NewGuid(), Code = "DEMO-2" };
        var reconciled = PassengerScenarios.ReconcileFlights(inputs, inputs with { Flights = [.. inputs.Flights, added] }, layout);
        Assert.All(reconciled.PassengerGeneration!.Batches, item => Assert.Equal(PassengerBatchConfig.Spread, item.CheckpointMode));
    }

    [Fact]
    public void Flight_edits_reconcile_full_counts_cancellations_and_last_flight_removal()
    {
        var inputs = Inputs;
        inputs = PassengerScenarios.Synchronize(inputs with { PassengerGeneration = PassengerScenarios.Defaults(inputs, Layout) }, Layout);
        var resized = PassengerScenarios.ReconcileFlights(inputs, inputs with { Flights = [inputs.Flights[0] with { PassengerCapacity = 75 }] }, Layout);
        Assert.Equal(75, resized.Populations[0].Count);
        Assert.Equal(75, resized.PassengerGeneration!.Batches[0].ModeledCount);
        var cancelled = PassengerScenarios.ReconcileFlights(resized, resized with { Flights = [resized.Flights[0] with { Status = "cancelled" }] }, Layout);
        Assert.Empty(cancelled.Populations); Assert.Empty(cancelled.Flows);
        Assert.Equal("subset", cancelled.PassengerGeneration!.Batches[0].CountMode);
        Assert.Equal(0, cancelled.PassengerGeneration.Batches[0].ModeledCount);
        var empty = PassengerScenarios.ReconcileFlights(inputs, inputs with { Flights = [] }, Layout);
        Assert.Null(empty.PassengerGeneration); Assert.Empty(empty.Populations); Assert.Empty(empty.Flows);
    }

    [Fact]
    public void Stale_flight_profiles_and_unsynchronized_counts_fail_with_explicit_fields()
    {
        var inputs = Inputs;
        var config = PassengerScenarios.Defaults(inputs, Layout);
        var invalid = inputs with { PassengerGeneration = config with { Batches = [config.Batches[0] with { ModeledCount = 121 }] } };
        var error = Assert.Throws<ScenarioValidationException>(() => PassengerScenarios.Request(invalid, Layout));
        Assert.Contains("inputs.passengerGeneration.batches.0.modeledCount", error.Errors.Keys);
        Assert.Throws<ScenarioValidationException>(() => PassengerScenarios.Validate(inputs with { PassengerGeneration = config }, Layout));
    }

    private static readonly PassengerDistribution Minutes = new("uniform", 1, 2, 1.5, .2, 1.5);
    private static PassengerFlowConfig Flow => new(
        [new("standard", .7, false, "security", 1, 1), new("reducedMobility", .3, true, "assistedSecurity", 1.5, 2)],
        [new("bagDrop", 2, Minutes), new("assistedSecurity", 1, Minutes)], [new("bagDrop", 10, 20)]);

    [Fact]
    public void Alternative_flows_persist_in_the_profile_and_reach_generation_and_captured_runs()
    {
        var inputs = Inputs;
        var config = PassengerScenarios.Defaults(inputs, Layout);
        Assert.Null(config.Flow);
        Assert.False(JsonSerializer.SerializeToElement(config, ContractJson.Options).TryGetProperty("flow", out _));
        inputs = PassengerScenarios.Synchronize(inputs with { PassengerGeneration = config with { Flow = Flow } }, Layout);
        PassengerScenarios.Validate(inputs, Layout);
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var stored = JsonSerializer.Deserialize<ScenarioInputs>(JsonSerializer.Serialize(inputs, web), web)!.PassengerGeneration!.Flow!;
        Assert.Equal(Flow.Segments, stored.Segments); Assert.Equal(Flow.Stages, stored.Stages); Assert.Equal(Flow.Closures, stored.Closures);
        Assert.Equal(Flow.Segments, PassengerScenarios.Request(inputs, Layout).Flow!.Segments);
        var version = Guid.NewGuid();
        var captured = new RunInputSnapshot(1, Start,
            new(new(Guid.NewGuid(), "demo", "Demo", "", SceneId, version, 3, Start, Start, true), inputs),
            new(new(version, SceneId, 1, Start), Layout));
        var wire = JsonSerializer.SerializeToElement(CapturedSimulation.Request(Guid.NewGuid(), captured), ContractJson.Options);
        var flow = wire.GetProperty("passengerGeneration").GetProperty("flow");
        Assert.Equal("bagDrop", flow.GetProperty("closures")[0].GetProperty("stage").GetString());
        Assert.False(flow.GetProperty("segments")[0].TryGetProperty("transferMinutes", out _));
    }

    [Fact]
    public void Invalid_routing_rules_fail_with_explicit_fields()
    {
        var inputs = Inputs;
        var config = PassengerScenarios.Defaults(inputs, Layout) with { Flow = Flow with { Stages = [Flow.Stages[1]] } };
        var error = Assert.Throws<ScenarioValidationException>(() => PassengerScenarios.Request(inputs with { PassengerGeneration = config }, Layout));
        Assert.Contains("inputs.passengerGeneration.flow.segments.1.holdBaggage", error.Errors.Keys);
        Assert.Contains("inputs.passengerGeneration.flow.closures.0.stage", error.Errors.Keys);
    }
}

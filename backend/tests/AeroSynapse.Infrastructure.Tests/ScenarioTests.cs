using System.Text.Json;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using AeroSynapse.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace AeroSynapse.Infrastructure.Tests;

public sealed class ScenarioTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly DateTime Start = new(2026, 10, 11, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Scenario_round_trips_all_inputs_and_duplicate_is_independent()
    {
        await using var test = await TestDatabase.CreateAsync();
        var service = new ScenarioService(test.Context);
        var layout = await service.PublishLayoutAsync(SyntheticScenarioSeeder.SceneId, Token);
        var request = Request(layout);
        var original = await service.CreateAsync(request, Token);
        var duplicate = await service.DuplicateAsync(original.Scenario.Id, new("copy", "Independent copy"), Token);
        Assert.NotEqual(original.Scenario.Id, duplicate.Scenario.Id);
        Assert.Equal(JsonSerializer.Serialize(original.Inputs, Json), JsonSerializer.Serialize(duplicate.Inputs, Json));
        await using var fresh = test.NewContext();
        var reopened = new ScenarioService(fresh);
        var read = Assert.IsType<ScenarioDetails>(await reopened.GetAsync(original.Scenario.Id, Token));
        Assert.Single(read.Inputs.Flights);
        Assert.Single(read.Inputs.Populations);
        Assert.Single(read.Inputs.Flows);
        Assert.Single(read.Inputs.Events);
        Assert.Equal(6, read.Inputs.StaffAllocations.Count);
        var edited = await reopened.UpdateAsync(duplicate.Scenario.Id, request with
        {
            Code = "copy", Name = "Edited copy", Revision = 1,
            Inputs = request.Inputs with { Seed = 99, Populations = [request.Inputs.Populations[0] with { Count = 150 }] }
        }, Token);
        Assert.Equal(2, edited.Scenario.Revision);
        var unchanged = Assert.IsType<ScenarioDetails>(await reopened.GetAsync(original.Scenario.Id, Token));
        Assert.Equal(1, unchanged.Scenario.Revision);
        Assert.Equal(120, unchanged.Inputs.Populations[0].Count);
        Assert.Equal(42, unchanged.Inputs.Seed);
        Assert.Equal(2, (await reopened.ListAsync(SyntheticScenarioSeeder.SceneId, Token)).Count);
        Assert.Empty(await reopened.ListAsync(Guid.NewGuid(), Token));
    }

    [Theory]
    [InlineData("unknown-gate", "inputs.flights[0].gateId")]
    [InlineData("unknown-route", "inputs.flows[0].routeId")]
    [InlineData("wrong-route-gate", "inputs.flows[0].routeId")]
    [InlineData("unknown-flight", "inputs.flows[0].flightId")]
    [InlineData("unknown-population", "inputs.flows[0].populationId")]
    [InlineData("population-overflow", "inputs.flows")]
    [InlineData("aggregate-overflow", "inputs.flows")]
    [InlineData("flight-overflow", "inputs.flows")]
    [InlineData("staff-overflow", "inputs.staffAllocations[6].staffMemberId")]
    [InlineData("unknown-staff", "inputs.staffAllocations[0].staffMemberId")]
    [InlineData("unknown-checkpoint", "inputs.staffAllocations[0].checkpointId")]
    [InlineData("invalid-event-target", "inputs.events[0].checkpointId")]
    [InlineData("invalid-event-flight", "inputs.events[0].flightId")]
    [InlineData("conflicting-event-gate", "inputs.events[0].gateId")]
    [InlineData("invalid-json", "inputs.events[0].payloadJson")]
    [InlineData("array-json", "inputs.events[0].payloadJson")]
    [InlineData("negative-population", "inputs.populations[0].count")]
    [InlineData("negative-flow", "inputs.flows[0].passengerCount")]
    [InlineData("negative-seed", "inputs.seed")]
    [InlineData("zero-duration", "inputs.durationMinutes")]
    [InlineData("missing-name", "name")]
    [InlineData("nul-code", "code")]
    [InlineData("nul-name", "name")]
    [InlineData("nul-description", "description")]
    [InlineData("nul-flight-code", "inputs.flights[0].code")]
    [InlineData("nul-population-name", "inputs.populations[0].name")]
    [InlineData("nul-event-type", "inputs.events[0].type")]
    [InlineData("non-utc", "inputs.startsAtUtc")]
    [InlineData("invalid-window", "inputs.flows[0].endsAtUtc")]
    [InlineData("after-departure", "inputs.flows[0].endsAtUtc")]
    [InlineData("duplicate-flight", "inputs.flights[1].id")]
    [InlineData("null-inputs", "inputs")]
    [InlineData("null-array", "inputs.flights")]
    [InlineData("null-entry", "inputs.flights[0]")]
    [InlineData("missing-revision", "revision")]
    public async Task Invalid_edits_return_field_errors_and_leave_original_unchanged(string invalid, string field)
    {
        await using var test = await TestDatabase.CreateAsync();
        var service = new ScenarioService(test.Context);
        var layout = await service.PublishLayoutAsync(SyntheticScenarioSeeder.SceneId, Token);
        var request = Request(layout) with { Revision = 1 };
        var original = await service.CreateAsync(request, Token);
        original = Assert.IsType<ScenarioDetails>(await service.GetAsync(original.Scenario.Id, Token));
        var inputs = request.Inputs;
        var flight = inputs.Flights[0];
        var population = inputs.Populations[0];
        var flow = inputs.Flows[0];
        var item = inputs.Events[0];
        var allocation = inputs.StaffAllocations[0];
        request = invalid switch
        {
            "unknown-gate" => request with { Inputs = inputs with { Flights = [flight with { GateId = Guid.NewGuid() }] } },
            "unknown-route" => request with { Inputs = inputs with { Flows = [flow with { RouteId = Guid.NewGuid() }] } },
            "wrong-route-gate" => request with { Inputs = inputs with { Flows = [flow with { RouteId = SyntheticScenarioSeeder.Id(401) }] } },
            "unknown-flight" => request with { Inputs = inputs with { Flows = [flow with { FlightId = Guid.NewGuid() }] } },
            "unknown-population" => request with { Inputs = inputs with { Flows = [flow with { PopulationId = Guid.NewGuid() }] } },
            "population-overflow" => request with { Inputs = inputs with { Populations = [population with { Count = 10 }] } },
            "aggregate-overflow" => request with { Inputs = inputs with { Flows = [flow with { PassengerCount = 80 }, flow with { Id = Guid.NewGuid(), PassengerCount = 80 }] } },
            "flight-overflow" => request with { Inputs = inputs with { Flights = [flight with { PassengerCapacity = 10 }] } },
            "staff-overflow" => request with { Inputs = inputs with { StaffAllocations = [.. inputs.StaffAllocations, allocation] } },
            "unknown-staff" => request with { Inputs = inputs with { StaffAllocations = [allocation with { StaffMemberId = Guid.NewGuid() }] } },
            "unknown-checkpoint" => request with { Inputs = inputs with { StaffAllocations = [allocation with { CheckpointId = Guid.NewGuid() }] } },
            "invalid-event-target" => request with { Inputs = inputs with { Events = [item with { CheckpointId = Guid.NewGuid() }] } },
            "invalid-event-flight" => request with { Inputs = inputs with { Events = [item with { FlightId = Guid.NewGuid() }] } },
            "conflicting-event-gate" => request with { Inputs = inputs with { Events = [item with { GateId = SyntheticScenarioSeeder.Id(202) }] } },
            "invalid-json" => request with { Inputs = inputs with { Events = [item with { PayloadJson = "{" }] } },
            "array-json" => request with { Inputs = inputs with { Events = [item with { PayloadJson = "[]" }] } },
            "negative-population" => request with { Inputs = inputs with { Populations = [population with { Count = -1 }] } },
            "negative-flow" => request with { Inputs = inputs with { Flows = [flow with { PassengerCount = -1 }] } },
            "negative-seed" => request with { Inputs = inputs with { Seed = -1 } },
            "zero-duration" => request with { Inputs = inputs with { DurationMinutes = 0 } },
            "missing-name" => request with { Name = " " },
            "nul-code" => request with { Code = "base\0line" },
            "nul-name" => request with { Name = "Base\0line" },
            "nul-description" => request with { Description = "Description\0text" },
            "nul-flight-code" => request with { Inputs = inputs with { Flights = [flight with { Code = "AS\0-201" }] } },
            "nul-population-name" => request with { Inputs = inputs with { Populations = [population with { Name = "Pop\0ulation" }] } },
            "nul-event-type" => request with { Inputs = inputs with { Events = [item with { Type = "checkpoint\0-opened" }] } },
            "non-utc" => request with { Inputs = inputs with { StartsAtUtc = DateTime.SpecifyKind(Start, DateTimeKind.Unspecified) } },
            "invalid-window" => request with { Inputs = inputs with { Flows = [flow with { EndsAtUtc = Start.AddMinutes(-1) }] } },
            "after-departure" => request with { Inputs = inputs with { Flows = [flow with { EndsAtUtc = Start.AddMinutes(59) }] } },
            "duplicate-flight" => request with { Inputs = inputs with { Flights = [flight, flight] } },
            "null-inputs" => request with { Inputs = null! },
            "null-array" => request with { Inputs = inputs with { Flights = null! } },
            "null-entry" => request with { Inputs = inputs with { Flights = [null!] } },
            "missing-revision" => request with { Revision = null },
            _ => throw new ArgumentOutOfRangeException(nameof(invalid))
        };
        var error = await Assert.ThrowsAsync<ScenarioValidationException>(() => service.UpdateAsync(original.Scenario.Id, request, Token));
        Assert.Contains(field, error.Errors.Keys);
        await using var fresh = test.NewContext();
        var unchanged = Assert.IsType<ScenarioDetails>(await new ScenarioService(fresh).GetAsync(original.Scenario.Id, Token));
        Assert.Equal(JsonSerializer.Serialize(original, Json), JsonSerializer.Serialize(unchanged, Json));
    }

    [Fact]
    public async Task Layout_and_staff_availability_are_versioned_and_cross_scene_references_are_rejected()
    {
        await using var test = await TestDatabase.CreateAsync();
        var database = test.Context;
        var service = new ScenarioService(database);
        var v1 = await service.PublishLayoutAsync(SyntheticScenarioSeeder.SceneId, Token);
        var request = Request(v1);
        await service.CreateAsync(request, Token);
        database.Staff.Remove(await database.Staff.SingleAsync(x => x.Id == SyntheticScenarioSeeder.Id(301), Token));
        (await database.Checkpoints.SingleAsync(x => x.Id == SyntheticScenarioSeeder.Id(101), Token)).CapacityPerMinute = 999;
        await database.SaveChangesAsync(Token);
        var v2 = await service.PublishLayoutAsync(SyntheticScenarioSeeder.SceneId, Token);
        Assert.Equal(2, v2.Version.Version);
        Assert.Equal(SyntheticScenarioSeeder.StaffSlots - 1, v2.Layout.Staff.Count);
        var savedV1 = Assert.IsType<PublishedLayout>(await service.GetLayoutAsync(v1.Version.SceneId, v1.Version.Id, Token));
        Assert.Equal(SyntheticScenarioSeeder.StaffSlots, savedV1.Layout.Staff.Count);
        Assert.Equal(SyntheticScenarioSeeder.SyntheticCapacityPerMinute(1), savedV1.Layout.Checkpoints[0].CapacityPerMinute);
        var unavailable = await Assert.ThrowsAsync<ScenarioValidationException>(() => service.CreateAsync(request with
        {
            Code = "v2", LayoutVersionId = v2.Version.Id
        }, Token));
        Assert.Contains("inputs.staffAllocations[0].staffMemberId", unavailable.Errors.Keys);
        var wrongScene = await Assert.ThrowsAsync<ScenarioValidationException>(() => service.CreateAsync(request with { SceneId = Guid.NewGuid() }, Token));
        Assert.Contains("sceneId", wrongScene.Errors.Keys);
        var missing = await Assert.ThrowsAsync<ScenarioValidationException>(() => service.CreateAsync(request with { LayoutVersionId = Guid.NewGuid() }, Token));
        Assert.Contains("layoutVersionId", missing.Errors.Keys);
        await Assert.ThrowsAsync<ScenarioNotFoundException>(() => service.PublishLayoutAsync(Guid.NewGuid(), Token));
        Assert.Null(await service.GetLayoutAsync(Guid.NewGuid(), v1.Version.Id, Token));
    }

    [Fact]
    public async Task Run_snapshot_survives_scenario_and_live_layout_changes_and_can_be_retrieved_in_isolation()
    {
        await using var test = await TestDatabase.CreateAsync();
        var database = test.Context;
        var service = new ScenarioService(database);
        var layout = await service.PublishLayoutAsync(SyntheticScenarioSeeder.SceneId, Token);
        var request = Request(layout);
        var scenario = await service.CreateAsync(request, Token);
        var run = await service.CreateRunAsync(scenario.Scenario.Id, Token);
        Assert.Equal("planned", run.Status);
        await service.UpdateAsync(scenario.Scenario.Id, request with { Revision = 1, Name = "Changed", Inputs = request.Inputs with { Seed = 99 } }, Token);
        (await database.Gates.SingleAsync(x => x.Id == SyntheticScenarioSeeder.Id(201), Token)).Name = "Changed gate";
        await database.SaveChangesAsync(Token);
        await using var fresh = test.NewContext();
        var reopened = new ScenarioService(fresh);
        var stored = Assert.IsType<SimulationRun>(await reopened.GetRunAsync(scenario.Scenario.Id, run.Id, Token));
        var snapshot = Assert.IsType<RunInputSnapshot>(JsonSerializer.Deserialize<RunInputSnapshot>(stored.InputSnapshotJson!, Json));
        Assert.Equal(1, snapshot.SchemaVersion);
        Assert.Equal(1, snapshot.Scenario.Scenario.Revision);
        Assert.Equal(request.Name, snapshot.Scenario.Scenario.Name);
        Assert.Equal(42, snapshot.Scenario.Inputs.Seed);
        Assert.Equal(layout.Layout.Gates[0].Name, snapshot.Layout.Layout.Gates[0].Name);
        Assert.Single(snapshot.Scenario.Inputs.Flights);
        Assert.Single(snapshot.Scenario.Inputs.Populations);
        Assert.Single(snapshot.Scenario.Inputs.Flows);
        Assert.Single(snapshot.Scenario.Inputs.Events);
        Assert.Equal(6, snapshot.Scenario.Inputs.StaffAllocations.Count);
        Assert.Null(await reopened.GetRunAsync(Guid.NewGuid(), run.Id, Token));
        Assert.Single(await reopened.RunsAsync(scenario.Scenario.Id, 1, Token));
        Assert.Empty(await reopened.RunsAsync(Guid.NewGuid(), 1, Token));
        await Assert.ThrowsAsync<ScenarioValidationException>(() => reopened.RunsAsync(scenario.Scenario.Id, 0, Token));
        var second = await reopened.CreateRunAsync(scenario.Scenario.Id, Token);
        Assert.Equal(2, second.ScenarioRevision);
        Assert.Equal(99, second.Seed);
    }

    [Theory]
    [InlineData("layout")]
    [InlineData("snapshot")]
    [InlineData("revision")]
    [InlineData("seed")]
    [InlineData("parameters")]
    public async Task PostgreSQL_protects_published_layouts_and_captured_run_inputs(string target)
    {
        await using var test = await TestDatabase.CreateAsync();
        var database = test.Context;
        var service = new ScenarioService(database);
        var layout = await service.PublishLayoutAsync(SyntheticScenarioSeeder.SceneId, Token);
        var scenario = await service.CreateAsync(Request(layout), Token);
        var run = await service.CreateRunAsync(scenario.Scenario.Id, Token);
        if (target == "layout")
            (await database.LayoutVersions.SingleAsync(Token)).LayoutJson = "{}";
        else
        {
            if (target == "snapshot") run.InputSnapshotJson = "{}";
            if (target == "revision") run.ScenarioRevision = 2;
            if (target == "seed") run.Seed = 7;
            if (target == "parameters") run.ParametersJson = "{}";
        }
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync(Token));
        Assert.Equal(PostgresErrorCodes.CheckViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        database.ChangeTracker.Clear();
        var preserved = await database.Runs.SingleAsync(x => x.Id == run.Id, Token);
        var runs = new RunStorageService(database);
        var launched = await runs.TransitionAsync(preserved.Id, new("running", preserved.Revision), Token);
        await runs.TransitionAsync(preserved.Id, new("completed", launched.Revision), Token);
        Assert.Equal(42, preserved.Seed);
    }

    [Fact]
    public async Task Concurrent_edits_and_duplicate_codes_are_explicit_conflicts()
    {
        await using var test = await TestDatabase.CreateAsync();
        var service = new ScenarioService(test.Context);
        var layout = await service.PublishLayoutAsync(SyntheticScenarioSeeder.SceneId, Token);
        var request = Request(layout);
        var scenario = await service.CreateAsync(request, Token);
        var results = await Task.WhenAll(Enumerable.Range(1, 2).Select(async index =>
        {
            await using var context = test.NewContext();
            try
            {
                await new ScenarioService(context).UpdateAsync(scenario.Scenario.Id, request with { Revision = 1, Name = $"Edit {index}" }, Token);
                return true;
            }
            catch (ScenarioConflictException) { return false; }
        }));
        Assert.Single(results, x => x);
        Assert.Single(results, x => !x);
        Assert.Equal(2, (await service.GetAsync(scenario.Scenario.Id, Token))!.Scenario.Revision);
        await Assert.ThrowsAsync<ScenarioConflictException>(() => service.DuplicateAsync(scenario.Scenario.Id, new(request.Code, "Duplicate"), Token));
        await using var fresh = test.NewContext();
        Assert.Equal(1, await fresh.Scenarios.CountAsync(Token));
    }

    [Fact]
    public async Task Upgrade_preserves_legacy_runs_and_reset_removes_new_demo_dependencies_only()
    {
        await using var test = await TestDatabase.CreateAsync();
        var database = test.Context;
        // Roll back to the initial migration and upgrade a populated database again.
        await database.GetService<IMigrator>().MigrateAsync("20261011091612_InitialDigitalTwin", Token);
        database.ChangeTracker.Clear();
        await database.Database.MigrateAsync(Token);
        var legacy = await database.Runs.SingleAsync(Token);
        Assert.Null(legacy.ScenarioId);
        Assert.Null(legacy.InputSnapshotJson);
        Assert.Equal(42, legacy.Seed);
        var service = new ScenarioService(database);
        var demo = await service.CreateAsync(Request(await service.PublishLayoutAsync(SyntheticScenarioSeeder.SceneId, Token)), Token);
        await service.CreateRunAsync(demo.Scenario.Id, Token);
        var otherScene = new Scene { Id = Guid.NewGuid(), Code = "other", Name = "Preserved" };
        database.Scenes.Add(otherScene);
        await database.SaveChangesAsync(Token);
        var otherLayout = await service.PublishLayoutAsync(otherScene.Id, Token);
        var other = await service.CreateAsync(new("preserved", "Preserved", "", otherScene.Id, otherLayout.Version.Id,
            new ScenarioInputs { StartsAtUtc = Start, DurationMinutes = 60 }), Token);
        var otherRun = await service.CreateRunAsync(other.Scenario.Id, Token);
        await SyntheticScenarioSeeder.ResetAsync(database, Token);
        Assert.Null(await service.GetAsync(demo.Scenario.Id, Token));
        Assert.NotNull(await service.GetAsync(other.Scenario.Id, Token));
        Assert.NotNull(await service.GetRunAsync(other.Scenario.Id, otherRun.Id, Token));
        Assert.Equal(1, await database.Scenarios.CountAsync(Token));
        Assert.Equal(1, await database.LayoutVersions.CountAsync(Token));
    }

    private static ScenarioWriteRequest Request(PublishedLayout layout)
    {
        var flightId = Guid.NewGuid();
        var populationId = Guid.NewGuid();
        return new("baseline", "Baseline operations", "Synthetic scenario for integration tests", layout.Version.SceneId, layout.Version.Id,
            new ScenarioInputs
            {
                StartsAtUtc = Start, DurationMinutes = 60,
                Flights = [new(flightId, "AS-201", SyntheticScenarioSeeder.Id(201), Start.AddMinutes(55), 130)],
                Populations = [new(populationId, "Departing passengers", 120)],
                Flows = [new(Guid.NewGuid(), populationId, flightId, SyntheticScenarioSeeder.Id(400), 120, Start, Start.AddMinutes(45))],
                StaffAllocations = layout.Layout.Staff.Where(x => x.CheckpointId is not null).Select(x => new StaffAllocation(x.Id, x.CheckpointId!.Value)).ToArray(),
                Events = [new(Guid.NewGuid(), "checkpoint-opened", Start.AddMinutes(1), SyntheticScenarioSeeder.Id(101), null, flightId, "{\"lanes\":2}")]
            });
    }
}

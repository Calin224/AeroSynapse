using System.Text.Json;
using AeroSynapse.Domain.DigitalTwin;
using AeroSynapse.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace AeroSynapse.Infrastructure.Tests;

public sealed class StorageTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migration_creates_clean_database_and_round_trips_down_up()
    {
        await using var test = await TestDatabase.CreateAsync();
        var database = test.Context;
        Assert.Equal(database.Database.GetMigrations().Count(), (await database.Database.GetAppliedMigrationsAsync(Token)).Count());
        Assert.Empty(await database.Database.GetPendingMigrationsAsync(Token));
        Assert.False(database.Database.HasPendingModelChanges());
        Assert.Equal(3, await database.Checkpoints.CountAsync(Token));
        await database.GetService<IMigrator>().MigrateAsync("0", Token);
        Assert.Empty(await database.Database.GetAppliedMigrationsAsync(Token));
        Assert.Null(await Scalar(database, "SELECT to_regclass('twin.scenes')::text"));
        database.ChangeTracker.Clear();
        await database.Database.MigrateAsync(Token);
        Assert.Equal(5, await database.Gates.CountAsync(Token));
    }

    [Fact]
    public async Task Seed_repairs_missing_objects_without_overwriting_edits_and_is_concurrent_safe()
    {
        await using var test = await TestDatabase.CreateAsync();
        var database = test.Context;
        var scene = await database.Scenes.SingleAsync(Token);
        scene.Name = "Edited by integration test";
        var missingStaffId = SyntheticScenarioSeeder.Id(301);
        database.Staff.Remove(await database.Staff.SingleAsync(x => x.Id == missingStaffId, Token));
        await database.SaveChangesAsync(Token);
        await Task.WhenAll(Enumerable.Range(1, 4).Select(async _ =>
        {
            await using var context = test.NewContext();
            await SyntheticScenarioSeeder.SeedAsync(context, Token);
        }));
        Assert.Equal("Edited by integration test", await database.Scenes.Select(x => x.Name).SingleAsync(Token));
        Assert.Equal(SyntheticScenarioSeeder.StaffSlots, await database.Staff.CountAsync(Token));
        Assert.True(await database.Staff.AnyAsync(x => x.Id == missingStaffId, Token));
        Assert.Equal(15, await database.Routes.CountAsync(Token));
        Assert.Equal(3, await database.Events.CountAsync(Token));
        Assert.Equal(1, await database.Runs.CountAsync(Token));
    }

    [Fact]
    public async Task Demo_has_valid_relations_and_every_object_is_synthetic()
    {
        await using var test = await TestDatabase.CreateAsync();
        var database = test.Context;
        var details = await new SceneQueryService(database).GetAsync(SyntheticScenarioSeeder.SceneId, Token);
        Assert.NotNull(details);
        Assert.True(details.Scene.IsSynthetic);
        Assert.Single(details.Terminals);
        Assert.Equal(3, details.Checkpoints.Count);
        Assert.Equal(5, details.Gates.Count);
        Assert.Equal(SyntheticScenarioSeeder.StaffSlots, details.Staff.Count);
        Assert.Equal(15, details.Routes.Count);
        Assert.All(details.Checkpoints, checkpoint => Assert.True(checkpoint.IsSynthetic));
        Assert.All(details.Gates, gate => Assert.True(gate.IsSynthetic));
        Assert.All(details.Terminals, terminal => Assert.True(terminal.IsSynthetic));
        Assert.All(details.Staff, staff =>
        {
            Assert.True(staff.IsSynthetic);
            // Spare security slots have no control until a scenario places them.
            if (staff.CheckpointId is not null) Assert.Contains(details.Checkpoints, checkpoint => checkpoint.Id == staff.CheckpointId);
        });
        Assert.All(details.Routes, route =>
        {
            Assert.True(route.IsSynthetic);
            Assert.Contains(details.Checkpoints, checkpoint => checkpoint.Id == route.FromCheckpointId);
            Assert.Contains(details.Gates, gate => gate.Id == route.ToGateId);
        });
        Assert.All(await database.Events.ToArrayAsync(Token), item => Assert.True(item.IsSynthetic));
        Assert.All(await database.Runs.ToArrayAsync(Token), item => Assert.True(item.IsSynthetic));
    }

    [Theory]
    [InlineData("duplicate-code", "23505")]
    [InlineData("negative-capacity", "23514")]
    [InlineData("cross-scene-terminal", "23503")]
    [InlineData("cross-terminal-route", "23503")]
    [InlineData("invalid-run-status", "23514")]
    [InlineData("cross-scene-event", "23503")]
    public async Task PostgreSQL_rejects_invalid_graphs_and_values(string scenario, string sqlState)
    {
        await using var test = await TestDatabase.CreateAsync();
        var database = test.Context;
        var otherScene = new Scene { Id = Guid.NewGuid(), Code = "other", Name = "Other scene" };
        var otherTerminal = new Terminal { Id = Guid.NewGuid(), SceneId = otherScene.Id, Code = "T2", Name = "Other terminal" };
        database.Scenes.Add(otherScene);
        database.Terminals.Add(otherTerminal);
        await database.SaveChangesAsync(Token);
        if (scenario is "duplicate-code" or "negative-capacity" or "cross-scene-terminal")
            database.Checkpoints.Add(new Checkpoint
            {
                Id = Guid.NewGuid(), SceneId = SyntheticScenarioSeeder.SceneId,
                TerminalId = scenario == "cross-scene-terminal" ? otherTerminal.Id : SyntheticScenarioSeeder.TerminalId,
                Code = scenario == "duplicate-code" ? "CP-1" : "TEST", Name = "Invalid test checkpoint",
                CapacityPerMinute = scenario == "negative-capacity" ? -1 : 1
            });
        if (scenario == "cross-terminal-route")
        {
            var otherGate = new Gate
            {
                Id = Guid.NewGuid(), SceneId = otherScene.Id, TerminalId = otherTerminal.Id,
                Code = "OTHER", Name = "Other gate", Capacity = 1
            };
            database.Gates.Add(otherGate);
            database.Routes.Add(new PassengerRoute
            {
                Id = Guid.NewGuid(), SceneId = otherScene.Id, TerminalId = otherTerminal.Id,
                FromCheckpointId = SyntheticScenarioSeeder.Id(101), ToGateId = otherGate.Id,
                Code = "TEST", DistanceMeters = 1, TravelSeconds = 1
            });
        }
        if (scenario == "invalid-run-status")
            database.Runs.Add(new SimulationRun
            {
                Id = Guid.NewGuid(), SceneId = otherScene.Id, StartedAtUtc = DateTime.UtcNow, Status = "unknown"
            });
        if (scenario == "cross-scene-event")
            database.Events.Add(new SceneEvent
            {
                Id = Guid.NewGuid(), SceneId = otherScene.Id, SimulationRunId = SyntheticScenarioSeeder.Id(500),
                OccurredAtUtc = DateTime.UtcNow, Type = "invalid"
            });
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync(Token));
        Assert.Equal(sqlState, Assert.IsType<PostgresException>(exception.InnerException).SqlState);
    }

    [Fact]
    public async Task Queries_and_repository_support_ordering_limits_and_scene_isolation()
    {
        await using var test = await TestDatabase.CreateAsync();
        var database = test.Context;
        var queries = new SceneQueryService(database);
        Assert.Single(await queries.ListAsync(Token));
        Assert.Null(await queries.GetAsync(Guid.NewGuid(), Token));
        var events = await queries.EventsAsync(SyntheticScenarioSeeder.SceneId, 2, Token);
        Assert.Equal(2, events.Count);
        Assert.True(events[0].OccurredAtUtc > events[1].OccurredAtUtc);
        Assert.True(JsonDocument.Parse(events[0].PayloadJson).RootElement.GetProperty("isSynthetic").GetBoolean());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => queries.EventsAsync(SyntheticScenarioSeeder.SceneId, 101, Token));
        var repository = new SimulationRunRepository(database);
        var run = new SimulationRun
        {
            Id = Guid.NewGuid(), SceneId = SyntheticScenarioSeeder.SceneId, StartedAtUtc = DateTime.UtcNow,
            Status = "running", Seed = 27, IsSynthetic = true
        };
        run.LaunchedAtUtc = run.StartedAtUtc;
        await repository.AddAsync(run, Token);
        Assert.NotNull(await repository.FindAsync(run.SceneId, run.Id, Token));
        Assert.Null(await repository.FindAsync(Guid.NewGuid(), run.Id, Token));
        Assert.Equal(run.Id, (await queries.RunsAsync(run.SceneId, 1, Token)).Single().Id);
        await database.Database.MigrateAsync(Token);
        Assert.NotNull(await repository.FindAsync(run.SceneId, run.Id, Token));
    }

    [Fact]
    public async Task Reset_restores_only_owned_demo_scene_and_preserves_unrelated_data()
    {
        await using var test = await TestDatabase.CreateAsync();
        var database = test.Context;
        var otherSceneId = Guid.NewGuid();
        database.Scenes.Add(new Scene { Id = otherSceneId, Code = "other", Name = "Preserve me", IsSynthetic = false });
        (await database.Checkpoints.SingleAsync(x => x.Code == "CP-1", Token)).CapacityPerMinute = 999;
        await database.SaveChangesAsync(Token);
        await SyntheticScenarioSeeder.ResetAsync(database, Token);
        await SyntheticScenarioSeeder.ResetAsync(database, Token);
        Assert.Equal(2, await database.Scenes.CountAsync(Token));
        Assert.Equal("Preserve me", await database.Scenes.Where(x => x.Id == otherSceneId).Select(x => x.Name).SingleAsync(Token));
        Assert.Equal(SyntheticScenarioSeeder.SyntheticCapacityPerMinute(1), await database.Checkpoints.Where(x => x.Code == "CP-1").Select(x => x.CapacityPerMinute).SingleAsync(Token));
        Assert.Equal(15, await database.Routes.CountAsync(Token));
    }

    [Fact]
    public async Task Seed_and_reset_refuse_to_modify_a_non_synthetic_reserved_scene()
    {
        await using var test = await TestDatabase.CreateAsync();
        var database = test.Context;
        (await database.Scenes.SingleAsync(Token)).IsSynthetic = false;
        await database.SaveChangesAsync(Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => SyntheticScenarioSeeder.SeedAsync(database, Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => SyntheticScenarioSeeder.ResetAsync(database, Token));
        Assert.Equal(5, await database.Gates.CountAsync(Token));
        Assert.Equal(3, await database.Checkpoints.CountAsync(Token));
    }

    [Fact]
    public async Task Migration_installs_query_indexes_in_PostgreSQL()
    {
        await using var test = await TestDatabase.CreateAsync();
        var indexes = Assert.IsType<string>(await Scalar(test.Context,
            "SELECT string_agg(indexname, ',') FROM pg_indexes WHERE schemaname = 'twin'"));
        Assert.Contains("ix_scenes_code", indexes);
        Assert.Contains("ix_events_scene_occurred", indexes);
        Assert.Contains("ix_events_run_occurred", indexes);
        Assert.Contains("ix_runs_scene_started", indexes);
        Assert.Contains("ix_runs_status_started", indexes);
    }

    private static async Task<object?> Scalar(TwinDbContext database, string sql)
    {
        var connection = database.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(Token);
        return value is DBNull ? null : value;
    }
}

using System.Reflection;
using AeroSynapse.Domain.DigitalTwin;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AeroSynapse.Infrastructure.Storage;

public static class SyntheticScenarioSeeder
{
    public static readonly Guid SceneId = Id(1);
    public static readonly Guid TerminalId = Id(2);
    // The terminal model's CHECK-IN zone carries this ID.
    public static readonly Guid CheckInAreaId = Id(901);
    /// <summary>Security staff slots in the demo terminal: three controls with two people each, plus spares.</summary>
    public const int StaffSlots = 18;
    private static readonly string CheckInMigration = typeof(Migrations.CheckInDesks).GetCustomAttribute<MigrationAttribute>()!.Id;
    private const string LockSql = "SELECT pg_advisory_xact_lock(62006002)";

    // IDs are stable across fresh databases, restarts and explicit reset.
    public static Guid Id(int number) => Guid.Parse($"60000000-0000-0000-0000-{number:D12}");

    // One operator runs one screening lane: 120-180 passengers an hour, so 2-3 a minute.
    // CP-3 stands for an older, slower lane.
    public static int SyntheticCapacityPerMinute(int checkpointNumber) => checkpointNumber == 3 ? 2 : 3;

    public static void Seed(TwinDbContext database) => SeedAsync(database).GetAwaiter().GetResult();

    public static async Task SeedAsync(TwinDbContext database, CancellationToken cancellationToken = default)
    {
        await using var transaction = database.Database.CurrentTransaction is null
            ? await database.Database.BeginTransactionAsync(cancellationToken) : null;
        await database.Database.ExecuteSqlRawAsync(LockSql, cancellationToken);
        if (await database.Scenes.AnyAsync(x => x.Id == SceneId && !x.IsSynthetic, cancellationToken))
            throw new InvalidOperationException("The reserved demo scene is not marked synthetic; seed refused.");
        await AddMissing(database, new[] { new Scene
        {
            Id = SceneId, Code = "synthetic-terminal-v1", Name = "Terminal Aurora · synthetic",
            Description = "Scenariu fictiv pentru integrare. Geometria, capacitățile, personalul și timpii sunt synthetic; nu reprezintă un aeroport real.",
            IsSynthetic = true
        } }, cancellationToken);
        await AddMissing(database, new[] { new Terminal
        {
            Id = TerminalId, SceneId = SceneId, Code = "T-DEMO", Name = "Terminal fictiv Aurora", IsSynthetic = true
        } }, cancellationToken);
        var checkpoints = Enumerable.Range(1, 3).Select(number => new Checkpoint
        {
            Id = Id(100 + number), SceneId = SceneId, TerminalId = TerminalId,
            Code = $"CP-{number}", Name = $"Checkpoint synthetic {number}", CapacityPerMinute = SyntheticCapacityPerMinute(number), IsSynthetic = true
        }).ToArray();
        var gates = Enumerable.Range(1, 5).Select(number => new Gate
        {
            Id = Id(200 + number), SceneId = SceneId, TerminalId = TerminalId,
            Code = $"G-{number}", Name = $"Poartă synthetic {number}", Capacity = 120 + number * 10, IsSynthetic = true
        }).ToArray();
        await AddMissing(database, checkpoints, cancellationToken);
        await AddMissing(database, gates, cancellationToken);
        // Seeding also follows a rollback below the check-in migration, which leaves no table for the desks.
        if ((await database.Database.GetAppliedMigrationsAsync(cancellationToken)).Contains(CheckInMigration))
            await AddMissing(database, new[] { new CheckInArea
            {
                Id = CheckInAreaId, SceneId = SceneId, TerminalId = TerminalId, Code = "CHECK-IN", Name = "Check-in · ghișee synthetic",
                DeskCount = 12, ServiceSeconds = 120, WalkingSeconds = 90, IsSynthetic = true
            } }, cancellationToken);
        // Six people start at the controls; the other slots are spare security staff the user can add to a scenario.
        await AddMissing(database, Enumerable.Range(1, StaffSlots).Select(number => new StaffMember
        {
            Id = Id(300 + number), SceneId = SceneId, TerminalId = TerminalId,
            CheckpointId = number <= 6 ? checkpoints[(number - 1) / 2].Id : null, Code = $"STAFF-{number}",
            DisplayName = $"Operator fictiv {number}", Role = "checkpoint-operator", IsSynthetic = true
        }).ToArray(), cancellationToken);
        await AddMissing(database, checkpoints.SelectMany((checkpoint, cp) => gates.Select((gate, g) => new PassengerRoute
        {
            Id = Id(400 + cp * 5 + g), SceneId = SceneId, TerminalId = TerminalId,
            FromCheckpointId = checkpoint.Id, ToGateId = gate.Id, Code = $"{checkpoint.Code}-{gate.Code}",
            DistanceMeters = 80 + cp * 20 + g * 15, TravelSeconds = 90 + cp * 20 + g * 15, IsSynthetic = true
        })).ToArray(), cancellationToken);
        var start = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        await AddMissing(database, new[] { new SimulationRun
        {
            Id = Id(500), SceneId = SceneId, StartedAtUtc = start, LaunchedAtUtc = start, FinishedAtUtc = start.AddHours(1),
            Status = "completed", Seed = 42, ParametersJson = "{\"duration_minutes\":60,\"passenger_count\":120,\"seed\":42}", IsSynthetic = true
        } }, cancellationToken);
        await AddMissing(database, Enumerable.Range(1, 3).Select(number => new SceneEvent
        {
            Id = Id(600 + number), SceneId = SceneId, SimulationRunId = Id(500),
            OccurredAtUtc = start.AddMinutes(number), Type = "synthetic-checkpoint-opened",
            PayloadJson = $"{{\"checkpointId\":\"{checkpoints[number - 1].Id}\",\"isSynthetic\":true}}", IsSynthetic = true
        }).ToArray(), cancellationToken);
        await database.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
    }

    private static async Task AddMissing<T>(TwinDbContext database, T[] candidates, CancellationToken cancellationToken)
        where T : TwinEntity
    {
        var ids = candidates.Select(x => x.Id).ToArray();
        var existing = await database.Set<T>().AsNoTracking().Where(x => ids.Contains(x.Id))
            .Select(x => x.Id).ToArrayAsync(cancellationToken);
        var existingIds = existing.ToHashSet();
        // A rollback can drop a table whose rows this context still tracks; those rows are re-added, not reused.
        foreach (var stale in database.ChangeTracker.Entries<T>().Where(x => ids.Contains(x.Entity.Id) && !existingIds.Contains(x.Entity.Id)).ToArray())
            stale.State = EntityState.Detached;
        database.Set<T>().AddRange(candidates.Where(x => !existingIds.Contains(x.Id)));
    }

    public static async Task ResetAsync(TwinDbContext database, CancellationToken cancellationToken = default)
    {
        // Only this dedicated synthetic scene is owned by the reset operation.
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        await database.Database.ExecuteSqlRawAsync(LockSql, cancellationToken);
        var scene = await database.Scenes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == SceneId, cancellationToken);
        if (scene is { IsSynthetic: false }) throw new InvalidOperationException("The demo scene is not marked synthetic; reset refused.");
        if (await database.BatchVariants.AnyAsync(v => database.Scenarios.Any(s => s.Id == v.ScenarioId && s.SceneId == SceneId), cancellationToken))
            throw new InvalidOperationException("Retained Monte Carlo batches refer to this demo scene; reset refused. Use an isolated demo database for a fresh seed.");
        if (await database.NotificationCommands.AnyAsync(c => database.Scenarios.Any(s => s.Id == c.ScenarioId && s.SceneId == SceneId), cancellationToken))
            throw new InvalidOperationException("Operational notification audit retains this demo scene; reset refused. Use an isolated demo database.");
        await database.RecommendationCandidates.Where(x => x.SceneId == SceneId).ExecuteDeleteAsync(cancellationToken);
        await database.Recommendations.Where(x => x.SceneId == SceneId).ExecuteDeleteAsync(cancellationToken);
        await database.Metrics.Where(x => x.SceneId == SceneId).ExecuteDeleteAsync(cancellationToken);
        await database.Snapshots.Where(x => x.SceneId == SceneId).ExecuteDeleteAsync(cancellationToken);
        await database.Events.Where(x => x.SceneId == SceneId).ExecuteDeleteAsync(cancellationToken);
        await database.AnomalyReviews.Where(x => x.SceneId == SceneId).ExecuteDeleteAsync(cancellationToken);
        await database.Runs.Where(x => x.SceneId == SceneId).ExecuteDeleteAsync(cancellationToken);
        await database.Scenarios.Where(x => x.SceneId == SceneId).ExecuteDeleteAsync(cancellationToken);
        await database.LayoutVersions.Where(x => x.SceneId == SceneId).ExecuteDeleteAsync(cancellationToken);
        await database.Routes.Where(x => x.SceneId == SceneId).ExecuteDeleteAsync(cancellationToken);
        await database.Staff.Where(x => x.SceneId == SceneId).ExecuteDeleteAsync(cancellationToken);
        await database.Gates.Where(x => x.SceneId == SceneId).ExecuteDeleteAsync(cancellationToken);
        if ((await database.Database.GetAppliedMigrationsAsync(cancellationToken)).Contains(CheckInMigration))
            await database.CheckInAreas.Where(x => x.SceneId == SceneId).ExecuteDeleteAsync(cancellationToken);
        await database.Checkpoints.Where(x => x.SceneId == SceneId).ExecuteDeleteAsync(cancellationToken);
        await database.Terminals.Where(x => x.SceneId == SceneId).ExecuteDeleteAsync(cancellationToken);
        await database.Scenes.Where(x => x.Id == SceneId).ExecuteDeleteAsync(cancellationToken);
        database.ChangeTracker.Clear();
        await SeedAsync(database, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}

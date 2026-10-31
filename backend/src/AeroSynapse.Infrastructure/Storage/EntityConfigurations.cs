using AeroSynapse.Domain.DigitalTwin;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AeroSynapse.Infrastructure.Storage;

internal static class Mapping
{
    internal static void Base<T>(EntityTypeBuilder<T> entity, string table) where T : TwinEntity
    {
        entity.HasBaseType((Type?)null);
        entity.ToTable(table);
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).ValueGeneratedNever();
        entity.Property(x => x.IsSynthetic).IsRequired();
    }
    internal static void Code<T>(EntityTypeBuilder<T> entity) where T : TwinEntity =>
        entity.Property<string>("Code").HasMaxLength(64).IsRequired();
}

internal sealed class SceneConfiguration : IEntityTypeConfiguration<Scene>
{
    public void Configure(EntityTypeBuilder<Scene> entity)
    {
        Mapping.Base(entity, "scenes");
        Mapping.Code(entity);
        entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
        entity.Property(x => x.Description).HasMaxLength(2000).IsRequired();
        entity.HasIndex(x => x.Code).IsUnique().HasDatabaseName("ix_scenes_code");
        entity.HasIndex(x => new { x.IsSynthetic, x.Id }).HasDatabaseName("ix_scenes_synthetic_id");
    }
}

internal sealed class TerminalConfiguration : IEntityTypeConfiguration<Terminal>
{
    public void Configure(EntityTypeBuilder<Terminal> entity)
    {
        Mapping.Base(entity, "terminals");
        Mapping.Code(entity);
        entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
        entity.HasAlternateKey(x => new { x.SceneId, x.Id });
        entity.HasIndex(x => new { x.SceneId, x.Code }).IsUnique();
        entity.HasOne<Scene>().WithMany().HasForeignKey(x => x.SceneId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CheckpointConfiguration : IEntityTypeConfiguration<Checkpoint>
{
    public void Configure(EntityTypeBuilder<Checkpoint> entity)
    {
        Mapping.Base(entity, "checkpoints");
        Mapping.Code(entity);
        entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
        entity.ToTable(t => t.HasCheckConstraint("ck_checkpoint_capacity", "\"CapacityPerMinute\" > 0"));
        entity.HasAlternateKey(x => new { x.SceneId, x.TerminalId, x.Id });
        entity.HasIndex(x => new { x.TerminalId, x.Code }).IsUnique();
        entity.HasOne<Terminal>().WithMany().HasForeignKey(x => new { x.SceneId, x.TerminalId })
            .HasPrincipalKey(x => new { x.SceneId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class GateConfiguration : IEntityTypeConfiguration<Gate>
{
    public void Configure(EntityTypeBuilder<Gate> entity)
    {
        Mapping.Base(entity, "gates");
        Mapping.Code(entity);
        entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
        entity.ToTable(t => t.HasCheckConstraint("ck_gate_capacity", "\"Capacity\" > 0"));
        entity.HasAlternateKey(x => new { x.SceneId, x.TerminalId, x.Id });
        entity.HasIndex(x => new { x.TerminalId, x.Code }).IsUnique();
        entity.HasOne<Terminal>().WithMany().HasForeignKey(x => new { x.SceneId, x.TerminalId })
            .HasPrincipalKey(x => new { x.SceneId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CheckInAreaConfiguration : IEntityTypeConfiguration<CheckInArea>
{
    public void Configure(EntityTypeBuilder<CheckInArea> entity)
    {
        Mapping.Base(entity, "check_in_areas");
        Mapping.Code(entity);
        entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
        entity.ToTable(t => t.HasCheckConstraint("ck_check_in_area_service",
            "\"DeskCount\" BETWEEN 1 AND 1000 AND \"ServiceSeconds\" BETWEEN 1 AND 3600 AND \"WalkingSeconds\" BETWEEN 0 AND 14400"));
        entity.HasAlternateKey(x => new { x.SceneId, x.TerminalId, x.Id });
        entity.HasIndex(x => new { x.TerminalId, x.Code }).IsUnique();
        entity.HasOne<Terminal>().WithMany().HasForeignKey(x => new { x.SceneId, x.TerminalId })
            .HasPrincipalKey(x => new { x.SceneId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class StaffConfiguration : IEntityTypeConfiguration<StaffMember>
{
    public void Configure(EntityTypeBuilder<StaffMember> entity)
    {
        Mapping.Base(entity, "staff");
        Mapping.Code(entity);
        entity.Property(x => x.DisplayName).HasMaxLength(160).IsRequired();
        entity.Property(x => x.Role).HasMaxLength(64).IsRequired();
        entity.HasIndex(x => new { x.TerminalId, x.Code }).IsUnique();
        entity.HasOne<Terminal>().WithMany().HasForeignKey(x => new { x.SceneId, x.TerminalId })
            .HasPrincipalKey(x => new { x.SceneId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Checkpoint>().WithMany().HasForeignKey(x => new { x.SceneId, x.TerminalId, x.CheckpointId })
            .HasPrincipalKey(x => new { x.SceneId, x.TerminalId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RouteConfiguration : IEntityTypeConfiguration<PassengerRoute>
{
    public void Configure(EntityTypeBuilder<PassengerRoute> entity)
    {
        Mapping.Base(entity, "routes");
        Mapping.Code(entity);
        entity.Property(x => x.DistanceMeters).HasPrecision(10, 2);
        entity.ToTable(t =>
        {
            t.HasCheckConstraint("ck_route_distance", "\"DistanceMeters\" > 0");
            t.HasCheckConstraint("ck_route_duration", "\"TravelSeconds\" > 0");
        });
        entity.HasIndex(x => new { x.TerminalId, x.Code }).IsUnique();
        entity.HasIndex(x => new { x.FromCheckpointId, x.ToGateId }).IsUnique();
        entity.HasOne<Checkpoint>().WithMany().HasForeignKey(x => new { x.SceneId, x.TerminalId, x.FromCheckpointId })
            .HasPrincipalKey(x => new { x.SceneId, x.TerminalId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Gate>().WithMany().HasForeignKey(x => new { x.SceneId, x.TerminalId, x.ToGateId })
            .HasPrincipalKey(x => new { x.SceneId, x.TerminalId, x.Id }).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RunConfiguration : IEntityTypeConfiguration<SimulationRun>
{
    public void Configure(EntityTypeBuilder<SimulationRun> entity)
    {
        Mapping.Base(entity, "runs");
        entity.Property(x => x.InputHash).HasMaxLength(64);
        entity.Property(x => x.ResultHash).HasMaxLength(64);
        entity.Property(x => x.EngineBuild).HasMaxLength(64);
        entity.Property(x => x.EngineVersion).HasMaxLength(64);
        entity.HasIndex(x => x.InputHash).HasDatabaseName("ix_runs_input_hash");
        entity.Property(x => x.Status).HasMaxLength(16).IsRequired();
        entity.Property(x => x.ParametersJson).HasColumnType("jsonb").IsRequired();
        entity.Property(x => x.InputSnapshotJson).HasColumnType("jsonb");
        entity.Property(x => x.FailureReason).HasMaxLength(2000);
        entity.Property(x => x.Revision).HasDefaultValue(1).IsConcurrencyToken();
        entity.Property(x => x.ExecutionRequested).HasDefaultValue(false);
        entity.HasIndex(x => new { x.ExecutionRequested, x.Status, x.StartedAtUtc }).HasDatabaseName("ix_runs_dispatch");
        entity.HasIndex(x => x.RetryOfRunId).IsUnique();
        entity.HasOne<SimulationRun>().WithMany().HasForeignKey(x => new { x.SceneId, x.RetryOfRunId })
            .HasPrincipalKey(x => new { x.SceneId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        entity.Ignore(x => x.ResultsValid);
        entity.ToTable(t =>
        {
            t.HasCheckConstraint("ck_run_status", "\"Status\" IN ('planned', 'pending', 'running', 'completed', 'failed')");
            t.HasCheckConstraint("ck_run_fingerprints", "(\"InputHash\" IS NULL OR \"InputHash\" ~ '^[0-9a-f]{64}$') AND (\"ResultHash\" IS NULL OR (\"Status\" = 'completed' AND \"ResultHash\" ~ '^[0-9a-f]{64}$')) AND (\"EngineBuild\" IS NULL OR \"EngineBuild\" ~ '^[0-9a-f]{64}$')");
            t.HasCheckConstraint("ck_run_engine_time", "\"EngineMilliseconds\" IS NULL OR (\"EngineMilliseconds\" >= 0 AND \"EngineMilliseconds\" < 'Infinity'::float8)");
            t.HasCheckConstraint("ck_run_dispatch_inputs", "NOT \"ExecutionRequested\" OR \"InputSnapshotJson\" IS NOT NULL");
            t.HasCheckConstraint("ck_run_retry_dispatch", "\"RetryOfRunId\" IS NULL OR \"ExecutionRequested\"");
            t.HasCheckConstraint("ck_run_seed", "\"Seed\" >= 0");
            t.HasCheckConstraint("ck_run_time", "\"FinishedAtUtc\" IS NULL OR \"FinishedAtUtc\" >= \"StartedAtUtc\"");
            t.HasCheckConstraint("ck_run_revision", "\"Revision\" > 0");
            t.HasCheckConstraint("ck_run_lifecycle", "(" +
                "\"Status\" IN ('planned', 'pending') AND \"LaunchedAtUtc\" IS NULL AND \"FinishedAtUtc\" IS NULL AND \"FailureReason\" IS NULL) OR (" +
                "\"Status\" = 'running' AND \"LaunchedAtUtc\" IS NOT NULL AND \"FinishedAtUtc\" IS NULL AND \"FailureReason\" IS NULL) OR (" +
                "\"Status\" = 'completed' AND \"LaunchedAtUtc\" IS NOT NULL AND \"FinishedAtUtc\" IS NOT NULL AND \"FailureReason\" IS NULL) OR (" +
                "\"Status\" = 'failed' AND \"LaunchedAtUtc\" IS NOT NULL AND \"FinishedAtUtc\" IS NOT NULL AND \"FailureReason\" IS NOT NULL AND length(btrim(\"FailureReason\")) > 0)");
            t.HasCheckConstraint("ck_run_launch_time", "\"LaunchedAtUtc\" IS NULL OR (\"LaunchedAtUtc\" >= \"StartedAtUtc\" AND (\"FinishedAtUtc\" IS NULL OR \"FinishedAtUtc\" >= \"LaunchedAtUtc\"))");
            t.HasCheckConstraint("ck_run_snapshot", "(\"ScenarioId\" IS NULL AND \"ScenarioRevision\" IS NULL AND \"InputSnapshotJson\" IS NULL) OR " +
                "(\"ScenarioId\" IS NOT NULL AND \"ScenarioRevision\" IS NOT NULL AND \"ScenarioRevision\" > 0 AND \"InputSnapshotJson\" IS NOT NULL AND jsonb_typeof(\"InputSnapshotJson\") = 'object')");
        });
        entity.HasAlternateKey(x => new { x.SceneId, x.Id });
        entity.HasOne<Scene>().WithMany().HasForeignKey(x => x.SceneId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Scenario>().WithMany().HasForeignKey(x => new { x.SceneId, x.ScenarioId })
            .HasPrincipalKey(x => new { x.SceneId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(x => new { x.ScenarioId, x.StartedAtUtc, x.Id }).IsDescending(false, true, true)
            .HasDatabaseName("ix_runs_scenario_started");
        entity.HasIndex(x => new { x.SceneId, x.StartedAtUtc, x.Id }).IsDescending(false, true, true)
            .HasDatabaseName("ix_runs_scene_started");
        entity.HasIndex(x => new { x.Status, x.StartedAtUtc }).HasDatabaseName("ix_runs_status_started");
    }
}

internal sealed class LayoutVersionConfiguration : IEntityTypeConfiguration<LayoutVersion>
{
    public void Configure(EntityTypeBuilder<LayoutVersion> entity)
    {
        Mapping.Base(entity, "layout_versions");
        entity.Property(x => x.LayoutJson).HasColumnType("jsonb").IsRequired();
        entity.HasAlternateKey(x => new { x.SceneId, x.Id });
        entity.HasIndex(x => new { x.SceneId, x.Version }).IsUnique();
        entity.HasOne<Scene>().WithMany().HasForeignKey(x => x.SceneId).OnDelete(DeleteBehavior.Restrict);
        entity.ToTable(t =>
        {
            t.HasCheckConstraint("ck_layout_version", "\"Version\" > 0");
            t.HasCheckConstraint("ck_layout_document", "jsonb_typeof(\"LayoutJson\") = 'object'");
        });
    }
}

internal sealed class ScenarioConfiguration : IEntityTypeConfiguration<Scenario>
{
    public void Configure(EntityTypeBuilder<Scenario> entity)
    {
        Mapping.Base(entity, "scenarios");
        Mapping.Code(entity);
        entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
        entity.Property(x => x.Description).HasMaxLength(2000).IsRequired();
        entity.Property(x => x.ConfigurationJson).HasColumnType("jsonb").IsRequired();
        entity.Property(x => x.Revision).IsConcurrencyToken();
        entity.HasAlternateKey(x => new { x.SceneId, x.Id });
        entity.HasIndex(x => new { x.SceneId, x.Code }).IsUnique();
        entity.HasOne<LayoutVersion>().WithMany().HasForeignKey(x => new { x.SceneId, x.LayoutVersionId })
            .HasPrincipalKey(x => new { x.SceneId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        entity.ToTable(t =>
        {
            t.HasCheckConstraint("ck_scenario_revision", "\"Revision\" > 0");
            t.HasCheckConstraint("ck_scenario_document", "jsonb_typeof(\"ConfigurationJson\") = 'object'");
            t.HasCheckConstraint("ck_scenario_text", "length(btrim(\"Code\")) > 0 AND length(btrim(\"Name\")) > 0");
            t.HasCheckConstraint("ck_scenario_time", "\"UpdatedAtUtc\" >= \"CreatedAtUtc\"");
            t.HasCheckConstraint("ck_scenario_deleted", "\"DeletedAtUtc\" IS NULL OR \"DeletedAtUtc\" >= \"CreatedAtUtc\"");
        });
    }
}

internal sealed class EventConfiguration : IEntityTypeConfiguration<SceneEvent>
{
    public void Configure(EntityTypeBuilder<SceneEvent> entity)
    {
        Mapping.Base(entity, "events");
        entity.Property(x => x.Type).HasMaxLength(64).IsRequired();
        entity.Property(x => x.PayloadJson).HasColumnType("jsonb").IsRequired();
        entity.HasOne<Scene>().WithMany().HasForeignKey(x => x.SceneId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<SimulationRun>().WithMany().HasForeignKey(x => new { x.SceneId, x.SimulationRunId })
            .HasPrincipalKey(x => new { x.SceneId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(x => new { x.SceneId, x.OccurredAtUtc, x.Id }).IsDescending(false, true, true)
            .HasDatabaseName("ix_events_scene_occurred");
        entity.HasIndex(x => new { x.SimulationRunId, x.OccurredAtUtc }).HasDatabaseName("ix_events_run_occurred");
    }
}

using AeroSynapse.Domain.DigitalTwin;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AeroSynapse.Infrastructure.Storage;

internal sealed class SimulationBatchConfiguration : IEntityTypeConfiguration<SimulationBatch>
{
    public void Configure(EntityTypeBuilder<SimulationBatch> entity)
    {
        Mapping.Base(entity, "simulation_batches");
        entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
        entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
        entity.Property(x => x.RequestJson).HasColumnType("jsonb").IsRequired();
        entity.Property(x => x.SeedsJson).HasColumnType("jsonb").IsRequired();
        entity.Property(x => x.FailureReason).HasMaxLength(2000);
        entity.Property(x => x.EngineBuild).HasMaxLength(64);
        entity.Property(x => x.Revision).IsConcurrencyToken();
        entity.HasOne<SimulationBatch>().WithMany().HasForeignKey(x => x.ReplayOfBatchId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(x => new { x.Status, x.CreatedAtUtc }).HasDatabaseName("ix_batches_dispatch");
        entity.ToTable(t =>
        {
            t.HasCheckConstraint("ck_batch_status", "\"Status\" IN ('pending','running','completed','completed_with_failures','cancelled','timed_out','failed')");
            t.HasCheckConstraint("ck_batch_limits", "\"TimeLimitSeconds\" BETWEEN 1 AND 3600 AND \"Revision\" > 0 AND length(btrim(\"Name\")) > 0");
            t.HasCheckConstraint("ck_batch_manifest", "jsonb_typeof(\"SeedsJson\") = 'array' AND jsonb_array_length(\"SeedsJson\") BETWEEN 1 AND 25 AND jsonb_typeof(\"RequestJson\") = 'object'");
            t.HasCheckConstraint("ck_batch_time", "\"DeadlineAtUtc\" > \"CreatedAtUtc\" AND (\"FinishedAtUtc\" IS NULL OR \"FinishedAtUtc\" >= \"CreatedAtUtc\")");
            t.HasCheckConstraint("ck_batch_terminal", "(\"Status\" IN ('pending','running')) = (\"FinishedAtUtc\" IS NULL)");
        });
    }
}
internal sealed class BatchVariantConfiguration : IEntityTypeConfiguration<BatchVariant>
{
    public void Configure(EntityTypeBuilder<BatchVariant> entity)
    {
        Mapping.Base(entity, "batch_variants");
        entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
        entity.Property(x => x.InputSnapshotJson).HasColumnType("jsonb").IsRequired();
        entity.HasAlternateKey(x => new { x.BatchId, x.Id });
        entity.HasOne<SimulationBatch>().WithMany().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Scenario>().WithMany().HasForeignKey(x => x.ScenarioId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(x => new { x.BatchId, x.Position }).IsUnique();
        entity.HasIndex(x => new { x.BatchId, x.ScenarioId }).IsUnique();
        entity.ToTable(t => t.HasCheckConstraint("ck_variant_input", "\"Position\" BETWEEN 0 AND 4 AND \"ScenarioRevision\" > 0 AND jsonb_typeof(\"InputSnapshotJson\") = 'object'"));
    }
}
internal sealed class BatchReplicationConfiguration : IEntityTypeConfiguration<BatchReplication>
{
    public void Configure(EntityTypeBuilder<BatchReplication> entity)
    {
        Mapping.Base(entity, "batch_replications");
        entity.Property(x => x.Status).HasMaxLength(16).IsRequired();
        entity.HasOne<BatchVariant>().WithMany().HasForeignKey(x => new { x.BatchId, x.VariantId })
            .HasPrincipalKey(x => new { x.BatchId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<SimulationRun>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(x => x.RunId).IsUnique();
        entity.HasIndex(x => new { x.VariantId, x.Seed }).IsUnique();
        entity.HasIndex(x => new { x.VariantId, x.SeedIndex }).IsUnique();
        entity.HasIndex(x => new { x.RemoteCancellationPending, x.NextCancellationAttemptAtUtc }).HasDatabaseName("ix_batch_cancellations");
        entity.ToTable(t =>
        {
            t.HasCheckConstraint("ck_replication_status", "\"Status\" IN ('pending','running','completed','failed','cancelled','timed_out')");
            t.HasCheckConstraint("ck_replication_seed", "\"Seed\" >= 0 AND \"SeedIndex\" BETWEEN 0 AND 24");
        });
    }
}

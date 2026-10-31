using AeroSynapse.Domain.DigitalTwin;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AeroSynapse.Infrastructure.Storage;

internal sealed class AnomalyReviewConfiguration : IEntityTypeConfiguration<AnomalyReview>
{
    public void Configure(EntityTypeBuilder<AnomalyReview> entity)
    {
        Mapping.Base(entity, "anomaly_reviews");
        entity.Property(x => x.SettingsKey).HasMaxLength(64).IsRequired();
        entity.Property(x => x.SettingsJson).HasColumnType("jsonb").IsRequired();
        entity.Property(x => x.DetectorVersion).HasMaxLength(64).IsRequired();
        entity.Property(x => x.Decision).HasMaxLength(16).IsRequired();
        entity.Property(x => x.Actor).HasMaxLength(160).IsRequired();
        entity.Property(x => x.Reason).HasMaxLength(2000).IsRequired();
        entity.Property(x => x.EpisodeJson).HasColumnType("jsonb").IsRequired();
        entity.HasOne<SimulationRun>().WithMany().HasForeignKey(x => new { x.SceneId, x.RunId })
            .HasPrincipalKey(x => new { x.SceneId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<SimulationBatch>().WithMany().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(x => x.CommandId).IsUnique();
        entity.HasIndex(x => new { x.RunId, x.BatchId, x.SettingsKey, x.CheckpointId, x.OnsetMinute, x.RecordedAtUtc });
        entity.ToTable(t =>
        {
            t.HasCheckConstraint("ck_anomaly_review_decision", "\"Decision\" IN ('confirmed', 'rejected')");
            t.HasCheckConstraint("ck_anomaly_review_synthetic", "\"IsSynthetic\" AND \"OnsetMinute\" >= 0");
            t.HasCheckConstraint("ck_anomaly_review_actor", "length(btrim(\"Actor\")) > 0");
            t.HasCheckConstraint("ck_anomaly_review_json", "jsonb_typeof(\"SettingsJson\") = 'object' AND jsonb_typeof(\"EpisodeJson\") = 'object'");
        });
    }
}

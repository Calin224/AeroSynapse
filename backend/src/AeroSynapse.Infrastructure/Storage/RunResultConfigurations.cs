using AeroSynapse.Domain.DigitalTwin;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AeroSynapse.Infrastructure.Storage;

internal sealed class RunMetricConfiguration : IEntityTypeConfiguration<RunMetric>
{
    public void Configure(EntityTypeBuilder<RunMetric> entity)
    {
        Mapping.Base(entity, "run_metrics");
        entity.Property(x => x.ZoneType).HasMaxLength(16).IsRequired();
        entity.Property(x => x.Name).HasMaxLength(64).IsRequired();
        entity.Property(x => x.Unit).HasMaxLength(32).IsRequired();
        entity.Property(x => x.Value).HasPrecision(18, 6);
        entity.HasOne<SimulationRun>().WithMany().HasForeignKey(x => new { x.SceneId, x.RunId })
            .HasPrincipalKey(x => new { x.SceneId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(x => new { x.RunId, x.RecordedAtUtc, x.Id }).HasDatabaseName("ix_metrics_run_time");
        entity.HasIndex(x => new { x.RunId, x.ZoneType, x.ZoneId, x.RecordedAtUtc, x.Name }).IsUnique()
            .HasDatabaseName("ix_metrics_run_zone_time_name");
        entity.ToTable(t =>
        {
            t.HasCheckConstraint("ck_metric_zone", "\"ZoneType\" IN ('scene', 'terminal', 'checkpoint', 'gate', 'checkIn') AND \"ZoneId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
            t.HasCheckConstraint("ck_metric_text", "length(btrim(\"Name\")) > 0 AND length(btrim(\"Unit\")) > 0");
        });
    }
}

internal sealed class RunSnapshotConfiguration : IEntityTypeConfiguration<RunSnapshot>
{
    public void Configure(EntityTypeBuilder<RunSnapshot> entity)
    {
        Mapping.Base(entity, "run_snapshots");
        entity.Property(x => x.ZoneType).HasMaxLength(16).IsRequired();
        entity.Property(x => x.PayloadJson).HasColumnType("jsonb").IsRequired();
        entity.HasOne<SimulationRun>().WithMany().HasForeignKey(x => new { x.SceneId, x.RunId })
            .HasPrincipalKey(x => new { x.SceneId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(x => new { x.RunId, x.RecordedAtUtc, x.Id }).HasDatabaseName("ix_snapshots_run_time");
        entity.HasIndex(x => new { x.RunId, x.ZoneType, x.ZoneId, x.RecordedAtUtc }).IsUnique()
            .HasDatabaseName("ix_snapshots_run_zone_time");
        entity.ToTable(t =>
        {
            t.HasCheckConstraint("ck_snapshot_zone", "\"ZoneType\" IN ('scene', 'terminal', 'checkpoint', 'gate', 'checkIn') AND \"ZoneId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
            t.HasCheckConstraint("ck_snapshot_payload", "jsonb_typeof(\"PayloadJson\") = 'object'");
        });
    }
}

internal sealed class RunRecommendationConfiguration : IEntityTypeConfiguration<RunRecommendation>
{
    public void Configure(EntityTypeBuilder<RunRecommendation> entity)
    {
        Mapping.Base(entity, "run_recommendations");
        entity.Property(x => x.Objective).HasMaxLength(160).IsRequired();
        entity.Property(x => x.Rationale).HasMaxLength(2000).IsRequired();
        entity.Property(x => x.BaselineScore).HasPrecision(18, 6);
        entity.HasAlternateKey(x => new { x.SceneId, x.Id });
        entity.HasOne<SimulationRun>().WithMany().HasForeignKey(x => new { x.SceneId, x.BaselineRunId })
            .HasPrincipalKey(x => new { x.SceneId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(x => new { x.BaselineRunId, x.CreatedAtUtc, x.Id }).IsDescending(false, true, true)
            .HasDatabaseName("ix_recommendations_baseline_created");
        entity.ToTable(t => t.HasCheckConstraint("ck_recommendation_text", "length(btrim(\"Objective\")) > 0 AND length(btrim(\"Rationale\")) > 0"));
    }
}

internal sealed class RecommendationCandidateConfiguration : IEntityTypeConfiguration<RecommendationCandidate>
{
    public void Configure(EntityTypeBuilder<RecommendationCandidate> entity)
    {
        Mapping.Base(entity, "recommendation_candidates");
        entity.Property(x => x.Score).HasPrecision(18, 6);
        entity.Property(x => x.AssessmentJson).HasColumnType("jsonb").IsRequired();
        entity.HasOne<RunRecommendation>().WithMany().HasForeignKey(x => new { x.SceneId, x.RecommendationId })
            .HasPrincipalKey(x => new { x.SceneId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<SimulationRun>().WithMany().HasForeignKey(x => new { x.SceneId, x.RunId })
            .HasPrincipalKey(x => new { x.SceneId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(x => new { x.RecommendationId, x.RunId }).IsUnique();
        entity.HasIndex(x => new { x.RecommendationId, x.Rank }).IsUnique();
        entity.HasIndex(x => new { x.RunId, x.RecommendationId }).HasDatabaseName("ix_candidates_run_recommendation");
        entity.ToTable(t =>
        {
            t.HasCheckConstraint("ck_candidate_rank", "\"Rank\" > 0");
            t.HasCheckConstraint("ck_candidate_assessment", "jsonb_typeof(\"AssessmentJson\") = 'object'");
        });
    }
}

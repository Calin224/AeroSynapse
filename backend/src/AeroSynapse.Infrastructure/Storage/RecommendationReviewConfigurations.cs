using AeroSynapse.Domain.DigitalTwin;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AeroSynapse.Infrastructure.Storage;

internal sealed class RecommendationReviewConfiguration : IEntityTypeConfiguration<RecommendationReview>
{
    public void Configure(EntityTypeBuilder<RecommendationReview> entity)
    {
        Mapping.Base(entity, "recommendation_reviews");
        entity.Property(x => x.Status).HasMaxLength(16).IsRequired();
        entity.Property(x => x.ExplanationJson).HasColumnType("jsonb").IsRequired();
        entity.HasAlternateKey(x => new { x.SceneId, x.Id });
        entity.HasOne<RunRecommendation>().WithOne().HasForeignKey<RecommendationReview>(x => new { x.SceneId, x.Id })
            .HasPrincipalKey<RunRecommendation>(x => new { x.SceneId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<SimulationBatch>().WithMany().HasForeignKey(x => x.EvaluationId).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<Scenario>().WithMany().HasForeignKey(x => new { x.SceneId, x.ScenarioId })
            .HasPrincipalKey(x => new { x.SceneId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<SimulationRun>().WithMany().HasForeignKey(x => new { x.SceneId, x.CandidateRunId })
            .HasPrincipalKey(x => new { x.SceneId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(x => x.EvaluationId).IsUnique();
        entity.ToTable(t =>
        {
            t.HasCheckConstraint("ck_review_status", "\"Status\" IN ('pending', 'approved', 'rejected')");
            t.HasCheckConstraint("ck_review_synthetic", "\"IsSynthetic\" AND \"ScenarioRevision\" > 0 AND \"CandidateRunId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
            t.HasCheckConstraint("ck_review_explanation", "jsonb_typeof(\"ExplanationJson\") = 'object'");
        });
    }
}

internal sealed class RecommendationAuditConfiguration : IEntityTypeConfiguration<RecommendationAudit>
{
    public void Configure(EntityTypeBuilder<RecommendationAudit> entity)
    {
        Mapping.Base(entity, "recommendation_audit");
        entity.Property(x => x.Action).HasMaxLength(16).IsRequired();
        entity.Property(x => x.Actor).HasMaxLength(160).IsRequired();
        entity.Property(x => x.Reason).HasMaxLength(2000).IsRequired();
        entity.Property(x => x.BeforeJson).HasColumnType("jsonb").IsRequired();
        entity.Property(x => x.AfterJson).HasColumnType("jsonb").IsRequired();
        entity.HasOne<RecommendationReview>().WithMany().HasForeignKey(x => new { x.SceneId, x.RecommendationId })
            .HasPrincipalKey(x => new { x.SceneId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(x => x.CommandId).IsUnique();
        entity.HasIndex(x => new { x.RecommendationId, x.RecordedAtUtc });
        entity.HasIndex(x => x.RecommendationId).IsUnique().HasFilter("\"Action\" <> 'created'");
        entity.ToTable(t =>
        {
            t.HasCheckConstraint("ck_review_audit_action", "\"Action\" IN ('created', 'approved', 'rejected')");
            t.HasCheckConstraint("ck_review_audit_actor", "length(btrim(\"Actor\")) > 0");
        });
    }
}

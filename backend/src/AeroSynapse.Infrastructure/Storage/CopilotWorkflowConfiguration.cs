using AeroSynapse.Domain.DigitalTwin;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AeroSynapse.Infrastructure.Storage;

internal sealed class CopilotWorkflowConfiguration : IEntityTypeConfiguration<CopilotWorkflowRecord>
{
    public void Configure(EntityTypeBuilder<CopilotWorkflowRecord> entity)
    {
        entity.ToTable("copilot_workflows", table => {
            table.HasCheckConstraint("ck_workflow_status", "\"Status\" IN ('pending','running','completed','failed','cancelled','timed_out')");
            table.HasCheckConstraint("ck_workflow_scope", "\"Minute\" >= 0 AND \"Revision\" > 0 AND \"DeadlineAtUtc\" > \"CreatedAtUtc\"");
            table.HasCheckConstraint("ck_workflow_terminal", "(\"Status\" IN ('pending','running')) = (\"FinishedAtUtc\" IS NULL)");
        });
        entity.HasKey(w => w.Id); entity.Property(w => w.Id).ValueGeneratedNever();
        entity.Property(w => w.Status).HasMaxLength(16); entity.Property(w => w.PlaybookId).HasMaxLength(64);
        entity.Property(w => w.StopCode).HasMaxLength(128);
        entity.Property(w => w.ManifestHash).HasMaxLength(64); entity.Property(w => w.SourceHash).HasMaxLength(64);
        entity.Property(w => w.EngineBuild).HasMaxLength(64); entity.Property(w => w.EngineVersion).HasMaxLength(64);
        entity.Property(w => w.RequestJson).HasColumnType("jsonb"); entity.Property(w => w.DefinitionJson).HasColumnType("jsonb");
        entity.Property(w => w.StepsJson).HasColumnType("jsonb"); entity.Property(w => w.ConclusionJson).HasColumnType("jsonb");
        entity.HasIndex(w => new { w.Status, w.NextAttemptAtUtc }); entity.HasIndex(w => new { w.ScenarioId, w.RunId, w.CreatedAtUtc });
        entity.HasOne<SimulationRun>().WithMany().HasForeignKey(w => new { w.SceneId, w.RunId })
            .HasPrincipalKey(w => new { w.SceneId, w.Id }).OnDelete(DeleteBehavior.Cascade);
    }
}

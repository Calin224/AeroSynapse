using AeroSynapse.Domain.DigitalTwin;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AeroSynapse.Infrastructure.Storage;

internal sealed class CopilotTranscriptConfiguration : IEntityTypeConfiguration<CopilotTranscriptRecord>
{
    public void Configure(EntityTypeBuilder<CopilotTranscriptRecord> entity)
    {
        entity.ToTable("copilot_transcripts", table =>
        {
            table.HasCheckConstraint("ck_transcript_retention", "\"ExpiresAtUtc\" > \"CreatedAtUtc\"");
            table.HasCheckConstraint("ck_transcript_redaction", "NOT \"Redacted\" OR \"Question\" IS NULL");
            table.HasCheckConstraint("ck_transcript_minute", "\"Minute\" >= 0");
        });
        entity.HasKey(x => x.Id); entity.Property(x => x.Id).ValueGeneratedNever();
        entity.Property(x => x.Kind).HasMaxLength(64);
        entity.Property(x => x.Question).HasMaxLength(1000);
        entity.Property(x => x.ExplanationJson).HasColumnType("jsonb");
        entity.HasIndex(x => new { x.ScenarioId, x.RunId, x.CreatedAtUtc });
        entity.HasIndex(x => x.ExpiresAtUtc);
        entity.HasOne<SimulationRun>().WithMany().HasForeignKey(x => new { x.SceneId, x.RunId })
            .HasPrincipalKey(x => new { x.SceneId, x.Id }).OnDelete(DeleteBehavior.Cascade);
    }
}

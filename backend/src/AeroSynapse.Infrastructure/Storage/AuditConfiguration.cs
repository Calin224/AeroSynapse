using AeroSynapse.Domain.DigitalTwin;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AeroSynapse.Infrastructure.Storage;

internal sealed class AuditConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> entity)
    {
        entity.ToTable("audit_events");
        entity.HasKey(row => row.Sequence);
        entity.Property(row => row.Sequence).UseIdentityAlwaysColumn();
        entity.HasIndex(row => row.Id).IsUnique();
        entity.Property(row => row.ActorId).HasMaxLength(256);
        entity.Property(row => row.Actor).HasMaxLength(256);
        entity.Property(row => row.CorrelationId).HasMaxLength(64);
        entity.Property(row => row.Action).HasMaxLength(100);
        entity.Property(row => row.ResourceType).HasMaxLength(64);
        entity.Property(row => row.ChangesJson).HasColumnType("jsonb");
        entity.HasIndex(row => new { row.ScenarioId, row.Sequence });
        entity.HasIndex(row => new { row.RunId, row.Sequence });
        entity.HasIndex(row => new { row.RecommendationId, row.Sequence });
        entity.HasIndex(row => new { row.CorrelationId, row.Sequence });
        entity.HasIndex(row => row.RecordedAtUtc);
    }
}

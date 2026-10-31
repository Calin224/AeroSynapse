using AeroSynapse.Domain.DigitalTwin;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AeroSynapse.Infrastructure.Storage;

internal sealed class ExternalDataConfiguration : IEntityTypeConfiguration<ExternalDataSnapshot>
{
    public void Configure(EntityTypeBuilder<ExternalDataSnapshot> entity)
    {
        entity.ToTable("external_data_snapshots");
        entity.HasKey(row => row.Id);
        entity.Property(row => row.ProviderId).HasMaxLength(32);
        entity.Property(row => row.ContentHash).HasMaxLength(64);
        entity.Property(row => row.PayloadJson).HasColumnType("jsonb");
        entity.HasIndex(row => new { row.ScenarioId, row.RetrievedAtUtc });
        // Evidence must survive a permitted scenario purge, like the operational audit.
    }
}

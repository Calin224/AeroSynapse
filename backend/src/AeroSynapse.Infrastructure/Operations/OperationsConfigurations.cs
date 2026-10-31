using AeroSynapse.Domain.DigitalTwin;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AeroSynapse.Infrastructure.Operations;

internal sealed class OperationsCursorConfiguration : IEntityTypeConfiguration<OperationsCursor>
{
    public void Configure(EntityTypeBuilder<OperationsCursor> entity)
    {
        entity.ToTable("operations_cursors");
        entity.HasKey(x => x.ScenarioId);
        entity.Property(x => x.ScenarioId).ValueGeneratedNever();
        entity.HasOne<Scenario>().WithMany().HasForeignKey(x => x.ScenarioId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class OperationsEventConfiguration : IEntityTypeConfiguration<OperationsEvent>
{
    public void Configure(EntityTypeBuilder<OperationsEvent> entity)
    {
        entity.ToTable("operations_events");
        entity.HasKey(x => new { x.ScenarioId, x.Sequence });
        entity.Property(x => x.Type).HasMaxLength(32).IsRequired();
        entity.Property(x => x.PayloadJson).HasColumnType("jsonb").IsRequired();
        entity.HasOne<Scenario>().WithMany().HasForeignKey(x => x.ScenarioId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class RunProgressConfiguration : IEntityTypeConfiguration<RunProgress>
{
    public void Configure(EntityTypeBuilder<RunProgress> entity)
    {
        entity.ToTable("run_progress", table =>
        {
            table.HasCheckConstraint("ck_progress_minute", "\"SimulationMinute\" >= 0 AND \"SimulationMinute\" <= 10080");
            table.HasCheckConstraint("ck_progress_zones", "jsonb_typeof(\"ZonesJson\") = 'array' AND jsonb_array_length(\"ZonesJson\") <= 1000");
        });
        entity.HasKey(x => x.RunId);
        entity.Property(x => x.RunId).ValueGeneratedNever();
        entity.Property(x => x.ZonesJson).HasColumnType("jsonb").IsRequired();
        entity.HasOne<SimulationRun>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
    }
}
